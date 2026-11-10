namespace BatteryTray.Core;

/// <summary>Deduce el tipo de dispositivo a partir de los datos Bluetooth o del nombre.</summary>
public static class DeviceClassifier
{
    /// <summary>
    /// Bluetooth clásico: "Class of Device" de 24 bits.
    /// Bits 8-12 = clase mayor, bits 2-7 = clase menor (especificación Bluetooth "Assigned Numbers").
    /// </summary>
    public static DeviceKind FromClassOfDevice(uint raw)
    {
        uint major = (raw >> 8) & 0x1F;
        uint minor = (raw >> 2) & 0x3F;
        return major switch
        {
            1 => DeviceKind.Computer,
            2 => DeviceKind.Phone,
            4 => minor switch
            {
                1 or 2 or 6 => DeviceKind.Headphones, // auricular con micro, manos libres, auriculares
                5 or 7 => DeviceKind.Speaker,         // altavoz, audio portátil
                _ => DeviceKind.Unknown,
            },
            5 => FromPeripheralMinor(minor),
            7 => minor == 1 ? DeviceKind.Watch : DeviceKind.Unknown,
            _ => DeviceKind.Unknown,
        };
    }

    private static DeviceKind FromPeripheralMinor(uint minor)
    {
        uint low = minor & 0x0F;
        if (low is 1 or 2)
        {
            return DeviceKind.Gamepad; // joystick, mando
        }

        if (low == 7)
        {
            return DeviceKind.Pen;
        }

        return (minor >> 4) switch
        {
            1 => DeviceKind.Keyboard,
            2 => DeviceKind.Mouse,
            3 => DeviceKind.Keyboard, // combo teclado + ratón
            _ => DeviceKind.Unknown,
        };
    }

    /// <summary>
    /// Bluetooth LE: valor "Appearance" de 16 bits.
    /// Bits 6-15 = categoría, bits 0-5 = subcategoría.
    /// </summary>
    public static DeviceKind FromAppearance(ushort raw)
    {
        int category = raw >> 6;
        int sub = raw & 0x3F;
        return category switch
        {
            0x001 => DeviceKind.Phone,
            0x002 => DeviceKind.Computer,
            0x003 => DeviceKind.Watch,
            0x00F => sub switch
            {
                1 => DeviceKind.Keyboard,
                2 or 9 => DeviceKind.Mouse,   // ratón, touchpad
                3 or 4 => DeviceKind.Gamepad, // joystick, mando
                5 or 7 => DeviceKind.Pen,     // tableta digitalizadora, lápiz
                _ => DeviceKind.Unknown,
            },
            0x021 or 0x024 => DeviceKind.Speaker,     // altavoz, receptor de audio
            0x025 or 0x029 => DeviceKind.Headphones,  // audio para llevar puesto, audífono
            _ => DeviceKind.Unknown,
        };
    }

    private static readonly (string Needle, DeviceKind Kind)[] s_nameHints =
    [
        ("airpods", DeviceKind.Headphones),
        ("buds", DeviceKind.Headphones),
        ("headphone", DeviceKind.Headphones),
        ("headset", DeviceKind.Headphones),
        ("earbud", DeviceKind.Headphones),
        ("auricular", DeviceKind.Headphones),
        ("cascos", DeviceKind.Headphones),
        ("wh-1000", DeviceKind.Headphones),
        ("wf-1000", DeviceKind.Headphones),
        ("controller", DeviceKind.Gamepad),
        ("gamepad", DeviceKind.Gamepad),
        ("mando", DeviceKind.Gamepad),
        ("dualsense", DeviceKind.Gamepad),
        ("dualshock", DeviceKind.Gamepad),
        ("joy-con", DeviceKind.Gamepad),
        ("8bitdo", DeviceKind.Gamepad),
        ("mouse", DeviceKind.Mouse),
        ("ratón", DeviceKind.Mouse),
        ("raton", DeviceKind.Mouse),
        ("mx master", DeviceKind.Mouse),
        ("mx anywhere", DeviceKind.Mouse),
        ("trackpad", DeviceKind.Mouse),
        ("keyboard", DeviceKind.Keyboard),
        ("teclado", DeviceKind.Keyboard),
        ("keys", DeviceKind.Keyboard),
        ("stylus", DeviceKind.Pen),
        ("pen", DeviceKind.Pen),
        ("lápiz", DeviceKind.Pen),
        ("speaker", DeviceKind.Speaker),
        ("altavoz", DeviceKind.Speaker),
        ("soundlink", DeviceKind.Speaker),
        ("watch", DeviceKind.Watch),
        ("reloj", DeviceKind.Watch),
    ];

    /// <summary>Último recurso: pistas en el nombre.</summary>
    public static DeviceKind FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DeviceKind.Unknown;
        }

        foreach ((string needle, DeviceKind kind) in s_nameHints)
        {
            if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return DeviceKind.Unknown;
    }

    /// <summary>Devuelve el primer tipo conocido de la lista.</summary>
    public static DeviceKind FirstKnown(params ReadOnlySpan<DeviceKind> kinds)
    {
        foreach (DeviceKind k in kinds)
        {
            if (k != DeviceKind.Unknown)
            {
                return k;
            }
        }

        return DeviceKind.Unknown;
    }
}
