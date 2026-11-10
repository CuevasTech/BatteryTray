using BatteryTray.Core;
using Windows.System.Power;

namespace BatteryTray.Windows.Providers;

/// <summary>Batería del propio equipo (portátiles y tablets). En un sobremesa no aparece nada.</summary>
public sealed class SystemBatteryProvider : IBatteryProvider
{
    public const string DeviceId = "system:battery";

    private readonly string _displayName;
    private readonly Lock _gate = new();
    private IReadOnlyList<BatteryDevice> _devices = [];
    private bool _started;

    public SystemBatteryProvider(string displayName) => _displayName = displayName;

    public string Name => "Batería del equipo";

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
        if (!_started)
        {
            PowerManager.RemainingChargePercentChanged += OnPowerChanged;
            PowerManager.BatteryStatusChanged += OnPowerChanged;
            PowerManager.PowerSupplyStatusChanged += OnPowerChanged;
            _started = true;
        }

        Update();
        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Update();
        return Task.CompletedTask;
    }

    public Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(
            $"Estado: {PowerManager.BatteryStatus} | Carga: {PowerManager.RemainingChargePercent}% | Alimentación: {PowerManager.PowerSupplyStatus}");

    private void OnPowerChanged(object? sender, object e) => Update();

    private void Update()
    {
        IReadOnlyList<BatteryDevice> devices;
        BatteryStatus status = PowerManager.BatteryStatus;
        if (status == BatteryStatus.NotPresent)
        {
            devices = [];
        }
        else
        {
            int level = Math.Clamp(PowerManager.RemainingChargePercent, 0, 100);
            devices =
            [
                new BatteryDevice
                {
                    Id = DeviceId,
                    Name = _displayName,
                    Kind = DeviceKind.Computer,
                    Level = level,
                    IsConnected = true,
                    IsCharging = status == BatteryStatus.Charging,
                    Source = Name,
                    LastUpdated = DateTimeOffset.UtcNow,
                },
            ];
        }

        lock (_gate)
        {
            _devices = devices;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        if (_started)
        {
            PowerManager.RemainingChargePercentChanged -= OnPowerChanged;
            PowerManager.BatteryStatusChanged -= OnPowerChanged;
            PowerManager.PowerSupplyStatusChanged -= OnPowerChanged;
            _started = false;
        }

        return ValueTask.CompletedTask;
    }
}
