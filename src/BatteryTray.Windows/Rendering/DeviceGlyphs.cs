using BatteryTray.Core;

namespace BatteryTray.Windows.Rendering;

/// <summary>Iconos de la fuente del sistema (Segoe Fluent Icons / Segoe MDL2 Assets) por tipo de dispositivo.</summary>
public static class DeviceGlyphs
{
    public const string Bluetooth = "";

    public static string For(DeviceKind kind) => kind switch
    {
        DeviceKind.Headphones => "", // Headphone
        DeviceKind.Speaker => "",    // Speakers
        DeviceKind.Mouse => "",      // Mouse
        DeviceKind.Keyboard => "",   // KeyboardClassic
        DeviceKind.Gamepad => "",    // Game
        DeviceKind.Pen => "",        // Pen
        DeviceKind.Phone => "",      // CellPhone
        DeviceKind.Computer => "",   // DeviceLaptopNoPic
        _ => Bluetooth,
    };
}
