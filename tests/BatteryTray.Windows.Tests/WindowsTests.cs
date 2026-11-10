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
