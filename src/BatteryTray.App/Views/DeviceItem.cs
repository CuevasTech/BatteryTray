using BatteryTray.Core;
using BatteryTray.Core.Settings;
using BatteryTray.Services;
using BatteryTray.Windows.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace BatteryTray.Views;

/// <summary>Lo que muestra cada fila del panel.</summary>
public sealed class DeviceItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Glyph { get; init; }

    public required string LevelText { get; init; }

    public required string StatusText { get; init; }

    public required string AccessibleName { get; init; }

    public double LevelValue { get; init; }

    public required Brush BarBrush { get; init; }

    public required Brush IconBrush { get; init; }

    public double ContentOpacity { get; init; } = 1.0;

    internal static DeviceItem From(BatteryDevice d, AppSettings s)
    {
        LevelBand band = d.GetBand(s.LowBatteryThreshold);
        string name = s.DisplayName(d);
        string level = d.Level is int l ? Loc.Format("Percent", l) : "—";

        var parts = new List<string>();
        if (!d.IsConnected || d.IsStale)
        {
            parts.Add(Loc.Get("StatusDisconnected"));
            if (d.LastUpdated is not null)
            {
                parts.Add(Loc.Format("StatusLastSeen", Loc.Relative(d.LastUpdated)));
            }
        }
        else
        {
            if (d.Kind != DeviceKind.Computer)
            {
                parts.Add(Loc.Get("StatusConnected"));
            }

            if (d.IsCharging)
            {
                parts.Add(Loc.Get("StatusCharging"));
            }
            else if (band == LevelBand.Critical)
            {
                parts.Add(Loc.Get("StatusLow"));
            }
        }

        string status = string.Join(" · ", parts);
        bool live = d.IsConnected && !d.IsStale;

        return new DeviceItem
        {
            Id = d.Id,
            Name = name,
            Glyph = DeviceGlyphs.For(d.Kind),
            LevelText = level,
            StatusText = status,
            AccessibleName = Loc.Format("A11yDevice", name, level, status),
            LevelValue = d.Level ?? 0,
            BarBrush = Brush(!live ? "TextFillColorTertiaryBrush"
                : d.IsCharging ? "SystemFillColorSuccessBrush"
                : band switch
                {
                    LevelBand.Critical => "SystemFillColorCriticalBrush",
                    LevelBand.Warning => "SystemFillColorCautionBrush",
                    _ => "AccentFillColorDefaultBrush",
                }),
            IconBrush = Brush(live && band == LevelBand.Critical && !d.IsCharging ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush"),
            ContentOpacity = live ? 1.0 : 0.6,
        };
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
