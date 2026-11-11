using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using BatteryTray.Core;

namespace BatteryTray.Windows.Rendering;

/// <summary>Un elemento de la barra: un dispositivo (o el logo de la app si no hay ninguno).</summary>
/// <param name="Key">Identificador (el mismo que usan los iconos de la bandeja).</param>
/// <param name="Text">Texto del nivel ya formateado ("85 %").</param>
/// <param name="Tooltip">Texto al pasar el ratón.</param>
/// <param name="IsAppIcon">True para el elemento con el logo (cuando no hay dispositivos).</param>
public sealed record TaskbarItemSpec(
    string Key,
    int? Level,
    LevelBand Band,
    bool Charging,
    DeviceKind Kind,
    string? ColorKey,
    string Text,
    string Tooltip,
    bool IsAppIcon = false);

/// <summary>Medidas de la barra: tamaño total y recuadro de cada elemento (en píxeles).</summary>
public sealed class TaskbarBarLayout(Size size, IReadOnlyList<Rectangle> items, double scale)
{
    public Size Size { get; } = size;

    public IReadOnlyList<Rectangle> Items { get; } = items;

    public double Scale { get; } = scale;

    /// <summary>Índice del elemento bajo el punto, o -1.</summary>
    public int HitTest(Point point)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Contains(point))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Dibuja la "barra de herramientas" que va dentro de la barra de tareas: por cada
/// dispositivo, su icono, la batería de Windows 11 (con el color del dispositivo) y el
/// porcentaje, con el mismo efecto al pasar el ratón que los botones de la barra.
/// Medidas a 100 % de escala; todo se multiplica por la escala de la pantalla.
/// </summary>
public static class TaskbarBarRenderer
{
    private const float Padding = 8f;      // margen dentro de cada elemento
    private const float GlyphSize = 16f;   // icono del dispositivo
    private const float GlyphGap = 6f;
    private const float BatteryWidth = 20f; // la batería de Windows 11 a 20 px: 19×10 más el borne
    private const float BatteryHeight = 10f;
    private const float TextGap = 5f;
    private const float TextSize = 12f;    // como el texto del reloj
    private const float ItemHeight = 40f;  // como los botones de la barra de tareas
    private const float ItemGap = 2f;
    private const float Edge = 2f;
    private const float CornerRadius = 4f;

    private static readonly Lazy<FontFamily> s_textFamily = new(() => TryFamily("Segoe UI") ?? FontFamily.GenericSansSerif);
    private static readonly Lazy<FontFamily> s_glyphFamily = new(() => TryFamily("Segoe Fluent Icons") ?? TryFamily("Segoe MDL2 Assets") ?? FontFamily.GenericSansSerif);

    /// <summary>Calcula el tamaño de la barra para la altura de la barra de tareas y la escala.</summary>
    public static TaskbarBarLayout Measure(IReadOnlyList<TaskbarItemSpec> items, int barHeight, double scale)
    {
        scale = Math.Clamp(scale, 0.5, 5.0);
        barHeight = Math.Max(16, barHeight);
        int itemHeight = Math.Min(Px(ItemHeight, scale), barHeight - 2 * Px(2, scale));
        int itemY = (barHeight - itemHeight) / 2;

        using var bmp = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(s_textFamily.Value, TextSize * (float)scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();

        var rects = new List<Rectangle>(items.Count);
        int x = Px(Edge, scale);
        foreach (TaskbarItemSpec item in items)
        {
            int width;
            if (item.IsAppIcon)
            {
                width = 2 * Px(Padding, scale) + Px(GlyphSize, scale);
            }
            else
            {
                int textWidth = (int)Math.Ceiling(g.MeasureString(item.Text, font, PointF.Empty, format).Width);
                width = Px(Padding, scale) + Px(GlyphSize, scale) + Px(GlyphGap, scale) + Px(BatteryWidth, scale)
                    + Px(TextGap, scale) + textWidth + Px(Padding, scale);
            }

            rects.Add(new Rectangle(x, itemY, width, itemHeight));
            x += width + Px(ItemGap, scale);
        }

        int total = items.Count == 0 ? 2 * Px(Edge, scale) : x - Px(ItemGap, scale) + Px(Edge, scale);
        return new TaskbarBarLayout(new Size(total, barHeight), rects, scale);
    }

    /// <summary>
    /// Dibuja la barra sobre <paramref name="g"/> (de tamaño <see cref="TaskbarBarLayout.Size"/>).
    /// Todo el fondo queda casi transparente (alfa 1) para que los clics en los huecos
    /// lleguen a la barra y no a la barra de tareas.
    /// </summary>
    public static void Draw(
        Graphics g,
        TaskbarBarLayout layout,
        IReadOnlyList<TaskbarItemSpec> items,
        bool lightTaskbar,
        int hoverIndex = -1,
        bool pressed = false,
        Image? appIcon = null)
    {
        double scale = layout.Scale;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var background = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
        {
            g.FillRectangle(background, 0, 0, layout.Size.Width, layout.Size.Height);
        }

        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        // Calidad normal a propósito: la "alta" mezcla con corrección gamma y deja el fondo
        // (alfa 1) a 0 alrededor del texto y las imágenes, y por esos huecos los clics
        // pasarían a la barra de tareas.
        g.CompositingQuality = CompositingQuality.Default;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        // Sin ClearType: sobre un fondo transparente solo funciona el suavizado en gris.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        Color foreground = TrayIconRenderer.Foreground(lightTaskbar);
        using var textBrush = new SolidBrush(foreground);
        using var textFont = new Font(s_textFamily.Value, TextSize * (float)scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var glyphFont = new Font(s_glyphFamily.Value, GlyphSize * (float)scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.LineAlignment = StringAlignment.Center;
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        using var centered = (StringFormat)format.Clone();
        centered.Alignment = StringAlignment.Center;

        int height = layout.Size.Height;
        for (int i = 0; i < items.Count && i < layout.Items.Count; i++)
        {
            TaskbarItemSpec item = items[i];
            Rectangle r = layout.Items[i];

            if (i == hoverIndex)
            {
                // Mismo efecto que los botones de la barra de tareas de Windows 11.
                int alpha = lightTaskbar ? (pressed ? 0x0A : 0x12) : (pressed ? 0x0E : 0x18);
                Color tint = lightTaskbar ? Color.Black : Color.White;
                using var hover = new SolidBrush(Color.FromArgb(alpha, tint));
                using GraphicsPath path = RoundedRect(r, Px(CornerRadius, scale));
                g.FillPath(hover, path);
            }

            float x = r.X + Px(Padding, scale);
            float glyph = Px(GlyphSize, scale);
            if (item.IsAppIcon)
            {
                if (appIcon is not null)
                {
                    g.DrawImage(appIcon, new RectangleF(x, (height - glyph) / 2f, glyph, glyph));
                }

                continue;
            }

            // Icono del tipo de dispositivo.
            g.DrawString(DeviceGlyphs.For(item.Kind), glyphFont, textBrush, new RectangleF(x, 0, glyph, height), centered);
            x += glyph + Px(GlyphGap, scale);

            // La batería de Windows 11, con el color del dispositivo.
            int bw = Px(BatteryWidth, scale);
            int bh = Px(BatteryHeight, scale);
            using (Bitmap battery = TrayIconRenderer.RenderBattery(bw, bh, item.Level, item.Band, item.Charging, item.ColorKey, lightTaskbar))
            {
                int top = (height - bh) / 2 - bh / 2;
                g.DrawImage(battery, new Rectangle((int)x, top, battery.Width, battery.Height));
            }

            x += bw + Px(TextGap, scale);

            // Porcentaje.
            g.DrawString(item.Text, textFont, textBrush, new RectangleF(x, 0, r.Right - x, height), format);
        }
    }

    /// <summary>Dibuja la barra en una imagen nueva (para pruebas y capturas).</summary>
    public static Bitmap Render(IReadOnlyList<TaskbarItemSpec> items, int barHeight, double scale, bool lightTaskbar, int hoverIndex = -1, Image? appIcon = null)
    {
        TaskbarBarLayout layout = Measure(items, barHeight, scale);
        var bmp = new Bitmap(layout.Size.Width, layout.Size.Height, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bmp);
        Draw(g, layout, items, lightTaskbar, hoverIndex, pressed: false, appIcon);
        return bmp;
    }

    private static int Px(float value, double scale) => (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);

    private static GraphicsPath RoundedRect(Rectangle r, float radius)
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
