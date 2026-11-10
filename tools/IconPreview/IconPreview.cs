#:property TargetFramework=net10.0-windows10.0.26100.0
#:property PublishAot=false
#:project ../../src/BatteryTray.Windows/BatteryTray.Windows.csproj

// Genera una tira de muestra de los iconos de bandeja para revisarlos a ojo.
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
    new(82, LevelBand.Normal, false, DeviceKind.Headphones, IconStyle.Percentage),
    new(100, LevelBand.Normal, false, DeviceKind.Mouse, IconStyle.Percentage),
    new(30, LevelBand.Warning, false, DeviceKind.Keyboard, IconStyle.Percentage),
    new(9, LevelBand.Critical, false, DeviceKind.Mouse, IconStyle.Percentage),
    new(64, LevelBand.Normal, true, DeviceKind.Gamepad, IconStyle.Percentage),
    new(null, LevelBand.Unknown, false, DeviceKind.Unknown, IconStyle.Percentage),
    new(82, LevelBand.Normal, false, DeviceKind.Headphones, IconStyle.DeviceIcon),
    new(18, LevelBand.Critical, false, DeviceKind.Mouse, IconStyle.DeviceIcon),
    new(64, LevelBand.Normal, true, DeviceKind.Gamepad, IconStyle.DeviceIcon),
    new(57, LevelBand.Normal, false, DeviceKind.Keyboard, IconStyle.DeviceIcon),
    new(40, LevelBand.Normal, false, DeviceKind.Pen, IconStyle.DeviceIcon),
    new(70, LevelBand.Normal, false, DeviceKind.Computer, IconStyle.DeviceIcon),
];

foreach (int size in new[] { 16, 20, 24, 32 })
{
    foreach (bool light in new[] { false, true })
    {
        const int pad = 8;
        const int zoom = 4;
        int cell = size * zoom + pad;
        using var strip = new Bitmap(cell * specs.Length + pad, cell + pad);
        using Graphics g = Graphics.FromImage(strip);
        g.Clear(light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
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
