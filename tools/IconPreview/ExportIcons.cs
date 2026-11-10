#:property TargetFramework=net10.0-windows10.0.26100.0
#:property PublishAot=false
#:project ../../src/BatteryTray.Windows/BatteryTray.Windows.csproj

// Exporta iconos de bandeja sueltos (PNG) para montar capturas de la Store.
// Uso: dotnet run tools/IconPreview/ExportIcons.cs -- <carpeta-salida>
using System.Drawing.Imaging;
using BatteryTray.Core;
using BatteryTray.Core.Settings;
using BatteryTray.Windows.Rendering;

string outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);
(string Name, TrayIconSpec Spec)[] icons =
[
    ("mouse", new(21, LevelBand.Normal, false, DeviceKind.Mouse, IconStyle.WindowsBattery, "blue")),
    ("headphones", new(80, LevelBand.Normal, false, DeviceKind.Headphones, IconStyle.WindowsBattery, "purple")),
    ("gamepad", new(70, LevelBand.Normal, true, DeviceKind.Gamepad, IconStyle.WindowsBattery, "orange")),
    ("low", new(12, LevelBand.Critical, false, DeviceKind.Keyboard, IconStyle.WindowsBattery, "pink")),
    ("system", new(98, LevelBand.Normal, false, DeviceKind.Computer, IconStyle.WindowsBattery, null)),
];
foreach ((string name, TrayIconSpec spec) in icons)
{
    foreach (int size in new[] { 16, 20, 24 })
    {
        using var bmp = TrayIconRenderer.RenderBitmap(spec, size, lightTaskbar: false);
        bmp.Save(Path.Combine(outDir, $"{name}-{size}.png"), ImageFormat.Png);
    }
}
Console.WriteLine("ok");
