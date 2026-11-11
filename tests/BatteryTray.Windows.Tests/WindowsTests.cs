using System.Drawing;
using BatteryTray.Core;
using BatteryTray.Core.Settings;
using BatteryTray.Windows.Native;
using BatteryTray.Windows.Providers;
using BatteryTray.Windows.Rendering;
using Windows.Storage.Streams;

namespace BatteryTray.Windows.Tests;

public class CfgMgrTests
{
    [Fact]
    public void ParseMultiSz_SplitsUntilDoubleNull()
    {
        IReadOnlyList<string> ids = CfgMgr.ParseMultiSz("A\\1\0B\\2\0\0basura".AsSpan());
        Assert.Equal(["A\\1", "B\\2"], ids);
    }

    [Fact]
    public void ParseMultiSz_EmptyList() => Assert.Empty(CfgMgr.ParseMultiSz("\0\0".AsSpan()));

    [Fact]
    public void GetPresentDeviceIds_RootEnumeratorHasDevices()
    {
        // "ROOT" siempre tiene dispositivos (en cualquier Windows), así se prueba la llamada real.
        IReadOnlyList<string> ids = CfgMgr.GetPresentDeviceIds("ROOT");
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.StartsWith("ROOT\\", id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetPresentDeviceIds_UnknownEnumerator_ReturnsEmpty() =>
        Assert.Empty(CfgMgr.GetPresentDeviceIds("NO_EXISTE_ENUMERADOR"));

    [Fact]
    public void Properties_ReadFriendlyNameOrNull_AndMissingBatteryIsNull()
    {
        string id = CfgMgr.GetPresentDeviceIds("ROOT")[0];
        // La batería no existe en un nodo ROOT: debe devolver null sin fallar.
        Assert.Null(CfgMgr.GetByteProperty(id, CfgMgr.BluetoothBattery));
        // El nombre puede existir o no, pero nunca debe lanzar.
        _ = CfgMgr.GetStringProperty(id, CfgMgr.FriendlyName);
        Assert.Null(CfgMgr.GetStringProperty(@"ROOT\NO\EXISTE", CfgMgr.FriendlyName));
    }
}

public class GattParseTests
{
    private static IBuffer Buffer(params byte[] bytes)
    {
        var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    [Theory]
    [InlineData((byte)0, 0)]
    [InlineData((byte)57, 57)]
    [InlineData((byte)100, 100)]
    public void ParseLevel_Valid(byte raw, int expected) =>
        Assert.Equal(expected, BluetoothBatteryProvider.ParseLevel(Buffer(raw)));

    [Fact]
    public void ParseLevel_OutOfRangeOrEmpty_IsNull()
    {
        Assert.Null(BluetoothBatteryProvider.ParseLevel(Buffer(101)));
        Assert.Null(BluetoothBatteryProvider.ParseLevel(Buffer(255)));
        Assert.Null(BluetoothBatteryProvider.ParseLevel(Buffer()));
        Assert.Null(BluetoothBatteryProvider.ParseLevel(null));
    }
}

public class RendererTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void RenderBitmap_HasRequestedSizeAndTransparentCorner(int size)
    {
        var spec = new TrayIconSpec(82, LevelBand.Normal, false, DeviceKind.Mouse, IconStyle.Percentage);
        using Bitmap bmp = TrayIconRenderer.RenderBitmap(spec, size, lightTaskbar: false);

        Assert.Equal(size, bmp.Width);
        Assert.Equal(size, bmp.Height);
        Assert.Equal(0, bmp.GetPixel(0, 0).A);
    }

    [Theory]
    [InlineData(IconStyle.Percentage)]
    [InlineData(IconStyle.DeviceIcon)]
    public void RenderBitmap_DrawsSomething(IconStyle style)
    {
        var spec = new TrayIconSpec(5, LevelBand.Critical, false, DeviceKind.Headphones, style);
        using Bitmap bmp = TrayIconRenderer.RenderBitmap(spec, 24, lightTaskbar: true);

        int opaque = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).A > 128)
                {
                    opaque++;
                }
            }
        }

        Assert.True(opaque > 20, $"Muy pocos píxeles dibujados: {opaque}");
    }

    [Fact]
    public void WindowsBattery_WhiteOutlineAndDeviceColorFill()
    {
        // A 20 px la batería mide 19×10 como la de Windows: contorno en y=5, relleno de y=7 a y=12.
        var spec = new TrayIconSpec(50, LevelBand.Normal, false, DeviceKind.Mouse, IconStyle.WindowsBattery, "blue");
        using Bitmap bmp = TrayIconRenderer.RenderBitmap(spec, 20, lightTaskbar: false);

        Assert.Equal(Color.White.ToArgb(), bmp.GetPixel(9, 5).ToArgb());   // contorno blanco, como Windows
        Assert.Equal(0, bmp.GetPixel(9, 6).A);                            // hueco de 1 px
        Assert.Equal(Color.FromArgb(255, 0x60, 0xCD, 0xFF).ToArgb(), bmp.GetPixel(3, 9).ToArgb()); // relleno azul
        Assert.Equal(0, bmp.GetPixel(15, 9).A);                           // vacío a la derecha (50 %)
    }

    [Fact]
    public void WindowsBattery_LowBatteryTurnsOutlineRed()
    {
        var spec = new TrayIconSpec(10, LevelBand.Critical, false, DeviceKind.Mouse, IconStyle.WindowsBattery, "blue");
        using Bitmap bmp = TrayIconRenderer.RenderBitmap(spec, 20, lightTaskbar: false);

        Assert.Equal(Color.FromArgb(255, 255, 153, 164).ToArgb(), bmp.GetPixel(9, 5).ToArgb()); // borde rojo
        Assert.Equal(Color.FromArgb(255, 0x60, 0xCD, 0xFF).ToArgb(), bmp.GetPixel(2, 9).ToArgb()); // relleno sigue azul
    }

    [Fact]
    public void WindowsBattery_ChargingUsesWindowsGreen()
    {
        var spec = new TrayIconSpec(90, LevelBand.Normal, true, DeviceKind.Mouse, IconStyle.WindowsBattery);
        using Bitmap bmp = TrayIconRenderer.RenderBitmap(spec, 20, lightTaskbar: false);

        Assert.Equal(Color.FromArgb(255, 0x9F, 0xD8, 0x9F).ToArgb(), bmp.GetPixel(3, 9).ToArgb());
    }

    [Fact]
    public void RenderHicon_ReturnsHandle()
    {
        var spec = new TrayIconSpec(null, LevelBand.Unknown, false, DeviceKind.Unknown, IconStyle.Percentage);
        nint icon = TrayIconRenderer.RenderHicon(spec, 16, lightTaskbar: false);
        Assert.NotEqual(0, icon);
        Assert.True(DestroyIcon(icon));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
}

public class TaskbarBarRendererTests
{
    private static readonly TaskbarItemSpec[] s_items =
    [
        new("dev:a", 47, LevelBand.Normal, false, DeviceKind.Mouse, "blue", "47 %", "Ratón: 47 %"),
        new("dev:b", 100, LevelBand.Normal, true, DeviceKind.Headphones, "purple", "100 %", "Cascos: 100 %"),
        new("dev:c", 8, LevelBand.Critical, false, DeviceKind.Keyboard, null, "8 %", "Teclado: 8 %"),
    ];

    [Fact]
    public void Measure_ItemsAreSideBySideInsideTheBar()
    {
        TaskbarBarLayout layout = TaskbarBarRenderer.Measure(s_items, 48, 1.0);

        Assert.Equal(48, layout.Size.Height);
        Assert.Equal(s_items.Length, layout.Items.Count);
        for (int i = 0; i < layout.Items.Count; i++)
        {
            Rectangle r = layout.Items[i];
            Assert.True(r.Left >= 0 && r.Right <= layout.Size.Width && r.Top >= 0 && r.Bottom <= layout.Size.Height);
            if (i > 0)
            {
                Assert.True(r.Left >= layout.Items[i - 1].Right, "los elementos no se solapan");
            }
        }
    }

    [Fact]
    public void Measure_GrowsWithScale()
    {
        TaskbarBarLayout normal = TaskbarBarRenderer.Measure(s_items, 48, 1.0);
        TaskbarBarLayout large = TaskbarBarRenderer.Measure(s_items, 72, 1.5);
        Assert.True(large.Size.Width > normal.Size.Width * 1.3);
    }

    [Fact]
    public void Measure_FitsSmallTaskbars()
    {
        TaskbarBarLayout layout = TaskbarBarRenderer.Measure(s_items, 32, 1.0);
        Assert.All(layout.Items, r => Assert.True(r.Height <= 32 && r.Top >= 0));
    }

    [Fact]
    public void HitTest_FindsTheItemUnderThePoint()
    {
        TaskbarBarLayout layout = TaskbarBarRenderer.Measure(s_items, 48, 1.0);
        Rectangle second = layout.Items[1];
        Assert.Equal(1, layout.HitTest(new Point(second.X + 3, second.Y + second.Height / 2)));
        Assert.Equal(-1, layout.HitTest(new Point(0, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_NoFullyTransparentPixels_SoEveryClickReachesTheBar(bool light)
    {
        // Windows deja pasar los clics por los píxeles con alfa 0 de una ventana "layered":
        // toda la barra debe tener al menos alfa 1 (también alrededor del texto y las baterías).
        using Bitmap bmp = TaskbarBarRenderer.Render(s_items, 48, 1.25, light, hoverIndex: 1);
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                Assert.True(bmp.GetPixel(x, y).A > 0, $"píxel transparente en ({x}, {y})");
            }
        }
    }

    [Fact]
    public void Render_HoverHighlightsOnlyThatItem()
    {
        TaskbarBarLayout layout = TaskbarBarRenderer.Measure(s_items, 48, 1.0);
        using Bitmap normal = TaskbarBarRenderer.Render(s_items, 48, 1.0, false);
        using Bitmap hover = TaskbarBarRenderer.Render(s_items, 48, 1.0, false, hoverIndex: 0);

        Rectangle first = layout.Items[0];
        Rectangle second = layout.Items[1];
        // Esquina interior del elemento (sin icono ni texto): solo cambia con el ratón encima.
        Assert.True(hover.GetPixel(first.X + 4, first.Y + 4).A > normal.GetPixel(first.X + 4, first.Y + 4).A);
        Assert.Equal(normal.GetPixel(second.X + 4, second.Y + 4), hover.GetPixel(second.X + 4, second.Y + 4));
    }

    [Fact]
    public void Render_AppIconWhenThereAreNoDevices()
    {
        TaskbarItemSpec[] items = [new("main", null, LevelBand.Unknown, false, DeviceKind.Unknown, null, "", "BatteryTray", IsAppIcon: true)];
        using var icon = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(icon))
        {
            g.Clear(Color.Red);
        }

        using Bitmap bmp = TaskbarBarRenderer.Render(items, 48, 1.0, false, appIcon: icon);
        TaskbarBarLayout layout = TaskbarBarRenderer.Measure(items, 48, 1.0);
        Rectangle r = layout.Items[0];
        Color center = bmp.GetPixel(r.X + r.Width / 2, r.Y + r.Height / 2);
        Assert.True(center.R > 200 && center.G < 60, "se dibuja el logo");
    }

    [Fact]
    public void RenderBattery_HasRoomForTheChargingBolt()
    {
        using Bitmap battery = TrayIconRenderer.RenderBattery(20, 10, 60, LevelBand.Normal, true, "blue", false);
        Assert.Equal(20, battery.Width);
        Assert.Equal(20, battery.Height);
        bool boltAboveBody = false;
        for (int x = 0; x < battery.Width; x++)
        {
            boltAboveBody |= battery.GetPixel(x, 3).A > 0; // por encima de la batería (que empieza en y = 5)
        }

        Assert.True(boltAboveBody, "el rayo sobresale por arriba, como en Windows");
    }
}
