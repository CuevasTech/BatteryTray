#:property TargetFramework=net10.0-windows10.0.26100.0
#:property PublishAot=false
#:project ../../src/BatteryTray.Windows/BatteryTray.Windows.csproj

// Genera tiras de muestra de los iconos de bandeja para revisarlos a ojo.
// Uso: dotnet run tools/IconPreview/IconPreview.cs -- <carpeta-salida>
using System.Drawing;
using System.Drawing.Imaging;
using BatteryTray.Core;
using BatteryTray.Core.Settings;
using BatteryTray.Windows.Rendering;

string outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

TrayIconSpec[] specs =
[
    // Estilo batería de Windows: blanca (igual que Windows) y con contorno de color.
    new(98, LevelBand.Normal, false, DeviceKind.Computer, IconStyle.WindowsBattery, null),
    new(82, LevelBand.Normal, false, DeviceKind.Headphones, IconStyle.WindowsBattery, "blue"),
    new(55, LevelBand.Normal, false, DeviceKind.Mouse, IconStyle.WindowsBattery, "purple"),
    new(64, LevelBand.Normal, true, DeviceKind.Gamepad, IconStyle.WindowsBattery, "orange"),
    new(12, LevelBand.Critical, false, DeviceKind.Keyboard, IconStyle.WindowsBattery, "pink"),
    new(35, LevelBand.Warning, false, DeviceKind.Pen, IconStyle.WindowsBattery, "teal"),
    new(100, LevelBand.Normal, false, DeviceKind.Speaker, IconStyle.WindowsBattery, "indigo"),
    // Porcentaje e icono del dispositivo, con batería pequeña.
    new(82, LevelBand.Normal, false, DeviceKind.Headphones, IconStyle.Percentage, "blue"),
    new(12, LevelBand.Critical, false, DeviceKind.Keyboard, IconStyle.Percentage, "pink"),
    new(64, LevelBand.Normal, true, DeviceKind.Gamepad, IconStyle.Percentage, "orange"),
    new(82, LevelBand.Normal, false, DeviceKind.Headphones, IconStyle.DeviceIcon, "blue"),
    new(55, LevelBand.Normal, false, DeviceKind.Mouse, IconStyle.DeviceIcon, "purple"),
];

foreach (int size in new[] { 16, 20, 24, 32 })
{
    foreach (bool light in new[] { false, true })
    {
        int pad = 8;
        int zoom = size <= 20 ? 6 : 4;
        int cell = size * zoom + pad;
        using var strip = new Bitmap(cell * specs.Length + pad, cell + pad);
        using Graphics g = Graphics.FromImage(strip);
        g.Clear(light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(32, 28, 28));
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        for (int i = 0; i < specs.Length; i++)
        {
            using Bitmap icon = TrayIconRenderer.RenderBitmap(specs[i], size, light);
            g.DrawImage(icon, new Rectangle(pad + i * cell, pad, size * zoom, size * zoom));
        }

        string file = Path.Combine(outDir, $"tray-{size}-{(light ? "light" : "dark")}.png");
        strip.Save(file, ImageFormat.Png);
        Console.WriteLine(file);
    }
}

// Tamaño real (sin ampliar), como se ve en la barra.
using (var real = new Bitmap(specs.Length * 28 + 8, 40))
using (Graphics g = Graphics.FromImage(real))
{
    g.Clear(Color.FromArgb(32, 28, 28));
    for (int i = 0; i < specs.Length; i++)
    {
        using Bitmap icon = TrayIconRenderer.RenderBitmap(specs[i], 16, false);
        g.DrawImageUnscaled(icon, 8 + i * 28, 12);
    }

    real.Save(Path.Combine(outDir, "tray-real-16.png"), ImageFormat.Png);
}
