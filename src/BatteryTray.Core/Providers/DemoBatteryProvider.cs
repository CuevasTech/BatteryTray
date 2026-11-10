namespace BatteryTray.Core.Providers;

/// <summary>
/// Simula varios dispositivos para probar la interfaz sin hardware:
/// uno se descarga despacio, uno baja del umbral (para ver el aviso),
/// uno se está cargando y otro está desconectado.
/// </summary>
public sealed class DemoBatteryProvider : IBatteryProvider
{
    public static readonly TimeSpan DefaultTick = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private readonly TimeSpan _tick;
    private readonly Lock _gate = new();
    private readonly List<SimDevice> _sim;
    private ITimer? _timer;
    private IReadOnlyList<BatteryDevice> _devices = [];

    public DemoBatteryProvider(TimeProvider? time = null, TimeSpan? tick = null)
    {
        _time = time ?? TimeProvider.System;
        _tick = tick ?? DefaultTick;
        _sim =
        [
            new("demo:headphones", "WH-1000XM5", DeviceKind.Headphones, 82, Connected: true, Charging: false, Step: -1),
            new("demo:mouse", "MX Master 3S", DeviceKind.Mouse, 23, Connected: true, Charging: false, Step: -1),
            new("demo:gamepad", "Xbox Wireless Controller", DeviceKind.Gamepad, 64, Connected: true, Charging: true, Step: 3),
            new("demo:keyboard", "Keychron K3", DeviceKind.Keyboard, 57, Connected: false, Charging: false, Step: 0),
        ];
        Publish();
    }

    public string Name => "Demo";

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
        _timer ??= _time.CreateTimer(_ => Step(), null, _tick, _tick);
        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("Modo demostración activo: los dispositivos son simulados.");

    internal void Step()
    {
        lock (_gate)
        {
            for (int i = 0; i < _sim.Count; i++)
            {
                SimDevice d = _sim[i];
                if (!d.Connected || d.Level is not int level)
                {
                    continue;
                }

                if (d.Charging)
                {
                    level = Math.Min(100, level + Math.Max(1, Math.Abs(d.Step)));
                    // Al llenarse deja de cargar y empieza a gastarse.
                    _sim[i] = level >= 100
                        ? d with { Level = 100, Charging = false, Step = -1 }
                        : d with { Level = level };
                }
                else
                {
                    level = Math.Max(0, level + d.Step);
                    // Al llegar al 5 % "lo enchufamos".
                    _sim[i] = level <= 5
                        ? d with { Level = level, Charging = true, Step = 5 }
                        : d with { Level = level };
                }
            }
        }

        Publish();
    }

    private void Publish()
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            _devices = _sim.Select(d => new BatteryDevice
            {
                Id = d.Id,
                Name = d.Name,
                Kind = d.Kind,
                Level = d.Level,
                IsConnected = d.Connected,
                IsCharging = d.Charging,
                // Desconectado: se muestra como "último valor conocido" de hace un rato.
                IsStale = !d.Connected,
                Source = Name,
                LastUpdated = d.Connected ? now : now - TimeSpan.FromHours(2),
            }).ToList();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        _timer = null;
        return ValueTask.CompletedTask;
    }

    private sealed record SimDevice(string Id, string Name, DeviceKind Kind, int? Level, bool Connected, bool Charging, int Step);
}
