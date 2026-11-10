using BatteryTray.Core;
using BatteryTray.Core.Providers;
using BatteryTray.Core.Settings;

namespace BatteryTray.Core.Tests;

internal sealed class FakeProvider(string name) : IBatteryProvider
{
    public string Name { get; } = name;

    public IReadOnlyList<BatteryDevice> Devices { get; private set; } = [];

    public event EventHandler? Changed;

    public bool ThrowOnStart { get; set; }

    public void Set(params BatteryDevice[] devices)
    {
        Devices = devices;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        ThrowOnStart ? throw new InvalidOperationException("boom") : Task.CompletedTask;

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken = default) => Task.FromResult("ok");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class MonitorTests
{
    private static BatteryDevice Dev(string id, int? level, bool connected = true, DeviceKind kind = DeviceKind.Mouse, string? name = null) =>
        new() { Id = id, Name = name ?? id, Kind = kind, Level = level, IsConnected = connected, Source = "fake", LastUpdated = DateTimeOffset.UtcNow };

    [Fact]
    public async Task BrokenProvider_DoesNotBreakOthers()
    {
        var broken = new FakeProvider("broken") { ThrowOnStart = true };
        var ok = new FakeProvider("ok");
        await using var monitor = new BatteryMonitor([broken, ok]);

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        ok.Set(Dev("bt:1", 50));

        Assert.Single(monitor.Devices);
    }

    [Fact]
    public async Task DevicesWithoutAnyLevel_AreHidden()
    {
        var p = new FakeProvider("p");
        await using var monitor = new BatteryMonitor([p]);

        p.Set(Dev("bt:phone", null, kind: DeviceKind.Phone), Dev("bt:mouse", 40));

        Assert.Equal(["bt:mouse"], monitor.Devices.Select(d => d.Id));
    }

    [Fact]
    public async Task DisconnectedDevice_ShowsLastKnownLevel()
    {
        var p = new FakeProvider("p");
        await using var monitor = new BatteryMonitor([p]);

        p.Set(Dev("bt:1", 64));
        p.Set(Dev("bt:1", null, connected: false));

        BatteryDevice d = Assert.Single(monitor.Devices);
        Assert.Equal(64, d.Level);
        Assert.True(d.IsStale);
        Assert.False(d.IsConnected);
    }

    [Fact]
    public async Task InitialCache_IsUsedAfterRestart()
    {
        var p = new FakeProvider("p");
        var cache = new Dictionary<string, CachedReading>
        {
            ["bt:1"] = new() { Name = "Buds", Kind = DeviceKind.Headphones, Level = 33, Timestamp = DateTimeOffset.UtcNow.AddHours(-3) },
        };
        await using var monitor = new BatteryMonitor([p], cache);

        p.Set(Dev("bt:1", null, connected: false, name: "Buds"));

        Assert.Equal(33, Assert.Single(monitor.Devices).Level);
    }

    [Fact]
    public async Task Forget_RemovesCachedDevice()
    {
        var p = new FakeProvider("p");
        await using var monitor = new BatteryMonitor([p]);
        p.Set(Dev("bt:1", 64));
        p.Set(Dev("bt:1", null, connected: false));

        monitor.Forget("bt:1");

        Assert.Empty(monitor.Devices);
    }

    [Fact]
    public async Task SameDeviceFromTwoProviders_PrefersOneWithLevel()
    {
        var a = new FakeProvider("a");
        var b = new FakeProvider("b");
        await using var monitor = new BatteryMonitor([a, b]);

        a.Set(Dev("bt:1", null));
        b.Set(Dev("bt:1", 77));

        Assert.Equal(77, Assert.Single(monitor.Devices).Level);
    }

    [Fact]
    public async Task Ordering_ConnectedFirst_ComputerLast()
    {
        var p = new FakeProvider("p");
        await using var monitor = new BatteryMonitor([p]);

        p.Set(
            Dev("system:battery", 90, kind: DeviceKind.Computer, name: "Este equipo"),
            Dev("bt:z", 50, name: "Zeta"),
            Dev("bt:a", 50, name: "Alfa"),
            Dev("bt:off", 10, connected: false, name: "Apagado") with { IsStale = true });

        Assert.Equal(["bt:a", "bt:z", "system:battery", "bt:off"], monitor.Devices.Select(d => d.Id));
    }

    [Fact]
    public async Task DevicesChanged_NotRaisedWhenOnlyTimestampChanges()
    {
        var p = new FakeProvider("p");
        await using var monitor = new BatteryMonitor([p]);
        int raised = 0;
        monitor.DevicesChanged += (_, _) => raised++;

        p.Set(Dev("bt:1", 50));
        p.Set(Dev("bt:1", 50) with { LastUpdated = DateTimeOffset.UtcNow.AddSeconds(30) });

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Cache_IsMarkedDirtyOnNewReading()
    {
        var p = new FakeProvider("p");
        await using var monitor = new BatteryMonitor([p]);

        p.Set(Dev("bt:1", 50));
        IReadOnlyDictionary<string, CachedReading> snap = monitor.GetCacheSnapshot(out bool dirty);
        monitor.GetCacheSnapshot(out bool dirtyAgain);

        Assert.True(dirty);
        Assert.False(dirtyAgain);
        Assert.Equal(50, snap["bt:1"].Level);
    }
}

public class AlertTrackerTests
{
    private static BatteryDevice Dev(int level, bool charging = false, bool connected = true) =>
        new() { Id = "bt:1", Name = "Mouse", Level = level, IsCharging = charging, IsConnected = connected, Source = "t" };

    [Fact]
    public void AlertsOncePerDischarge()
    {
        var t = new LowBatteryAlertTracker();

        Assert.Empty(t.Evaluate([Dev(25)], 20));
        Assert.Single(t.Evaluate([Dev(20)], 20));
        Assert.Empty(t.Evaluate([Dev(19)], 20));
        Assert.Empty(t.Evaluate([Dev(22)], 20)); // oscila, dentro del margen: no re-arma
        Assert.Empty(t.Evaluate([Dev(18)], 20));
    }

    [Fact]
    public void RearmsAfterCharging()
    {
        var t = new LowBatteryAlertTracker();
        Assert.Single(t.Evaluate([Dev(10)], 20));
        Assert.Empty(t.Evaluate([Dev(12, charging: true)], 20));
        Assert.Single(t.Evaluate([Dev(15)], 20));
    }

    [Fact]
    public void IgnoresDisconnectedAndStale()
    {
        var t = new LowBatteryAlertTracker();
        Assert.Empty(t.Evaluate([Dev(5, connected: false)], 20));
        Assert.Empty(t.Evaluate([Dev(5) with { IsStale = true }], 20));
    }

    [Fact]
    public void ConnectionTracker_IgnoresFirstSnapshot()
    {
        var t = new ConnectionChangeTracker();
        BatteryDevice on = Dev(50);

        Assert.Empty(t.Evaluate([on]));
        var changes = t.Evaluate([on with { IsConnected = false }]);

        Assert.Single(changes);
        Assert.False(changes[0].Connected);
    }
}

public class DemoProviderTests
{
    [Fact]
    public void Demo_HasFourDevicesAndMouseCrossesThreshold()
    {
        var demo = new DemoBatteryProvider();
        Assert.Equal(4, demo.Devices.Count);
        Assert.Single(demo.Devices, d => !d.IsConnected && d.IsStale);

        var tracker = new LowBatteryAlertTracker();
        int alerts = 0;
        for (int i = 0; i < 5; i++)
        {
            demo.Step();
            alerts += tracker.Evaluate(demo.Devices, 20).Count;
        }

        Assert.Equal(1, alerts);
    }

    [Fact]
    public void Demo_ChargingDeviceStopsAtFull()
    {
        var demo = new DemoBatteryProvider();
        for (int i = 0; i < 40; i++)
        {
            demo.Step();
        }

        BatteryDevice pad = demo.Devices.Single(d => d.Id == "demo:gamepad");
        Assert.InRange(pad.Level!.Value, 0, 100);
    }
}
