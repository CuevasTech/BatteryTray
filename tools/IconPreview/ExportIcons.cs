#:property TargetFramework=net10.0-windows10.0.26100.0
#:property PublishAot=false
#:project ../../src/BatteryTray.Windows/BatteryTray.Windows.csproj

// Exporta iconos de bandeja sueltos y la barra de la barra de tareas (PNG) para montar
// las capturas de la Store y las imágenes del README.
// Uso: dotnet run tools/IconPreview/ExportIcons.cs -- <carpeta-salida>
using System.Drawing;
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
// Iconos de bandeja a 200 % (32 px), para imágenes nítidas.
foreach ((string name, TrayIconSpec spec) in icons)
{
    using var bmp = TrayIconRenderer.RenderBitmap(spec, 32, lightTaskbar: false);
    bmp.Save(Path.Combine(outDir, $"{name}-32.png"), ImageFormat.Png);
}

// La barra dentro de la barra de tareas, a 100 % y 200 %, en español ("47 %") e inglés ("47%").
foreach ((string lang, string format) in new[] { ("es", "{0} %"), ("en", "{0}%") })
{
    TaskbarItemSpec[] bar =
    [
        new("mouse", 47, LevelBand.Normal, false, DeviceKind.Mouse, "blue", string.Format(format, 47), ""),
        new("headphones", 82, LevelBand.Normal, false, DeviceKind.Headphones, "purple", string.Format(format, 82), ""),
        new("gamepad", 64, LevelBand.Normal, true, DeviceKind.Gamepad, "orange", string.Format(format, 64), ""),
    ];
    foreach (bool light in new[] { false, true })
    {
        foreach (int scale in new[] { 1, 2 })
        {
            using Bitmap bmp = TaskbarBarRenderer.Render(bar, 48 * scale, scale, light);
            bmp.Save(Path.Combine(outDir, $"bar-{lang}-{(light ? "light" : "dark")}-{scale}x.png"), ImageFormat.Png);

            // Con el ratón encima del segundo dispositivo (los cascos).
            using Bitmap hover = TaskbarBarRenderer.Render(bar, 48 * scale, scale, light, hoverIndex: 1);
            hover.Save(Path.Combine(outDir, $"bar-{lang}-{(light ? "light" : "dark")}-{scale}x-hover.png"), ImageFormat.Png);
        }
    }

    // Centro de cada elemento a 200 %, para colocar encima el texto emergente en las imágenes.
    TaskbarBarLayout layout = TaskbarBarRenderer.Measure(bar, 96, 2);
    string centers = string.Join(",", layout.Items.Select(r => (r.X + r.Width / 2).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    File.WriteAllText(Path.Combine(outDir, $"bar-{lang}-layout.js"), $"window.BAR_{lang.ToUpperInvariant()} = [{centers}];" + Environment.NewLine);
}

Console.WriteLine("ok");
