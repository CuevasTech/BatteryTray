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
/// Dibuja los iconos de la bandeja copiando el icono de batería de Windows 11 (medido píxel
/// a píxel: a 20 px es una batería de 19×10 con contorno de 1 px, 1 px de hueco, relleno
/// de 15×6 y borne de 1 px) y el rayo de carga recortado en el centro.
/// Los colores son los mismos que usa Windows: el contorno siempre del color del texto y el
/// relleno verde cargando, rojo con la batería baja y, si no, del color del texto.
/// </summary>
public static class TrayIconRenderer
{
    private static readonly Lazy<FontFamily> s_textFamily = new(() => TryFamily("Segoe UI") ?? FontFamily.GenericSansSerif);
    private static readonly Lazy<FontFamily> s_glyphFamily = new(() => TryFamily("Segoe Fluent Icons") ?? TryFamily("Segoe MDL2 Assets") ?? FontFamily.GenericSansSerif);

    private sealed record Palette(Color Foreground, Color Critical, Color Success);

    // Los colores del icono de batería de Windows 11, sacados de sus propios recursos
    // (SystemTray, en MicrosoftWindows.Client.Core): texto, rojo (batería baja) y verde (cargando).
    private static readonly Palette s_dark = new(Color.White, Color.FromArgb(255, 0xD9, 0x2C, 0x2C), Color.FromArgb(255, 0x9F, 0xD8, 0x9F));
    private static readonly Palette s_light = new(Color.FromArgb(0xE4, 0, 0, 0), Color.FromArgb(255, 0xC5, 0x0F, 0x1F), Color.FromArgb(255, 0x10, 0x7C, 0x10));

    // Rayo de carga en un cuadro de 1×1 (x, y).
    private static readonly PointF[] s_bolt =
    [
        new(0.64f, 0.00f), new(0.06f, 0.56f), new(0.44f, 0.56f), new(0.34f, 1.00f),
        new(0.94f, 0.42f), new(0.56f, 0.42f), new(0.68f, 0.00f),
    ];

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
        // "Half": cada píxel ocupa [i, i+1], así los trazos de 1 px en x+0,5 quedan nítidos.
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        bool critical = spec.Band == LevelBand.Critical && !spec.Charging;
        (Color outline, Color fill) = BatteryColors(pal, spec.Band, spec.Charging);

        if (spec.Style == IconStyle.WindowsBattery)
        {
            // Proporción del icono de Windows: 19×10 px a 20 px (125 %). A 16 px (100 %) Windows no
            // deja a las apps pasar de 16×16, así que se usa 15×9, lo más parecido que cabe.
            int height = size <= 16 ? 9 : (int)Math.Round(size * 0.5);
            var box = new Rectangle(0, (size - height) / 2, size, height);
            Rectangle body = DrawBattery(g, box, outline, fill, spec.Level, compact: false);
            if (spec.Charging)
            {
                DrawBolt(g, body, pal.Foreground, gap: Stroke(height));
            }

            return bmp;
        }

        // Estilos con número o icono arriba y una batería pequeña debajo.
        int miniHeight = Math.Max(5, (int)Math.Round(size * 0.34));
        var mini = new Rectangle(0, size - miniHeight, size, miniHeight);
        var content = new RectangleF(0, 0, size, size - miniHeight - Math.Max(1, size / 16));

        using (var brush = new SolidBrush(critical ? pal.Critical : pal.Foreground))
        {
            if (spec.Style == IconStyle.DeviceIcon)
            {
                DrawFitted(g, DeviceGlyphs.For(spec.Kind), s_glyphFamily.Value, FontStyle.Regular, content, brush);
            }
            else
            {
                string text = spec.Level is int l ? l.ToString(CultureInfo.InvariantCulture) : "?";
                DrawFitted(g, text, s_textFamily.Value, FontStyle.Bold, content, brush);
            }
        }

        DrawBattery(g, mini, outline, fill, spec.Level, compact: true);
        return bmp;
    }

    /// <summary>Color del texto de la barra de tareas (blanco en oscuro, casi negro en claro).</summary>
    public static Color Foreground(bool lightTaskbar) => (lightTaskbar ? s_light : s_dark).Foreground;

    /// <summary>Rojo de la batería baja de Windows para el tema de la barra de tareas.</summary>
    public static Color Critical(bool lightTaskbar) => (lightTaskbar ? s_light : s_dark).Critical;

    /// <summary>
    /// Solo la batería de Windows (con el rayo si carga), en un recuadro de <paramref name="width"/>×<paramref name="height"/>
    /// (borne incluido). La imagen mide el doble de alto, con la batería centrada, para que
    /// quepa el rayo, que sobresale por arriba.
    /// </summary>
    public static Bitmap RenderBattery(int width, int height, int? level, LevelBand band, bool charging, bool lightTaskbar)
    {
        width = Math.Clamp(width, 8, 512);
        height = Math.Clamp(height, 4, 256);
        Palette pal = lightTaskbar ? s_light : s_dark;
        var bmp = new Bitmap(width, height * 2, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighQuality;

        (Color outline, Color fill) = BatteryColors(pal, band, charging);
        var box = new Rectangle(0, height / 2, width, height);
        Rectangle body = DrawBattery(g, box, outline, fill, level, compact: false);
        if (charging)
        {
            DrawBolt(g, body, pal.Foreground, gap: Stroke(height));
        }

        return bmp;
    }

    /// <summary>
    /// Igual que Windows: el contorno siempre del color del texto; el relleno verde mientras
    /// carga, rojo con la batería baja y, si no, también del color del texto.
    /// </summary>
    private static (Color Outline, Color Fill) BatteryColors(Palette pal, LevelBand band, bool charging)
    {
        Color fill = charging ? pal.Success : band == LevelBand.Critical ? pal.Critical : pal.Foreground;
        return (pal.Foreground, fill);
    }

    private static int Stroke(int height) => height >= 14 ? 2 : 1;

    /// <summary>Dibuja la batería en el recuadro (incluido el borne) y devuelve el cuerpo.</summary>
    private static Rectangle DrawBattery(Graphics g, Rectangle box, Color outline, Color fill, int? level, bool compact)
    {
        int s = Stroke(box.Height);
        int nubW = s;
        int nubH = Math.Max(2, (int)Math.Round(box.Height * 0.4));
        int gap = compact && box.Height < 8 ? 0 : s;
        var body = new Rectangle(box.X, box.Y, box.Width - nubW, box.Height);
        // Esquinas tan redondeadas como las de Windows (≈3 px en una batería de 10 px de alto).
        float radius = compact ? Math.Max(1.5f, box.Height * 0.22f) : Math.Max(2f, box.Height * 0.3f);

        // Contorno: el trazo se centra medio píxel hacia dentro para que quede nítido.
        float half = s / 2f;
        using (var pen = new Pen(outline, s))
        using (GraphicsPath path = RoundedRect(new RectangleF(body.X + half, body.Y + half, body.Width - s, body.Height - s), radius))
        {
            g.DrawPath(pen, path);
        }

        // Borne a la derecha.
        using (var nubBrush = new SolidBrush(outline))
        {
            g.FillRectangle(nubBrush, body.Right, box.Y + (box.Height - nubH) / 2, nubW, nubH);
        }

        // Relleno, separado del contorno por un píxel de hueco.
        int inset = s + gap;
        var inner = new Rectangle(body.X + inset, body.Y + inset, body.Width - 2 * inset, body.Height - 2 * inset);
        if (level is int l && l > 0 && inner.Width > 0 && inner.Height > 0)
        {
            int width = Math.Max(1, (int)Math.Round(inner.Width * Math.Clamp(l, 0, 100) / 100.0));
            using var fillBrush = new SolidBrush(fill);
            if (!compact && inner.Height >= 7 && width >= 3)
            {
                // Como en Windows, el relleno tiene las esquinas apenas redondeadas.
                using GraphicsPath fillPath = RoundedRect(new RectangleF(inner.X, inner.Y, width, inner.Height), 1f);
                g.FillPath(fillBrush, fillPath);
            }
            else
            {
                SmoothingMode previous = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None; // bordes rectos y nítidos
                g.FillRectangle(fillBrush, inner.X, inner.Y, width, inner.Height);
                g.SmoothingMode = previous;
            }
        }

        return body;
    }

    /// <summary>
    /// Rayo de carga con un hueco transparente alrededor. Como en Windows, va centrado en
    /// horizontal y sobresale por encima del borde superior.
    /// </summary>
    private static void DrawBolt(Graphics g, Rectangle body, Color color, int gap)
    {
        float h = body.Height * 1.05f;
        float w = h * 0.68f;
        float x = body.X + (body.Width - w) / 2f;
        float y = body.Y - body.Height * 0.32f;

        using var path = new GraphicsPath();
        path.AddPolygon(s_bolt.Select(p => new PointF(x + p.X * w, y + p.Y * h)).ToArray());

        // Primero se "borra" un contorno alrededor del rayo y luego se pinta el rayo.
        CompositingMode previous = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var eraser = new Pen(Color.Transparent, gap * 2f) { LineJoin = LineJoin.Round })
        {
            g.DrawPath(eraser, path);
        }

        using (var clear = new SolidBrush(Color.Transparent))
        {
            g.FillPath(clear, path);
        }

        g.CompositingMode = previous;
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>Dibuja el texto como trazado, escalado para ocupar el área sin salirse.</summary>
    private static void DrawFitted(Graphics g, string text, FontFamily family, FontStyle style, RectangleF area, Brush brush)
    {
        using var path = new GraphicsPath();
        using var format = StringFormat.GenericTypographic;
        path.AddString(text, family, (int)style, 100f, PointF.Empty, format);
        RectangleF bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        float scale = Math.Min(area.Width / bounds.Width, area.Height / bounds.Height);
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
