#:property TargetFramework=net10.0
#:property PublishAot=false
#:package Svg.Skia

// Genera el icono de la app (.ico) y las imágenes del paquete MSIX a partir de los SVG
// de docs/brand. Uso:
//   dotnet run tools/IconGen/IconGen.cs -- assets <carpeta Assets>
//   dotnet run tools/IconGen/IconGen.cs -- preview <archivo.png>
using SkiaSharp;
using Svg.Skia;

string root = FindRepoRoot();
string mode = args.Length > 0 ? args[0] : "assets";
using SKPicture big = LoadSvg(Path.Combine(root, "docs", "brand", "logo.svg"));
using SKPicture small = LoadSvg(Path.Combine(root, "docs", "brand", "logo-small.svg"));

// Por debajo de 48 px se usa la versión simplificada (se lee mejor).
SKPicture For(int size) => size < 48 ? small : big;

if (mode == "preview")
{
    string output = args.Length > 1 ? args[1] : "preview.png";
    int[] sizes = [256, 128, 64, 48, 32, 24, 16];
    int width = sizes.Sum() + (sizes.Length + 1) * 16;
    using var sheet = new SKBitmap(width, 2 * (256 + 32));
    using (var canvas = new SKCanvas(sheet))
    {
        canvas.Clear(new SKColor(32, 32, 32));
        using var light = new SKPaint { Color = new SKColor(243, 243, 243) };
        canvas.DrawRect(0, 256 + 32, width, 256 + 32, light);
        for (int row = 0; row < 2; row++)
        {
            int x = 16;
            foreach (int s in sizes)
            {
                using SKBitmap icon = Render(For(s), s, s, 1f);
                canvas.DrawBitmap(icon, x, row * (256 + 32) + 16 + (256 - s) / 2);
                x += s + 16;
            }
        }
    }

    Save(sheet, output);
    Console.WriteLine(output);
    return;
}

string outDir = args.Length > 1 ? args[1] : "Assets";
Directory.CreateDirectory(outDir);

SaveSquare("Square44x44Logo.scale-200.png", 88, 1f);
foreach (int s in new[] { 16, 24, 32, 48, 256 })
{
    SaveSquare($"Square44x44Logo.targetsize-{s}_altform-unplated.png", s, 1f);
}

SaveSquare("Square150x150Logo.scale-200.png", 300, 0.66f);
SaveSquare("StoreLogo.png", 50, 1f);
SaveSquare("LockScreenLogo.scale-200.png", 48, 1f);
SaveRect("Wide310x150Logo.scale-200.png", 620, 300, 0.62f);
SaveRect("SplashScreen.scale-200.png", 1240, 600, 0.42f);
SaveIco("AppIcon.ico", [16, 20, 24, 32, 40, 48, 64, 256]);
Console.WriteLine($"Listo: {Path.GetFullPath(outDir)}");

void SaveSquare(string name, int size, float fill) => SaveRect(name, size, size, fill);

void SaveRect(string name, int w, int h, float fill)
{
    using SKBitmap bmp = Render(For((int)(Math.Min(w, h) * fill)), w, h, fill);
    Save(bmp, Path.Combine(outDir, name));
}

void SaveIco(string name, int[] sizes)
{
    // ICO con cada tamaño guardado como PNG (admitido desde Windows Vista).
    var pngs = sizes.Select(s =>
    {
        using SKBitmap bmp = Render(For(s), s, s, 1f);
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }).ToList();

    using var fs = File.Create(Path.Combine(outDir, name));
    using var bw = new BinaryWriter(fs);
    bw.Write((ushort)0);
    bw.Write((ushort)1);
    bw.Write((ushort)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
        bw.Write(dim);
        bw.Write(dim);
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

static SKPicture LoadSvg(string path)
{
    var svg = new SKSvg();
    return svg.Load(path) ?? throw new InvalidOperationException($"No se pudo cargar {path}");
}

static SKBitmap Render(SKPicture picture, int w, int h, float fill)
{
    var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(SKColors.Transparent);
    SKRect bounds = picture.CullRect;
    float box = Math.Min(w, h) * fill;
    float scale = box / Math.Max(bounds.Width, bounds.Height);
    canvas.Translate((w - bounds.Width * scale) / 2f, (h - bounds.Height * scale) / 2f);
    canvas.Scale(scale);
    canvas.DrawPicture(picture);
    return bmp;
}

static void Save(SKBitmap bmp, string path)
{
    using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
}

static string FindRepoRoot()
{
    string? dir = Directory.GetCurrentDirectory();
    while (dir is not null && !File.Exists(Path.Combine(dir, "BatteryTray.slnx")))
    {
        dir = Path.GetDirectoryName(dir);
    }

    return dir ?? throw new InvalidOperationException("Ejecuta la herramienta desde dentro del repositorio.");
}
