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
    public void AssignMissingColors_GivesDistinctStableColors()
    {
        var s = new AppSettings();
        BatteryDevice[] devices =
        [
            new() { Id = "bt:1", Name = "A", Kind = DeviceKind.Mouse, Source = "t" },
            new() { Id = "bt:2", Name = "B", Kind = DeviceKind.Headphones, Source = "t" },
            new() { Id = "system:battery", Name = "PC", Kind = DeviceKind.Computer, Source = "t" },
        ];

        Assert.True(s.AssignMissingColors(devices));
        Assert.False(s.AssignMissingColors(devices)); // ya tienen color: nada cambia

        Assert.Equal("blue", s.DeviceColors["bt:1"]);
        Assert.Equal("purple", s.DeviceColors["bt:2"]);
        Assert.Equal(DeviceColors.None, s.DeviceColors["system:battery"]);
        Assert.Equal("blue", s.DeviceColor(devices[0]));
        Assert.Null(s.DeviceColor(devices[2]));

        s.ColorPerDevice = false;
        Assert.Null(s.DeviceColor(devices[0]));
    }

    [Fact]
    public void NextFree_ReusesPaletteWhenFull()
    {
        Assert.Equal("orange", DeviceColors.NextFree(["blue", "purple"]));
        Assert.Contains(DeviceColors.NextFree(DeviceColors.Palette), DeviceColors.Palette);
    }

    [Fact]
    public void Normalize_DropsUnknownColors()
    {
        var s = new AppSettings
        {
            DeviceColors = new() { ["bt:1"] = "blue", ["bt:2"] = "javascript:alert(1)", ["bt:3"] = DeviceColors.None },
        };

        s.Normalize();

        Assert.Equal(2, s.DeviceColors.Count);
        Assert.False(s.DeviceColors.ContainsKey("bt:2"));
    }

    [Fact]
    public void Placement_DefaultsToNotificationArea_AndUnknownValuesAreReset()
    {
        var s = new AppSettings();
        Assert.Equal(DisplayPlacement.NotificationArea, s.Placement);

        s.Placement = (DisplayPlacement)42;
        s.Normalize();
        Assert.Equal(DisplayPlacement.NotificationArea, s.Placement);

        s.Placement = DisplayPlacement.Taskbar;
        s.Normalize();
        Assert.Equal(DisplayPlacement.Taskbar, s.Placement);
    }

    [Fact]
    public void Placement_RoundTripsThroughJson()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bt-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "settings.json");
        try
        {
            JsonStore.SaveSettings(path, new AppSettings { Placement = DisplayPlacement.Taskbar });
            Assert.Contains("\"Taskbar\"", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal(DisplayPlacement.Taskbar, JsonStore.LoadSettings(path).Placement);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Defaults_UseWindowsBatteryStyleWithColors()
    {
        var s = new AppSettings();
        Assert.Equal(IconStyle.WindowsBattery, s.IconStyle);
        Assert.True(s.ColorPerDevice);
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
