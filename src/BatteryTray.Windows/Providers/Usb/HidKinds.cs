using BatteryTray.Core;
using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb;

/// <summary>Tipo de aparato según sus colecciones HID estándar (para elegir el icono).</summary>
internal static class HidKinds
{
    public static DeviceKind Guess(HidDeviceGroup device)
    {
        bool Has(ushort page, ushort usage) => device.Collections.Any(c => c.UsagePage == page && c.Usage == usage);

        // Generic Desktop (0x01): 0x02 ratón, 0x06 teclado, 0x04 joystick, 0x05 mando.
        if (Has(0x01, 0x05) || Has(0x01, 0x04))
        {
            return DeviceKind.Gamepad;
        }

        if (Has(0x01, 0x02))
        {
            return Has(0x01, 0x06) && device.Product is string p && p.Contains("keyboard", StringComparison.OrdinalIgnoreCase)
                ? DeviceKind.Keyboard
                : DeviceKind.Mouse;
        }

        if (Has(0x01, 0x06))
        {
            return DeviceKind.Keyboard;
        }

        // Telephony (0x0B) = cascos con micrófono; Digitizers (0x0D) 0x02 = lápiz.
        if (device.Collections.Any(c => c.UsagePage == 0x0B))
        {
            return DeviceKind.Headphones;
        }

        if (Has(0x0D, 0x02))
        {
            return DeviceKind.Pen;
        }

        return DeviceClassifier.FromName(device.Product);
    }
}
