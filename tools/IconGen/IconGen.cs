#:property TargetFramework=net10.0-windows
#:property PublishAot=false
#:package System.Drawing.Common

// Genera el icono de la app (.ico) y las imágenes que pide el paquete MSIX.
// Uso: dotnet run tools/IconGen/IconGen.cs -- <carpeta Assets>
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

string outDir = args.Length > 0 ? args[0] : "Assets";
Directory.CreateDirectory(outDir);

// Imágenes cuadradas con el logo ocupando casi todo.
SaveSquare("Square44x44Logo.scale-200.png", 88, 0.92f);
SaveSquare("Square44x44Logo.targetsize-16_altform-unplated.png", 16, 1f);
SaveSquare("Square44x44Logo.targetsize-24_altform-unplated.png", 24, 1f);
SaveSquare("Square44x44Logo.targetsize-32_altform-unplated.png", 32, 1f);
SaveSquare("Square44x44Logo.targetsize-48_altform-unplated.png", 48, 1f);
SaveSquare("Square44x44Logo.targetsize-256_altform-unplated.png", 256, 1f);
SaveSquare("Square150x150Logo.scale-200.png", 300, 0.6f);
SaveSquare("StoreLogo.png", 50, 0.92f);
SaveSquare("LockScreenLogo.scale-200.png", 48, 0.92f);
SaveRect("Wide310x150Logo.scale-200.png", 620, 300, 0.6f);
SaveRect("SplashScreen.scale-200.png", 1240, 600, 0.4f);
SaveIco("AppIcon.ico", [16, 20, 24, 32, 40, 48, 64, 256]);
Console.WriteLine($"Listo: {Path.GetFullPath(outDir)}");

void SaveSquare(string name, int size, float fill) => SaveRect(name, size, size, fill);

void SaveRect(string name, int w, int h, float fill)
{
    using Bitmap bmp = Render(w, h, fill);
    bmp.Save(Path.Combine(outDir, name), ImageFormat.Png);
}

void SaveIco(string name, int[] sizes)
{
    // Formato ICO con cada tamaño guardado como PNG (admitido desde Windows Vista).
    var pngs = sizes.Select(s =>
    {
        using Bitmap bmp = Render(s, s, 1f);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }).ToList();

    using var fs = File.Create(Path.Combine(outDir, name));
    using var bw = new BinaryWriter(fs);
    bw.Write((ushort)0);
    bw.Write((ushort)1);
    bw.Write((ushort)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(pngs[i].Length);
        bw.Write(offset);
        offset += pngs[i].Length;
    }

    foreach (byte[] png in pngs)
    {
        bw.Write(png);
    }
}

static Bitmap Render(int w, int h, float fill)
{
    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using Graphics g = Graphics.FromImage(bmp);
    g.Clear(Color.Transparent);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

    float box = Math.Min(w, h) * fill;
    float ox = (w - box) / 2f;
    float oy = (h - box) / 2f;

    // Cuerpo de la batería (horizontal) y su borne.
    float bodyW = box * 0.80f;
    float bodyH = box * 0.52f;
    float nubW = box * 0.08f;
    float nubH = bodyH * 0.40f;
    float gapNub = box * 0.02f;
    float totalW = bodyW + gapNub + nubW;
    float bx = ox + (box - totalW) / 2f;
    float by = oy + (box - bodyH) / 2f;
    float radius = bodyH * 0.24f;

    var body = new RectangleF(bx, by, bodyW, bodyH);
    using (var grad = new LinearGradientBrush(body, Color.FromArgb(255, 0, 103, 192), Color.FromArgb(255, 0, 178, 202), LinearGradientMode.ForwardDiagonal))
    using (GraphicsPath bodyPath = RoundedRect(body, radius))
    {
        g.FillPath(grad, bodyPath);
    }

    var nub = new RectangleF(bx + bodyW + gapNub, by + (bodyH - nubH) / 2f, nubW, nubH);
    using (var nubBrush = new SolidBrush(Color.FromArgb(255, 0, 178, 202)))
    using (GraphicsPath nubPath = RoundedRect(nub, nubW * 0.45f))
    {
        g.FillPath(nubBrush, nubPath);
    }

    // Tres barras de nivel; la última más tenue (batería "casi llena").
    float pad = bodyH * 0.18f;
    float inner = bodyW - pad * 2;
    float barGap = inner * 0.08f;
    float barW = (inner - barGap * 2) / 3f;
    float barH = bodyH - pad * 2;
    for (int i = 0; i < 3; i++)
    {
        int alpha = i == 2 ? 120 : 245;
        var bar = new RectangleF(bx + pad + i * (barW + barGap), by + pad, barW, barH);
        using var barBrush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        using GraphicsPath barPath = RoundedRect(bar, Math.Min(barW, barH) * 0.22f);
        g.FillPath(barBrush, barPath);
    }

    return bmp;
}

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    var path = new GraphicsPath();
    float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
    if (d < 1f)
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
