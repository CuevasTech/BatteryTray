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

    /// <summary>Anillo con el color del dispositivo (el mismo que su batería en la bandeja).</summary>
    public required Brush AccentBrush { get; init; }

    public Thickness AccentThickness { get; init; }

    /// <summary>Pincel del color de un dispositivo para el tema actual de la app (null si no tiene color).</summary>
    internal static Brush? ColorBrush(string? colorKey)
    {
        bool dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        return DeviceColors.Rgb(colorKey, dark) is uint rgb
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb))
            : null;
    }

    internal static DeviceItem From(BatteryDevice d, AppSettings s)
    {
        Brush? accent = ColorBrush(s.DeviceColor(d));
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
            // La barra lleva el color del dispositivo (como su batería en la bandeja);
            // en rojo si está baja. Sin color, los de Windows.
            BarBrush = !live ? Brush("TextFillColorTertiaryBrush")
                : band == LevelBand.Critical && !d.IsCharging ? Brush("SystemFillColorCriticalBrush")
                : accent ?? Brush(d.IsCharging ? "SystemFillColorSuccessBrush"
                    : band == LevelBand.Warning ? "SystemFillColorCautionBrush"
                    : "AccentFillColorDefaultBrush"),
            IconBrush = Brush(live && band == LevelBand.Critical && !d.IsCharging ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush"),
            ContentOpacity = live ? 1.0 : 0.6,
            AccentBrush = accent ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            AccentThickness = new Thickness(accent is null ? 0 : 2),
        };
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
