using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using BatteryTray.Core;
using BatteryTray.Core.Settings;

namespace BatteryTray.Windows.Rendering;

/// <summary>Qué hay que dibujar en un icono de la bandeja.</summary>
public sealed record TrayIconSpec(int? Level, LevelBand Band, bool Charging, DeviceKind Kind, IconStyle Style);

/// <summary>
/// Dibuja los iconos de la bandeja: el porcentaje (o el icono del dispositivo) con una
/// barra de nivel debajo. Los colores siguen el tema de la barra de tareas (claro u oscuro).
/// </summary>
public static class TrayIconRenderer
{
    private static readonly Lazy<FontFamily> s_textFamily = new(() => TryFamily("Segoe UI") ?? FontFamily.GenericSansSerif);
    private static readonly Lazy<FontFamily> s_glyphFamily = new(() => TryFamily("Segoe Fluent Icons") ?? TryFamily("Segoe MDL2 Assets") ?? FontFamily.GenericSansSerif);

    private sealed record Palette(Color Foreground, Color Track, Color Critical, Color Caution, Color Success);

    // Colores de la paleta de Windows 11 para texto y estados (crítico, precaución, correcto).
    private static readonly Palette s_dark = new(Color.White, Color.FromArgb(90, 255, 255, 255), Color.FromArgb(255, 255, 153, 164), Color.FromArgb(255, 252, 225, 0), Color.FromArgb(255, 108, 203, 95));
    private static readonly Palette s_light = new(Color.FromArgb(255, 27, 27, 27), Color.FromArgb(90, 0, 0, 0), Color.FromArgb(255, 196, 43, 28), Color.FromArgb(255, 157, 93, 0), Color.FromArgb(255, 15, 123, 15));

    /// <summary>Crea un HICON. Quien lo recibe debe liberarlo con DestroyIcon.</summary>
    public static nint RenderHicon(TrayIconSpec spec, int size, bool lightTaskbar)
    {
        using Bitmap bmp = RenderBitmap(spec, size, lightTaskbar);
        return bmp.GetHicon();
    }

    public static Bitmap RenderBitmap(TrayIconSpec spec, int size, bool lightTaskbar)
    {
        size = Math.Clamp(size, 16, 256);
        Palette pal = lightTaskbar ? s_light : s_dark;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        int barHeight = Math.Max(2, (int)Math.Round(size / 8.0));
        int gap = Math.Max(1, size / 16);
        var content = new RectangleF(0, 0, size, size - barHeight - gap);

        Color mainColor = spec.Band == LevelBand.Critical && !spec.Charging ? pal.Critical : pal.Foreground;
        using (var brush = new SolidBrush(mainColor))
        {
            if (spec.Style == IconStyle.DeviceIcon)
            {
                DrawFitted(g, DeviceGlyphs.For(spec.Kind), s_glyphFamily.Value, FontStyle.Regular, content, brush, maxWidthRatio: 1.0f);
            }
            else
            {
                string text = spec.Level is int l ? l.ToString(CultureInfo.InvariantCulture) : "?";
                DrawFitted(g, text, s_textFamily.Value, FontStyle.Bold, content, brush, maxWidthRatio: 1.0f);
            }
        }

        DrawBar(g, pal, spec, size, barHeight);
        return bmp;
    }

    private static void DrawBar(Graphics g, Palette pal, TrayIconSpec spec, int size, int barHeight)
    {
        float y = size - barHeight;
        float radius = barHeight / 2f;
        var track = new RectangleF(0.5f, y, size - 1f, barHeight);
        using (var trackBrush = new SolidBrush(pal.Track))
        using (GraphicsPath trackPath = RoundedRect(track, radius))
        {
            g.FillPath(trackBrush, trackPath);
        }

        if (spec.Level is not int level || level <= 0)
        {
            return;
        }

        Color fill = spec.Charging ? pal.Success
            : spec.Band switch
            {
                LevelBand.Critical => pal.Critical,
                LevelBand.Warning => pal.Caution,
                _ => pal.Foreground,
            };

        float width = Math.Max(barHeight, track.Width * level / 100f);
        using var fillBrush = new SolidBrush(fill);
        using GraphicsPath fillPath = RoundedRect(new RectangleF(track.X, y, width, barHeight), radius);
        g.FillPath(fillBrush, fillPath);
    }

    /// <summary>Dibuja el texto como trazado, escalado para ocupar el área sin salirse.</summary>
    private static void DrawFitted(Graphics g, string text, FontFamily family, FontStyle style, RectangleF area, Brush brush, float maxWidthRatio)
    {
        using var path = new GraphicsPath();
        using var format = StringFormat.GenericTypographic;
        path.AddString(text, family, (int)style, 100f, PointF.Empty, format);
        RectangleF bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        float scale = Math.Min(area.Width * maxWidthRatio / bounds.Width, area.Height / bounds.Height);
        float w = bounds.Width * scale;
        float h = bounds.Height * scale;
        float x = area.X + (area.Width - w) / 2f;
        float y = area.Y + (area.Height - h) / 2f;

        using var m = new Matrix();
        m.Translate(x, y);
        m.Scale(scale, scale);
        m.Translate(-bounds.X, -bounds.Y);
        path.Transform(m);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static FontFamily? TryFamily(string name)
    {
        try
        {
            return new FontFamily(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
