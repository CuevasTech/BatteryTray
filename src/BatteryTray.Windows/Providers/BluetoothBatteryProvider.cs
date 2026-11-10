using System.Globalization;
using System.Text;
using BatteryTray.Core;
using BatteryTray.Windows.Native;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace BatteryTray.Windows.Providers;

/// <summary>
/// Batería de dispositivos Bluetooth emparejados.
///
/// Fuentes, por orden de preferencia:
/// 1. La propiedad PnP DEVPKEY_Bluetooth_Battery que Windows rellena para auriculares
///    (perfil manos libres) y muchos ratones/teclados BLE. Es lo que enseña Configuración.
/// 2. Para dispositivos BLE conectados sin esa propiedad: el servicio GATT estándar de
///    batería (0x180F, característica 0x2A19), con notificaciones si el dispositivo las admite.
///
/// Nombre, tipo y estado de conexión vienen de las API WinRT de Bluetooth.
/// Todo es de solo lectura: la app nunca empareja, conecta ni escribe configuración
/// en los dispositivos (salvo suscribirse a notificaciones de batería por GATT).
/// </summary>
public sealed class BluetoothBatteryProvider : IBatteryProvider
{
    private static readonly string[] s_enumerators = ["BTHENUM", "BTHLE", "BTHLEDEVICE"];
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan s_gattReadInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_operationTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, ClassicEntry> _classic = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LeEntry> _le = new(StringComparer.Ordinal);
    private Dictionary<ulong, PnpBattery> _pnp = [];
    private IReadOnlyList<BatteryDevice> _devices = [];
    private DeviceWatcher? _classicWatcher;
    private DeviceWatcher? _leWatcher;
    private Task? _loop;
    private DateTimeOffset _nextScan = DateTimeOffset.MaxValue;
    private DateTimeOffset _nextPoll = DateTimeOffset.MinValue;
    private string _lastError = "";
    private int _pnpNodesSeen;

    public string Name => "Bluetooth";

    public IReadOnlyList<BatteryDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices;
            }
        }
    }

    public event EventHandler? Changed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await RescanAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _nextPoll = DateTimeOffset.UtcNow + s_pollInterval;
        }

        StartWatchers();
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => RescanAsync(cancellationToken, forceGattRead: true);

    // ---------------------------------------------------------------- bucle principal

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset due;
                lock (_gate)
                {
                    due = _nextScan < _nextPoll ? _nextScan : _nextPoll;
                }

                TimeSpan wait = due - now;
                if (wait > TimeSpan.Zero)
                {
                    // Espera hasta el siguiente sondeo o hasta que alguien pida un escaneo.
                    await _wake.WaitAsync(wait > s_pollInterval ? s_pollInterval : wait, ct).ConfigureAwait(false);
                    continue;
                }

                bool scan;
                lock (_gate)
                {
                    scan = _nextScan <= now;
                    if (scan)
                    {
                        _nextScan = DateTimeOffset.MaxValue;
                    }

                    _nextPoll = now + s_pollInterval;
                }

                if (scan)
                {
                    await RescanAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await PollAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Nunca dejar morir el bucle por un fallo puntual.
                RecordError("Bucle", ex);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
    }

    private void RequestScan(TimeSpan delay)
    {
        lock (_gate)
        {
            DateTimeOffset at = DateTimeOffset.UtcNow + delay;
            if (at < _nextScan)
            {
                _nextScan = at;
            }
        }

        Wake();
    }

    private void RequestPoll(TimeSpan delay)
    {
        lock (_gate)
        {
            DateTimeOffset at = DateTimeOffset.UtcNow + delay;
            if (at < _nextPoll)
            {
                _nextPoll = at;
            }
        }

        Wake();
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Ya estaba despierto.
        }
        catch (ObjectDisposedException)
        {
            // Cerrando.
        }
    }

    // ---------------------------------------------------------------- escaneo completo

    private async Task RescanAsync(CancellationToken ct, bool forceGattRead = false)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SyncClassicAsync(ct).ConfigureAwait(false);
            await SyncLeAsync(ct).ConfigureAwait(false);
            RefreshStates();
            ReadPnp();
            Publish();
            EnsureGatt(forceGattRead);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RefreshStates();
            ReadPnp();
            Publish();
            EnsureGatt(forceRead: false);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task SyncClassicAsync(CancellationToken ct)
    {
        IReadOnlyList<DeviceInformation> infos = await FindPairedAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (DeviceInformation info in infos)
        {
            seen.Add(info.Id);
            bool known;
            lock (_gate)
            {
                known = _classic.ContainsKey(info.Id);
            }

            if (known)
            {
                continue;
            }

            try
            {
                BluetoothDevice? device = await BluetoothDevice.FromIdAsync(info.Id).AsTask(ct).WaitAsync(s_operationTimeout, ct).ConfigureAwait(false);
                if (device is null || device.BluetoothAddress == 0)
                {
                    device?.Dispose();
                    continue;
                }

                var entry = new ClassicEntry(device);
                device.ConnectionStatusChanged += OnClassicConnectionChanged;
                device.NameChanged += OnNameChanged;
                lock (_gate)
                {
                    _classic[info.Id] = entry;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                RecordError("Bluetooth clásico", ex);
            }
        }

        RemoveMissing(_classic, seen, e =>
        {
            e.Device.ConnectionStatusChanged -= OnClassicConnectionChanged;
            e.Device.NameChanged -= OnNameChanged;
            e.Device.Dispose();
        });
    }

    private async Task SyncLeAsync(CancellationToken ct)
    {
        IReadOnlyList<DeviceInformation> infos = await FindPairedAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (DeviceInformation info in infos)
        {
            seen.Add(info.Id);
            bool known;
            lock (_gate)
            {
                known = _le.ContainsKey(info.Id);
            }

            if (known)
            {
                continue;
            }

            try
            {
                BluetoothLEDevice? device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(ct).WaitAsync(s_operationTimeout, ct).ConfigureAwait(false);
                if (device is null || device.BluetoothAddress == 0)
                {
                    device?.Dispose();
                    continue;
                }

                var entry = new LeEntry(device, info.Name);
                device.ConnectionStatusChanged += OnLeConnectionChanged;
                device.NameChanged += OnNameChanged;
                lock (_gate)
                {
                    _le[info.Id] = entry;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                RecordError("Bluetooth LE", ex);
            }
        }

        RemoveMissing(_le, seen, e =>
        {
            e.Device.ConnectionStatusChanged -= OnLeConnectionChanged;
            e.Device.NameChanged -= OnNameChanged;
            e.ReleaseGatt(OnGattValueChanged);
            e.Device.Dispose();
        });
    }

    private static async Task<IReadOnlyList<DeviceInformation>> FindPairedAsync(string selector, CancellationToken ct)
    {
        // Los selectores "FromPairingState" consultan "association endpoints" (AEP).
        DeviceInformationCollection result = await DeviceInformation
            .FindAllAsync(selector, null, DeviceInformationKind.AssociationEndpoint)
            .AsTask(ct)
            .WaitAsync(s_operationTimeout, ct)
            .ConfigureAwait(false);
        return result.ToList();
    }

    private void RemoveMissing<T>(Dictionary<string, T> map, HashSet<string> seen, Action<T> release)
    {
        List<T> removed = [];
        lock (_gate)
        {
            foreach (string id in map.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                removed.Add(map[id]);
                map.Remove(id);
            }
        }

        foreach (T entry in removed)
        {
            try
            {
                release(entry);
            }
            catch (Exception ex)
            {
                RecordError("Liberar dispositivo", ex);
            }
        }
    }

    // ---------------------------------------------------------------- estado

    private void RefreshStates()
    {
        lock (_gate)
        {
            foreach (ClassicEntry e in _classic.Values)
            {
                e.Refresh();
            }

            foreach (LeEntry e in _le.Values)
            {
                e.Refresh();
            }
        }
    }

    private void OnClassicConnectionChanged(BluetoothDevice sender, object args)
    {
        // El nivel de batería suele llegar unos segundos después de conectar.
        RequestScan(TimeSpan.FromSeconds(2));
        RequestPoll(TimeSpan.FromSeconds(10));
    }

    private void OnLeConnectionChanged(BluetoothLEDevice sender, object args)
    {
        try
        {
            if (sender.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                LeEntry? entry;
                lock (_gate)
                {
                    entry = _le.Values.FirstOrDefault(e => ReferenceEquals(e.Device, sender));
                }

                // Al desconectar se suelta la sesión GATT para no forzar a mantener la conexión.
                entry?.ReleaseGatt(OnGattValueChanged);
            }
        }
        catch (Exception ex)
        {
            RecordError("Cambio de conexión BLE", ex);
        }

        RequestScan(TimeSpan.FromSeconds(2));
        RequestPoll(TimeSpan.FromSeconds(10));
    }

    private void OnNameChanged(object sender, object args) => RequestPoll(TimeSpan.FromSeconds(1));

    // ---------------------------------------------------------------- PnP

    private void ReadPnp()
    {
        var found = new Dictionary<ulong, PnpBattery>();
        int nodes = 0;
        foreach (string enumerator in s_enumerators)
        {
            foreach (string instanceId in CfgMgr.GetPresentDeviceIds(enumerator))
            {
                nodes++;
                byte? battery = CfgMgr.GetByteProperty(instanceId, CfgMgr.BluetoothBattery);
                if (battery is not byte level || level > 100)
                {
                    continue;
                }

                if (!BluetoothIds.TryParseAddress(CfgMgr.GetStringProperty(instanceId, CfgMgr.BluetoothAddress), out ulong address)
                    && !BluetoothIds.TryParseAddressFromInstanceId(instanceId, out address))
                {
                    continue;
                }

                // Si hay varios nodos con batería para el mismo dispositivo, gana el primero
                // (BTHENUM va antes que BTHLE en la lista de enumeradores).
                found.TryAdd(address, new PnpBattery(instanceId, level));
            }
        }

        lock (_gate)
        {
            _pnp = found;
            _pnpNodesSeen = nodes;
        }
    }

    // ---------------------------------------------------------------- GATT

    private void EnsureGatt(bool forceRead)
    {
        List<LeEntry> candidates;
        lock (_gate)
        {
            candidates = _le.Values
                .Where(e => e.IsConnected && !_pnp.ContainsKey(e.Address) && !e.GattBusy)
                .ToList();
            foreach (LeEntry e in candidates)
            {
                e.GattBusy = true;
            }
        }

        foreach (LeEntry entry in candidates)
        {
            // En segundo plano: un dispositivo lento no debe frenar a los demás.
            _ = Task.Run(async () =>
            {
                try
                {
                    await UpdateGattAsync(entry, forceRead, _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    entry.GattStatus = $"error: {ex.GetType().Name}";
                    RecordError("GATT", ex);
                }
                finally
                {
                    entry.GattBusy = false;
                }
            });
        }
    }

    private async Task UpdateGattAsync(LeEntry entry, bool forceRead, CancellationToken ct)
    {
        if (entry.Characteristic is null)
        {
            if (entry.GattUnsupported)
            {
                return;
            }

            GattDeviceServicesResult services = await entry.Device
                .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached)
                .AsTask(ct).WaitAsync(s_operationTimeout, ct).ConfigureAwait(false);

            if (services.Status == GattCommunicationStatus.Success && services.Services.Count == 0)
            {
                // Sin caché todavía: se pregunta al dispositivo (ya está conectado).
                services = await entry.Device
                    .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
                    .AsTask(ct).WaitAsync(s_operationTimeout, ct).ConfigureAwait(false);
            }

            if (services.Status != GattCommunicationStatus.Success)
            {
                entry.GattStatus = $"servicios: {services.Status}";
                return;
            }

            GattDeviceService? service = services.Services.FirstOrDefault();
            foreach (GattDeviceService extra in services.Services.Skip(1))
            {
                extra.Dispose();
            }

            if (service is null)
            {
                entry.GattStatus = "sin servicio de batería";
                entry.GattUnsupported = true;
                return;
            }

            GattCharacteristicsResult chars = await service
                .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached)
                .AsTask(ct).WaitAsync(s_operationTimeout, ct).ConfigureAwait(false);

            GattCharacteristic? characteristic = chars.Status == GattCommunicationStatus.Success
                ? chars.Characteristics.FirstOrDefault()
                : null;

            if (characteristic is null)
            {
                entry.GattStatus = $"característica: {chars.Status}";
                service.Dispose();
                return;
            }

            entry.Attach(service, characteristic);

            if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                characteristic.ValueChanged += OnGattValueChanged;
                GattCommunicationStatus status = await characteristic
                    .WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    .AsTask(ct).WaitAsync(s_operationTimeout, ct).ConfigureAwait(false);
                entry.Notifying = status == GattCommunicationStatus.Success;
            }

            forceRead = true;
        }

        if (forceRead || DateTimeOffset.UtcNow >= entry.NextGattRead)
        {
            GattReadResult read = await entry.Characteristic!
                .ReadValueAsync(BluetoothCacheMode.Uncached)
                .AsTask(ct).WaitAsync(s_operationTimeout, ct).ConfigureAwait(false);

            entry.NextGattRead = DateTimeOffset.UtcNow + s_gattReadInterval;
            if (read.Status == GattCommunicationStatus.Success && ParseLevel(read.Value) is int level)
            {
                entry.SetGattLevel(level);
                entry.GattStatus = entry.Notifying ? "ok (notificaciones)" : "ok (lectura)";
                Publish();
            }
            else
            {
                entry.GattStatus = $"lectura: {read.Status}";
            }
        }
    }

    private void OnGattValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            if (ParseLevel(args.CharacteristicValue) is not int level)
            {
                return;
            }

            LeEntry? entry;
            lock (_gate)
            {
                entry = _le.Values.FirstOrDefault(e => ReferenceEquals(e.Characteristic, sender));
            }

            if (entry is not null)
            {
                entry.SetGattLevel(level);
                Publish();
            }
        }
        catch (Exception ex)
        {
            RecordError("Notificación GATT", ex);
        }
    }

    internal static int? ParseLevel(IBuffer? buffer)
    {
        if (buffer is null || buffer.Length < 1)
        {
            return null;
        }

        using var reader = DataReader.FromBuffer(buffer);
        byte value = reader.ReadByte();
        return value <= 100 ? value : null; // valores fuera de rango se descartan
    }

    // ---------------------------------------------------------------- publicar

    private void Publish()
    {
        List<BatteryDevice> list;
        lock (_gate)
        {
            var byAddress = new Dictionary<ulong, BatteryDevice>();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            foreach (ClassicEntry e in _classic.Values)
            {
                Add(byAddress, e.Address, e.Name, e.Kind, e.IsConnected, null, null, now);
            }

            foreach (LeEntry e in _le.Values)
            {
                Add(byAddress, e.Address, e.Name, e.Kind, e.IsConnected, e.IsConnected ? e.GattLevel : null, e.GattTime, now);
            }

            list = [.. byAddress.Values];
            _devices = list;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Add(Dictionary<ulong, BatteryDevice> map, ulong address, string name, DeviceKind kind, bool connected, int? gattLevel, DateTimeOffset? gattTime, DateTimeOffset now)
    {
        int? level = _pnp.TryGetValue(address, out PnpBattery? pnp) ? pnp.Level : gattLevel;
        DateTimeOffset? time = pnp is not null ? now : gattTime;

        if (map.TryGetValue(address, out BatteryDevice? existing))
        {
            // Mismo dispositivo emparejado como clásico y como LE: se combinan.
            map[address] = existing with
            {
                IsConnected = existing.IsConnected || connected,
                Level = existing.Level ?? level,
                Kind = existing.Kind == DeviceKind.Unknown ? kind : existing.Kind,
                IsStale = !(existing.IsConnected || connected),
                LastUpdated = existing.LastUpdated ?? time,
            };
            return;
        }

        map[address] = new BatteryDevice
        {
            Id = BluetoothIds.ToDeviceId(address),
            Name = name,
            Kind = kind,
            Level = level,
            IsConnected = connected,
            // Desconectado: lo que quede en la propiedad PnP es el último valor conocido.
            IsStale = !connected && level is not null,
            Source = pnp is not null ? "Bluetooth (Windows)" : "Bluetooth (GATT)",
            LastUpdated = time,
        };
    }

    // ---------------------------------------------------------------- watchers

    private void StartWatchers()
    {
        try
        {
            _classicWatcher = CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            _leWatcher = CreateWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        }
        catch (Exception ex)
        {
            RecordError("Vigilancia de dispositivos", ex);
        }
    }

    private DeviceWatcher CreateWatcher(string selector)
    {
        DeviceWatcher watcher = DeviceInformation.CreateWatcher(selector, null, DeviceInformationKind.AssociationEndpoint);
        watcher.Added += OnWatcherAddedOrRemoved;
        watcher.Removed += OnWatcherRemoved;
        watcher.Updated += OnWatcherUpdated;
        watcher.Start();
        return watcher;
    }

    private void OnWatcherAddedOrRemoved(DeviceWatcher sender, DeviceInformation args) => RequestScan(TimeSpan.FromSeconds(1));

    private void OnWatcherRemoved(DeviceWatcher sender, DeviceInformationUpdate args) => RequestScan(TimeSpan.FromSeconds(1));

    private void OnWatcherUpdated(DeviceWatcher sender, DeviceInformationUpdate args) => RequestPoll(TimeSpan.FromSeconds(1));

    private static void StopWatcher(DeviceWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
        catch (Exception)
        {
            // Cerrando: se ignora.
        }
    }

    // ---------------------------------------------------------------- diagnóstico

    private void RecordError(string where, Exception ex)
    {
        string message = $"{where}: {ex.GetType().Name}: {DeviceText.MaskAddressesInText(ex.Message)}";
        lock (_gate)
        {
            _lastError = message;
        }

        AppLog.Error(message);
    }

    public Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Emparejados clásicos: {_classic.Count}");
            foreach (ClassicEntry e in _classic.Values)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- [clásico] {e.Name} | {DeviceText.MaskAddress(e.Address)} | {(e.IsConnected ? "conectado" : "desconectado")} | CoD 0x{e.ClassOfDevice:X6} | {e.Kind}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"Emparejados BLE: {_le.Count}");
            foreach (LeEntry e in _le.Values)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- [BLE] {e.Name} | {DeviceText.MaskAddress(e.Address)} | {(e.IsConnected ? "conectado" : "desconectado")} | Appearance 0x{e.Appearance:X4} | {e.Kind} | GATT: {e.GattStatus ?? "-"}{(e.GattLevel is int l ? $" {l}%" : "")}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"Nodos PnP Bluetooth presentes: {_pnpNodesSeen}; con batería: {_pnp.Count}");
            foreach ((ulong address, PnpBattery pnp) in _pnp)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- {DeviceText.MaskAddress(address)} = {pnp.Level}% en {DeviceText.MaskAddressesInText(pnp.InstanceId)}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"Vigilancia: clásico={_classicWatcher?.Status.ToString() ?? "no"}, BLE={_leWatcher?.Status.ToString() ?? "no"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Último error: {(_lastError.Length == 0 ? "ninguno" : _lastError)}");
        }

        return Task.FromResult(sb.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        StopWatcher(_classicWatcher);
        StopWatcher(_leWatcher);

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cerrando.
            }
        }

        lock (_gate)
        {
            foreach (ClassicEntry e in _classic.Values)
            {
                e.Device.ConnectionStatusChanged -= OnClassicConnectionChanged;
                e.Device.NameChanged -= OnNameChanged;
                e.Device.Dispose();
            }

            foreach (LeEntry e in _le.Values)
            {
                if (e.Characteristic is not null)
                {
                    e.Characteristic.ValueChanged -= OnGattValueChanged;
                }

                e.Device.ConnectionStatusChanged -= OnLeConnectionChanged;
                e.Device.NameChanged -= OnNameChanged;
                e.ReleaseGatt(OnGattValueChanged);
                e.Device.Dispose();
            }

            _classic.Clear();
            _le.Clear();
        }

        _cts.Dispose();
    }

    // ---------------------------------------------------------------- tipos internos

    private sealed record PnpBattery(string InstanceId, int Level);

    private static string FallbackName(ulong address) =>
        "Bluetooth " + (address & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture);

    private sealed class ClassicEntry
    {
        public ClassicEntry(BluetoothDevice device)
        {
            Device = device;
            Address = device.BluetoothAddress;
            Refresh();
        }

        public BluetoothDevice Device { get; }

        public ulong Address { get; }

        public string Name { get; private set; } = "";

        public DeviceKind Kind { get; private set; }

        public bool IsConnected { get; private set; }

        public uint ClassOfDevice { get; private set; }

        public void Refresh()
        {
            Name = DeviceText.Sanitize(Device.Name, FallbackName(Address));
            IsConnected = Device.ConnectionStatus == BluetoothConnectionStatus.Connected;
            ClassOfDevice = Device.ClassOfDevice?.RawValue ?? 0;
            Kind = DeviceClassifier.FirstKnown(DeviceClassifier.FromClassOfDevice(ClassOfDevice), DeviceClassifier.FromName(Name));
        }
    }

    private sealed class LeEntry
    {
        private readonly string _infoName;

        public LeEntry(BluetoothLEDevice device, string infoName)
        {
            Device = device;
            Address = device.BluetoothAddress;
            _infoName = infoName;
            Refresh();
        }

        public BluetoothLEDevice Device { get; }

        public ulong Address { get; }

        public string Name { get; private set; } = "";

        public DeviceKind Kind { get; private set; }

        public bool IsConnected { get; private set; }

        public ushort Appearance { get; private set; }

        public GattDeviceService? Service { get; private set; }

        public GattCharacteristic? Characteristic { get; private set; }

        public volatile bool GattBusy;

        public bool GattUnsupported { get; set; }

        public bool Notifying { get; set; }

        public int? GattLevel { get; private set; }

        public DateTimeOffset? GattTime { get; private set; }

        public string? GattStatus { get; set; }

        public DateTimeOffset NextGattRead { get; set; } = DateTimeOffset.MinValue;

        public void Refresh()
        {
            string raw = string.IsNullOrWhiteSpace(Device.Name) ? _infoName : Device.Name;
            Name = DeviceText.Sanitize(raw, FallbackName(Address));
            IsConnected = Device.ConnectionStatus == BluetoothConnectionStatus.Connected;
            Appearance = Device.Appearance?.RawValue ?? 0;
            Kind = DeviceClassifier.FirstKnown(DeviceClassifier.FromAppearance(Appearance), DeviceClassifier.FromName(Name));
        }

        public void Attach(GattDeviceService service, GattCharacteristic characteristic)
        {
            Service = service;
            Characteristic = characteristic;
        }

        public void SetGattLevel(int level)
        {
            GattLevel = level;
            GattTime = DateTimeOffset.UtcNow;
        }

        public void ReleaseGatt(TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler)
        {
            GattCharacteristic? ch = Characteristic;
            GattDeviceService? svc = Service;
            Characteristic = null;
            Service = null;
            Notifying = false;
            GattUnsupported = false;
            NextGattRead = DateTimeOffset.MinValue;
            try
            {
                if (ch is not null)
                {
                    ch.ValueChanged -= handler;
                }

                // La característica se libera junto con su servicio.
                svc?.Dispose();
            }
            catch (Exception)
            {
                // El dispositivo ya no está; no pasa nada.
            }
        }
    }
}
