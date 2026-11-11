using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BatteryTray.Core;
using Windows.Gaming.Input;
using Windows.System.Power;

namespace BatteryTray.Windows.Providers;

/// <summary>
/// Mandos de Xbox (One, Series, Elite, Adaptive) por cable USB o con el adaptador
/// inalámbrico de Xbox, con la API oficial de Windows (Windows.Gaming.Input).
///
/// - Solo se usa RawGameController. La clase Gamepad no se toca nunca: al iniciarla, Windows
///   cambia de modo los mandos de PlayStation por Bluetooth y estropea juegos antiguos (SDL).
/// - Windows da el nivel en cuatro escalones: 100, 70, 40 o 10 %.
/// - Los mandos por Bluetooth LE ya los cubre el proveedor Bluetooth (con el porcentaje
///   exacto), así que aquí se omiten para no duplicarlos.
/// - Si Windows.Gaming.Input no da nada (p. ej. con la app en segundo plano), se usa XInput.
/// Todo es de solo lectura.
/// </summary>
public sealed partial class XboxControllerProvider : IBatteryProvider
{
    private const ushort Microsoft = 0x045E;
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(60);

    // Tras conectarse, el mando tarda unos segundos en enviar su primer estado.
    private static readonly TimeSpan s_grace = TimeSpan.FromSeconds(15);

    // Por Bluetooth LE (los cubre el proveedor Bluetooth).
    private static readonly HashSet<ushort> s_blePids = [0x0B13, 0x0B20, 0x0B22, 0x0B21];

    private static readonly Dictionary<ushort, string> s_names = new()
    {
        [0x02D1] = "Xbox One Controller",
        [0x02DD] = "Xbox One Controller",
        [0x02E3] = "Xbox Elite Controller",
        [0x02EA] = "Xbox One S Controller",
        [0x02E0] = "Xbox One S Controller",
        [0x02FD] = "Xbox One S Controller",
        [0x0B00] = "Xbox Elite Controller Series 2",
        [0x0B05] = "Xbox Elite Controller Series 2",
        [0x0B12] = "Xbox Wireless Controller",
        [0x0B0A] = "Xbox Adaptive Controller",
        [0x0B0C] = "Xbox Adaptive Controller",
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<RawGameController, DateTimeOffset> _controllers = [];
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<BatteryDevice> _devices = [];
    private Task? _loop;
    private bool _subscribed;
    private string _lastNote = "";

    public string Name => "Xbox";

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
        try
        {
            // Primero los avisos y luego la lista (la lista empieza vacía y se llena en uno o dos segundos).
            RawGameController.RawGameControllerAdded += OnAdded;
            RawGameController.RawGameControllerRemoved += OnRemoved;
            _subscribed = true;
            foreach (RawGameController c in RawGameController.RawGameControllers)
            {
                OnAdded(null, c);
            }
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException)
        {
            _lastNote = $"Windows.Gaming.Input no disponible: {ex.GetType().Name}";
        }

        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Poll();
        return Task.CompletedTask;
    }

    private void OnAdded(object? sender, RawGameController controller)
    {
        lock (_gate)
        {
            _controllers.TryAdd(controller, DateTimeOffset.UtcNow);
        }

        // El primer estado llega a los pocos segundos.
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Poll(), TaskScheduler.Default);
    }

    private void OnRemoved(object? sender, RawGameController controller)
    {
        lock (_gate)
        {
            _controllers.Remove(controller);
        }

        Poll();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Poll();
            try
            {
                await Task.Delay(s_pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Poll()
    {
        try
        {
            var list = new List<BatteryDevice>();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            KeyValuePair<RawGameController, DateTimeOffset>[] controllers;
            lock (_gate)
            {
                controllers = [.. _controllers];
            }

            foreach ((RawGameController c, DateTimeOffset added) in controllers)
            {
                if (Read(c, added, now) is BatteryDevice d)
                {
                    list.Add(d);
                }
            }

            // XInput como respaldo si Windows.Gaming.Input no dio ningún mando de Xbox con batería.
            if (list.Count == 0)
            {
                list.AddRange(ReadXInput(now));
            }

            Publish(list);
        }
        catch (Exception ex)
        {
            string note = $"{ex.GetType().Name}: {ex.Message}";
            if (note != _lastNote)
            {
                _lastNote = note;
                AppLog.Warn($"Mandos Xbox: {note}");
            }
        }
    }

    private static BatteryDevice? Read(RawGameController c, DateTimeOffset added, DateTimeOffset now)
    {
        ushort vid = c.HardwareVendorId;
        ushort pid = c.HardwareProductId;

        // Solo mandos de Xbox (o compatibles): los de PlayStation y Nintendo van por HID,
        // y los virtuales de Steam (28DE:11FF) no tienen batería propia.
        if (vid is 0x054C or 0x057E or 0x28DE || (vid == Microsoft && c.IsWireless && s_blePids.Contains(pid)))
        {
            return null;
        }

        global::Windows.Devices.Power.BatteryReport? report = c.TryGetBatteryReport();
        if (report is null || report.Status == BatteryStatus.NotPresent)
        {
            return null; // por cable y sin batería, o sin información
        }

        bool charging = report.Status == BatteryStatus.Charging;
        int? level = null;
        if (report.RemainingCapacityInMilliwattHours is int remaining && report.FullChargeCapacityInMilliwattHours is int full && full > 0)
        {
            level = Math.Clamp((int)Math.Round(100.0 * remaining / full), 0, 100);
        }

        if (level is null || (now - added < s_grace && !charging && level <= 10))
        {
            return null; // aún no ha enviado su estado real
        }

        string name = s_names.TryGetValue(pid, out string? known) && vid == Microsoft
            ? known
            : DeviceText.Sanitize(c.DisplayName, "Xbox Controller");
        return new BatteryDevice
        {
            Id = MakeId("wgi", c.NonRoamableId),
            Name = name,
            Kind = DeviceKind.Gamepad,
            Level = level,
            IsConnected = true,
            IsCharging = charging,
            Source = "Xbox (Windows.Gaming.Input)",
            LastUpdated = now,
        };
    }

    // ------------------------------------------------------------ XInput (respaldo)

    private const byte BatteryTypeAlkaline = 0x02;
    private const byte BatteryTypeNimh = 0x03;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [LibraryImport("xinput1_4.dll")]
    private static partial uint XInputGetBatteryInformation(uint userIndex, byte deviceType, out XINPUT_BATTERY_INFORMATION batteryInformation);

    private static IEnumerable<BatteryDevice> ReadXInput(DateTimeOffset now)
    {
        var result = new List<BatteryDevice>();
        for (uint slot = 0; slot < 4; slot++)
        {
            XINPUT_BATTERY_INFORMATION info;
            try
            {
                if (XInputGetBatteryInformation(slot, 0, out info) != 0)
                {
                    continue; // no hay mando en esa posición
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return result;
            }

            // Solo mandos inalámbricos con un tipo de batería conocido dan nivel.
            if (info.BatteryType is not (BatteryTypeAlkaline or BatteryTypeNimh))
            {
                continue;
            }

            result.Add(new BatteryDevice
            {
                Id = MakeId("xinput", slot.ToString(CultureInfo.InvariantCulture)),
                Name = "Xbox Controller",
                Kind = DeviceKind.Gamepad,
                Level = XInputLevel(info.BatteryLevel),
                IsConnected = true,
                Source = "Xbox (XInput)",
                LastUpdated = now,
            });
        }

        return result;
    }

    /// <summary>Los cuatro escalones de XInput, con los mismos valores que da Windows.Gaming.Input.</summary>
    internal static int XInputLevel(byte level) => level switch
    {
        0 => 10,
        1 => 40,
        2 => 70,
        _ => 100,
    };

    /// <summary>Id corto y estable; el identificador de Windows va resumido con SHA-256.</summary>
    internal static string MakeId(string kind, string identity)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"xbox:{kind}:{Convert.ToHexStringLower(hash.AsSpan(0, 6))}";
    }

    private void Publish(List<BatteryDevice> list)
    {
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        bool changed;
        lock (_gate)
        {
            changed = list.Count != _devices.Count
                || !list.Zip(_devices).All(p => p.First with { LastUpdated = null } == p.Second with { LastUpdated = null });
            _devices = list;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Mandos vistos por Windows.Gaming.Input: {_controllers.Count}");
            foreach (RawGameController c in _controllers.Keys)
            {
                string battery;
                try
                {
                    global::Windows.Devices.Power.BatteryReport? r = c.TryGetBatteryReport();
                    battery = r is null ? "sin informe" : $"{r.Status} {r.RemainingCapacityInMilliwattHours}/{r.FullChargeCapacityInMilliwattHours}";
                }
                catch (Exception ex)
                {
                    battery = ex.GetType().Name;
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"- {c.HardwareVendorId:X4}:{c.HardwareProductId:X4} inalámbrico={c.IsWireless} {battery}");
            }

            if (_lastNote.Length > 0)
            {
                sb.AppendLine(_lastNote);
            }
        }

        return Task.FromResult(sb.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscribed)
        {
            RawGameController.RawGameControllerAdded -= OnAdded;
            RawGameController.RawGameControllerRemoved -= OnRemoved;
        }

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
    }
}
