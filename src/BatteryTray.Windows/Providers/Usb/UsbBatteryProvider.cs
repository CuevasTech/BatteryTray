using System.Globalization;
using System.Text;
using BatteryTray.Core;
using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb;

/// <summary>
/// Batería de aparatos con receptor USB o cable (ratones, teclados, cascos, mandos...),
/// leída con los protocolos de cada fabricante o con el estándar HID.
///
/// Cómo trabaja:
/// - Busca las colecciones HID al arrancar y cada vez que se enchufa o desenchufa algo
///   (y cada pocos minutos por si se pierde un aviso).
/// - Cada minuto pregunta la batería a los aparatos que reconoce algún protocolo.
/// - Todo pasa en un hilo de fondo y cada operación tiene un tiempo máximo.
/// - Los aparatos vistos en esta sesión que dejan de responder o se desenchufan siguen
///   en la lista como desconectados (con su último nivel conocido).
/// </summary>
public sealed class UsbBatteryProvider : IBatteryProvider
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_rescanInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_rescanDelay = TimeSpan.FromSeconds(2);
    private const int MaxDevices = 64;

    private readonly IReadOnlyList<IHidBatteryProtocol> _protocols;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, BatteryDevice> _known = new(StringComparer.Ordinal);
    private IReadOnlyList<(HidDeviceGroup Group, IHidBatteryProtocol Protocol)> _supported = [];
    private IReadOnlyList<BatteryDevice> _devices = [];
    private DeviceInterfaceWatcher? _watcher;
    private Task? _loop;
    private DateTimeOffset _rescanAt = DateTimeOffset.MinValue;
    private int _collectionsSeen;
    private IReadOnlyList<string> _unrecognized = [];
    private string _lastError = "";
    private readonly Dictionary<string, string> _lastResults = new(StringComparer.Ordinal);

    public UsbBatteryProvider()
        : this(UsbProtocols.All)
    {
    }

    internal UsbBatteryProvider(IReadOnlyList<IHidBatteryProtocol> protocols) => _protocols = protocols;

    public string Name => "USB";

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

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // La primera búsqueda va en segundo plano para no retrasar el arranque de la app.
        _watcher = new DeviceInterfaceWatcher(HidNative.HidInterfaceClass);
        _watcher.Changed += () => RequestRescan(s_rescanDelay);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(rescan: true, cancellationToken).ConfigureAwait(false);
    }

    private void RequestRescan(TimeSpan delay)
    {
        lock (_gate)
        {
            DateTimeOffset at = DateTimeOffset.UtcNow + delay;
            if (_rescanAt == DateTimeOffset.MinValue || at < _rescanAt)
            {
                _rescanAt = at;
            }
        }

        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Ya había un aviso pendiente.
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        DateTimeOffset nextRescan = DateTimeOffset.MinValue;
        DateTimeOffset nextPoll = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset requested;
                lock (_gate)
                {
                    requested = _rescanAt;
                }

                bool rescan = now >= nextRescan || (requested != DateTimeOffset.MinValue && now >= requested);
                if (rescan || now >= nextPoll)
                {
                    if (rescan)
                    {
                        lock (_gate)
                        {
                            _rescanAt = DateTimeOffset.MinValue;
                        }

                        nextRescan = now + s_rescanInterval;
                    }

                    await RunAsync(rescan, ct).ConfigureAwait(false);
                    nextPoll = DateTimeOffset.UtcNow + s_pollInterval;
                }

                DateTimeOffset due = nextPoll < nextRescan ? nextPoll : nextRescan;
                lock (_gate)
                {
                    if (_rescanAt != DateTimeOffset.MinValue && _rescanAt < due)
                    {
                        due = _rescanAt;
                    }
                }

                TimeSpan wait = due - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordError("bucle", ex);
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync(bool rescan, CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => Run(rescan, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _work.Release();
        }
    }

    /// <summary>Busca aparatos (si toca) y lee sus baterías. Se ejecuta en un hilo de fondo.</summary>
    private void Run(bool rescan, CancellationToken ct)
    {
        if (rescan)
        {
            IReadOnlyList<HidCollection> collections = HidEnumerator.GetCollections();
            var supported = new List<(HidDeviceGroup, IHidBatteryProtocol)>();
            var unrecognized = new List<string>();
            foreach (HidDeviceGroup group in HidDeviceGroup.Group(collections))
            {
                IHidBatteryProtocol? protocol = _protocols.FirstOrDefault(p => SafeSupports(p, group));
                if (protocol is not null)
                {
                    supported.Add((group.WithNames(), protocol));
                }
                else if (group.Transport is HidTransport.Usb or HidTransport.Bluetooth or HidTransport.BluetoothLe)
                {
                    // Para el diagnóstico: fabricante, modelo y colecciones (sin números de serie),
                    // así se puede añadir soporte para aparatos que aún no se reconocen.
                    string pages = string.Join(" ", group.Collections.Select(c => $"{c.UsagePage:X4}/{c.Usage:X4}").Distinct(StringComparer.Ordinal).Take(12));
                    unrecognized.Add($"{group.VendorId:X4}:{group.ProductId:X4} {group.Transport} [{pages}]");
                }
            }

            lock (_gate)
            {
                _collectionsSeen = collections.Count;
                _supported = supported;
                _unrecognized = unrecognized;
            }
        }

        IReadOnlyList<(HidDeviceGroup Group, IHidBatteryProtocol Protocol)> targets;
        lock (_gate)
        {
            targets = _supported;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var current = new Dictionary<string, BatteryDevice>(StringComparer.Ordinal);
        foreach ((HidDeviceGroup group, IHidBatteryProtocol protocol) in targets)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<HidBatteryReading> readings;
            try
            {
                readings = protocol.Read(group, ct);
                SetResult(group, protocol, readings.Count == 0 ? "sin datos" : string.Join(", ", readings.Select(Describe)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetResult(group, protocol, $"error {ex.GetType().Name}");
                RecordError(protocol.Name, ex);
                continue;
            }

            foreach (HidBatteryReading r in readings)
            {
                if (current.Count >= MaxDevices)
                {
                    break;
                }

                string id = HidIds.Make(protocol.Name, r.Identity);
                current[id] = new BatteryDevice
                {
                    Id = id,
                    Name = DeviceText.Sanitize(r.Name, protocol.Name),
                    Kind = r.Kind,
                    Level = r.Level is int l ? Math.Clamp(l, 0, 100) : null,
                    IsConnected = r.Connected,
                    IsCharging = r.Charging && r.Connected,
                    Source = $"USB · {protocol.Name}",
                    LastUpdated = r.Level is null ? null : now,
                };
            }
        }

        Publish(current);
    }

    private void Publish(Dictionary<string, BatteryDevice> current)
    {
        bool changed;
        lock (_gate)
        {
            // Los que se vieron antes y ya no responden quedan como desconectados.
            foreach ((string id, BatteryDevice old) in _known)
            {
                if (!current.ContainsKey(id))
                {
                    current[id] = old with { IsConnected = false, IsCharging = false, Level = null, LastUpdated = null };
                }
            }

            _known.Clear();
            foreach ((string id, BatteryDevice d) in current)
            {
                _known[id] = d;
            }

            IReadOnlyList<BatteryDevice> list = current.Values.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
            changed = list.Count != _devices.Count || !list.SequenceEqual(_devices, SameReading.Instance);
            _devices = list;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool SafeSupports(IHidBatteryProtocol protocol, HidDeviceGroup group)
    {
        try
        {
            return protocol.Supports(group);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Describe(HidBatteryReading r) =>
        r.Level is int l ? $"{l}%{(r.Charging ? " cargando" : "")}" : r.Connected ? "sin nivel" : "desconectado";

    private void SetResult(HidDeviceGroup group, IHidBatteryProtocol protocol, string text)
    {
        lock (_gate)
        {
            _lastResults[$"{group.VendorId:X4}:{group.ProductId:X4} {protocol.Name}"] = text;
        }
    }

    private void RecordError(string where, Exception ex)
    {
        string message = $"{where}: {ex.GetType().Name}: {ex.Message}";
        lock (_gate)
        {
            if (message == _lastError)
            {
                return; // no repetir el mismo error cada minuto en el registro
            }

            _lastError = message;
        }

        AppLog.Warn($"USB {message}");
    }

    public Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Colecciones HID: {_collectionsSeen}; aparatos reconocidos: {_supported.Count}; aviso de cambios: {(_watcher?.IsActive == true ? "sí" : "no")}");
            foreach ((string device, string result) in _lastResults)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {device}: {result}");
            }

            if (_unrecognized.Count > 0)
            {
                sb.AppendLine("Sin reconocer:");
                foreach (string device in _unrecognized)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- {device}");
                }
            }

            if (_lastError.Length > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Último error: {_lastError}");
            }
        }

        return Task.FromResult(sb.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        _watcher = null;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        _wake.Dispose();
        _work.Dispose();
    }

    /// <summary>Compara lecturas sin contar la hora.</summary>
    private sealed class SameReading : IEqualityComparer<BatteryDevice>
    {
        public static readonly SameReading Instance = new();

        public bool Equals(BatteryDevice? x, BatteryDevice? y) =>
            x is not null && y is not null && x with { LastUpdated = null } == y with { LastUpdated = null };

        public int GetHashCode(BatteryDevice obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
