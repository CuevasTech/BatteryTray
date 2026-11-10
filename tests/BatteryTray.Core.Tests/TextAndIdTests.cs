using BatteryTray.Core;

namespace BatteryTray.Core.Tests;

public class DeviceTextTests
{
    [Theory]
    [InlineData(null, "fb")]
    [InlineData("", "fb")]
    [InlineData("   ", "fb")]
    [InlineData("  MX   Master\t3S ", "MX Master 3S")]
    [InlineData("Auriculares‮gnp.exe", "Auricularesgnp.exe")] // sin caracteres de reordenación bidi
    [InlineData("Buds\u0000\u0007 Pro", "Buds Pro")]
    [InlineData("​", "fb")] // solo caracteres invisibles
    public void Sanitize_CleansUntrustedNames(string? input, string expected) =>
        Assert.Equal(expected, DeviceText.Sanitize(input, "fb"));

    [Fact]
    public void Sanitize_TruncatesLongNames()
    {
        string result = DeviceText.Sanitize(new string('a', 200), "fb");
        Assert.Equal(DeviceText.MaxNameLength, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void Sanitize_DoesNotSplitSurrogatePairs()
    {
        string input = new string('a', DeviceText.MaxNameLength - 2) + "😀😀😀";
        string result = DeviceText.Sanitize(input, "fb");
        Assert.False(char.IsHighSurrogate(result[^2]));
    }

    [Fact]
    public void MaskAddress_ShowsOnlyLastTwoBytes() =>
        Assert.Equal("XX:XX:XX:XX:34:56", DeviceText.MaskAddress(0x0C8A3B123456));

    [Fact]
    public void MaskAddressesInText_MasksInstanceIds() =>
        Assert.Equal(
            @"BTHLE\DEV_XXXXXXXX3456\8&1",
            DeviceText.MaskAddressesInText(@"BTHLE\DEV_0C8A3B123456\8&1"));
}

public class BluetoothIdsTests
{
    [Theory]
    [InlineData("0C8A3B123456", 0x0C8A3B123456UL)]
    [InlineData("0c:8a:3b:12:34:56", 0x0C8A3B123456UL)]
    [InlineData("0C-8A-3B-12-34-56", 0x0C8A3B123456UL)]
    public void TryParseAddress_Valid(string text, ulong expected)
    {
        Assert.True(BluetoothIds.TryParseAddress(text, out ulong address));
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0C8A3B12345")]   // corta
    [InlineData("0C8A3B1234567")] // larga
    [InlineData("0C8A3B12345Z")]  // no hex
    [InlineData("000000000000")]  // cero no es válida
    public void TryParseAddress_Invalid(string? text) =>
        Assert.False(BluetoothIds.TryParseAddress(text, out _));

    [Theory]
    [InlineData(@"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&200E\7&2C2A6C1F&0&A4C6F0123456_C00000000", 0xA4C6F0123456UL)]
    [InlineData(@"BTHENUM\DEV_A4C6F0123456\7&2C2A6C1F&0&BLUETOOTHDEVICE_A4C6F0123456", 0xA4C6F0123456UL)]
    [InlineData(@"BTHLE\DEV_E3F2A1B2C3D4\8&1A2B3C4D&0&E3F2A1B2C3D4", 0xE3F2A1B2C3D4UL)]
    [InlineData(@"BTHLEDEVICE\{0000180F-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&B023_REV&0007_E3F2A1B2C3D4\9&3A1&0&0019", 0xE3F2A1B2C3D4UL)]
    public void TryParseAddressFromInstanceId(string id, ulong expected)
    {
        Assert.True(BluetoothIds.TryParseAddressFromInstanceId(id, out ulong address));
        Assert.Equal(expected, address);
    }

    [Fact]
    public void ToDeviceId_IsStableUppercase() =>
        Assert.Equal("bt:0C8A3B123456", BluetoothIds.ToDeviceId(0x0C8A3B123456));
}

public class DeviceClassifierTests
{
    [Theory]
    [InlineData(0x240404u, DeviceKind.Headphones)] // audio/vídeo, auricular con micro
    [InlineData(0x240418u, DeviceKind.Headphones)] // audio/vídeo, auriculares
    [InlineData(0x240414u, DeviceKind.Speaker)]    // audio/vídeo, altavoz
    [InlineData(0x002540u, DeviceKind.Keyboard)]   // periférico, teclado
    [InlineData(0x002580u, DeviceKind.Mouse)]      // periférico, ratón
    [InlineData(0x002508u, DeviceKind.Gamepad)]    // periférico, mando
    [InlineData(0x5A020Cu, DeviceKind.Phone)]      // teléfono
    [InlineData(0x000704u, DeviceKind.Watch)]      // reloj
    [InlineData(0x000000u, DeviceKind.Unknown)]
    public void FromClassOfDevice(uint raw, DeviceKind expected) =>
        Assert.Equal(expected, DeviceClassifier.FromClassOfDevice(raw));

    [Theory]
    [InlineData((ushort)0x03C1, DeviceKind.Keyboard)]
    [InlineData((ushort)0x03C2, DeviceKind.Mouse)]
    [InlineData((ushort)0x03C4, DeviceKind.Gamepad)]
    [InlineData((ushort)0x0941, DeviceKind.Headphones)]
    [InlineData((ushort)0x00C0, DeviceKind.Watch)]
    [InlineData((ushort)0x0000, DeviceKind.Unknown)]
    public void FromAppearance(ushort raw, DeviceKind expected) =>
        Assert.Equal(expected, DeviceClassifier.FromAppearance(raw));

    [Theory]
    [InlineData("AirPods Pro de Ana", DeviceKind.Headphones)]
    [InlineData("Galaxy Buds2", DeviceKind.Headphones)]
    [InlineData("MX Master 3S", DeviceKind.Mouse)]
    [InlineData("Xbox Wireless Controller", DeviceKind.Gamepad)]
    [InlineData("MX Keys S", DeviceKind.Keyboard)]
    [InlineData("Cosa rara", DeviceKind.Unknown)]
    public void FromName(string name, DeviceKind expected) =>
        Assert.Equal(expected, DeviceClassifier.FromName(name));
}
