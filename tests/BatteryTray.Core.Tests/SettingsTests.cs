using BatteryTray.Core;
using BatteryTray.Core.Settings;

namespace BatteryTray.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "BatteryTrayTests", Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Normalize_ClampsAndFiltersUntrustedValues()
    {
        var s = new AppSettings
        {
            Language = "klingon",
            LowBatteryThreshold = 999,
            TrayMode = (TrayMode)42,
            HiddenDevices = ["bt:0C8A3B123456", "bt:0C8A3B123456", "../../etc", new string('a', 100)],
            Aliases = new() { ["bt:0C8A3B123456"] = "  Mi‮ ratón ", ["bad id!"] = "x", ["bt:1"] = "   " },
        };

        s.Normalize();

        Assert.Equal("auto", s.Language);
        Assert.Equal(AppSettings.MaxThreshold, s.LowBatteryThreshold);
        Assert.Equal(TrayMode.PerDevice, s.TrayMode);
        Assert.Equal(["bt:0C8A3B123456"], s.HiddenDevices);
        Assert.Single(s.Aliases);
        Assert.Equal("Mi ratón", s.Aliases["bt:0C8A3B123456"]);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        string path = Path.Combine(_dir, "settings.json");
        var s = new AppSettings { Language = "es", IconStyle = IconStyle.DeviceIcon, LowBatteryThreshold = 15 };
        s.TrayIconOverrides["system:battery"] = true;

        JsonStore.SaveSettings(path, s);
        AppSettings loaded = JsonStore.LoadSettings(path);

        Assert.Equal("es", loaded.Language);
        Assert.Equal(IconStyle.DeviceIcon, loaded.IconStyle);
        Assert.Equal(15, loaded.LowBatteryThreshold);
        Assert.True(loaded.TrayIconOverrides["system:battery"]);
        Assert.Contains("\"iconStyle\": \"DeviceIcon\"", File.ReadAllText(path));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultsAndKeepsBackup()
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ esto no es json");

        AppSettings loaded = JsonStore.LoadSettings(path);

        Assert.Equal(20, loaded.LowBatteryThreshold);
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Load_OversizedFile_IsIgnored()
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{\"language\":\"es\"," + new string(' ', (int)JsonStore.MaxFileBytes) + "}");

        Assert.Equal("auto", JsonStore.LoadSettings(path).Language);
    }

    [Fact]
    public void Cache_DropsInvalidEntries()
    {
        string path = Path.Combine(_dir, "cache.json");
        File.WriteAllText(path, """
            {
              "bt:0C8A3B123456": { "name": "Buds", "kind": "Headphones", "level": 40, "timestamp": "2026-11-10T10:00:00+00:00" },
              "bt:AAAAAAAAAAAA": { "name": "Bad", "kind": "Mouse", "level": 140, "timestamp": "2026-11-10T10:00:00+00:00" },
              "no valid": { "name": "Bad", "kind": "Mouse", "level": 10, "timestamp": "2026-11-10T10:00:00+00:00" }
            }
            """);

        Dictionary<string, CachedReading> cache = JsonStore.LoadCache(path);

        Assert.Single(cache);
        Assert.Equal(40, cache["bt:0C8A3B123456"].Level);
    }

    [Fact]
    public void HasTrayIcon_DefaultsOffForComputerBattery()
    {
        var s = new AppSettings();
        var pc = new BatteryDevice { Id = "system:battery", Name = "PC", Kind = DeviceKind.Computer, Source = "t" };
        var mouse = new BatteryDevice { Id = "bt:1", Name = "M", Kind = DeviceKind.Mouse, Source = "t" };

        Assert.False(s.HasTrayIcon(pc));
        Assert.True(s.HasTrayIcon(mouse));

        s.TrayIconOverrides[pc.Id] = true;
        Assert.True(s.HasTrayIcon(pc));
    }
}
