using System.Diagnostics;
using BatteryTray.Core;
using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb.Protocols;

/// <summary>
/// Mandos de PlayStation (DualShock 4, DualSense, DualSense Edge), Nintendo Switch Pro y
/// Joy-Con, y 8BitDo Ultimate 2 Wireless: la batería va dentro de los informes que el mando
/// envía continuamente, así que basta con escuchar medio segundo y cerrar.
///
/// Fuentes de los formatos: Linux hid-playstation.c y hid-nintendo.c (GPL-2.0-or-later),
/// SDL (SDL_hidapi_ps4.c, _ps5.c, _switch.c, _8bitdo.c; Zlib) y las notas de dekuNukem.
/// Implementación propia.
///
/// Seguridad: nunca se envía nada al mando. Algunas consultas (pedir ciertos informes de
/// característica por Bluetooth) cambian el mando a otro modo y estropean juegos antiguos;
/// por eso, por Bluetooth, solo se lee la batería si el mando ya está en modo completo
/// (lo pone así Steam o cualquier juego moderno). La colección se abre compartida y solo
/// para leer: cada lector recibe su copia de los informes y los juegos no se enteran.
/// Tampoco se deja abierta, para no impedir que DS4Windows la abra en exclusiva.
/// </summary>
internal sealed class PassiveGamepads : IHidBatteryProtocol
{
    private const int ListenMs = 500;
    private const int ReadTimeoutMs = 100;

    private enum Family
    {
        DualShock4,
        DualSense,
        SwitchPro,
        EightBitDo,
    }

    private sealed record Model(Family Family, string Name);

    private static readonly Dictionary<(ushort Vid, ushort Pid), Model> s_models = new()
    {
        [(0x054C, 0x05C4)] = new(Family.DualShock4, "DualShock 4"),
        [(0x054C, 0x09CC)] = new(Family.DualShock4, "DualShock 4"),
        [(0x054C, 0x0BA0)] = new(Family.DualShock4, "DualShock 4"), // adaptador inalámbrico de Sony
        [(0x054C, 0x0CE6)] = new(Family.DualSense, "DualSense"),
        [(0x054C, 0x0DF2)] = new(Family.DualSense, "DualSense Edge"),
        [(0x057E, 0x2009)] = new(Family.SwitchPro, "Nintendo Switch Pro Controller"),
        [(0x057E, 0x2006)] = new(Family.SwitchPro, "Joy-Con (L)"),
        [(0x057E, 0x2007)] = new(Family.SwitchPro, "Joy-Con (R)"),
        [(0x2DC8, 0x6012)] = new(Family.EightBitDo, "8BitDo Ultimate 2 Wireless"),
    };

    private readonly IHidOpener _opener;
    private readonly Func<HidCollection, bool> _isVirtual;

    public PassiveGamepads(IHidOpener? opener = null, Func<HidCollection, bool>? isVirtual = null)
    {
        _opener = opener ?? RealHidOpener.Instance;
        _isVirtual = isVirtual ?? (c => c.InstanceId is string id && CfgMgr.IsVirtualDevice(id));
    }

    public string Name => "pad";

    public bool Supports(HidDeviceGroup device)
    {
        if (!s_models.TryGetValue((device.VendorId, device.ProductId), out Model? model))
        {
            return false;
        }

        // El Switch Pro por USB necesita un "saludo" que cambia su estado: solo Bluetooth.
        if (model.Family == Family.SwitchPro && device.Transport != HidTransport.Bluetooth)
        {
            return false;
        }

        HidCollection? pad = GamepadCollection(device);
        return pad is not null && device.Transport is HidTransport.Usb or HidTransport.Bluetooth && !_isVirtual(pad);
    }

    private static HidCollection? GamepadCollection(HidDeviceGroup device) =>
        device.Find(c => c.UsagePage == 0x01 && c.Usage is 0x05 or 0x04 && c.InputReportLength > 0);

    public IReadOnlyList<HidBatteryReading> Read(HidDeviceGroup device, CancellationToken cancellationToken)
    {
        Model model = s_models[(device.VendorId, device.ProductId)];
        HidCollection pad = GamepadCollection(device)!;
        bool bluetooth = device.Transport == HidTransport.Bluetooth;
        string identity = HidIds.Identity(device);

        using IHidChannel? channel = _opener.OpenForRead(pad);
        if (channel is null)
        {
            return []; // en uso exclusivo por otra app (p. ej. DS4Windows)
        }

        byte[] buffer = new byte[pad.InputReportLength];
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ListenMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = channel.Read(buffer, ReadTimeoutMs);
            if (n < 0)
            {
                return [];
            }

            if (n == 0)
            {
                continue;
            }

            Parsed result = Parse(model.Family, device.ProductId, bluetooth, buffer.AsSpan(0, n));
            switch (result.Kind)
            {
                case ParsedKind.Battery:
                    return [new HidBatteryReading(identity, model.Name, DeviceKind.Gamepad, result.Level, result.Charging)];
                case ParsedKind.NoController:
                    return [new HidBatteryReading(identity, model.Name, DeviceKind.Gamepad, null, false, Connected: false)];
                case ParsedKind.NoBatteryInThisMode:
                    // Por Bluetooth en modo simple no hay batería: se mantiene el último valor conocido.
                    return [new HidBatteryReading(identity, model.Name, DeviceKind.Gamepad, null, false)];
            }
        }

        return [new HidBatteryReading(identity, model.Name, DeviceKind.Gamepad, null, false)];
    }

    internal enum ParsedKind
    {
        Unknown,
        Battery,
        NoController,
        NoBatteryInThisMode,
    }

    internal readonly record struct Parsed(ParsedKind Kind, int? Level = null, bool Charging = false);

    /// <summary>Busca la batería en un informe de entrada (byte 0 = id del informe).</summary>
    private static Parsed Parse(Family family, ushort productId, bool bluetooth, ReadOnlySpan<byte> r) => family switch
    {
        Family.DualShock4 => ParseDualShock4Report(r, bluetooth, productId == 0x0BA0),
        Family.DualSense => ParseDualSenseReport(r, bluetooth),
        Family.SwitchPro => ParseSwitchReport(r),
        _ => Parse8BitDoReport(r, bluetooth),
    };

    internal static Parsed ParseDualShock4Report(ReadOnlySpan<byte> r, bool bluetooth, bool sonyAdapter)
    {
        if (r.Length == 0)
        {
            return default;
        }

        if (!bluetooth && r[0] == 0x01 && r.Length >= 32)
        {
            // Con el adaptador de Sony sin mando emparejado, el bit 2 del byte 31 lo indica.
            if (sonyAdapter && (r[31] & 0x04) != 0)
            {
                return new Parsed(ParsedKind.NoController);
            }

            return Ds4Battery(r[30]);
        }

        if (bluetooth)
        {
            if (r[0] is >= 0x11 and <= 0x19 && r.Length >= 78 && (r[1] & 0x80) != 0 && Crc32Matches(r))
            {
                return Ds4Battery(r[32]);
            }

            if (r[0] == 0x01)
            {
                return new Parsed(ParsedKind.NoBatteryInThisMode);
            }
        }

        return default;
    }

    /// <summary>Linux dualshock4_parse_report: bit 4 = cable, bits 0-3 = nivel en décimas.</summary>
    internal static Parsed Ds4Battery(byte b)
    {
        bool cable = (b & 0x10) != 0;
        int v = b & 0x0F;
        if (cable)
        {
            return v switch
            {
                < 10 => new Parsed(ParsedKind.Battery, v * 10 + 5, Charging: true),
                10 => new Parsed(ParsedKind.Battery, 100, Charging: true),
                11 => new Parsed(ParsedKind.Battery, 100),
                _ => default, // error o desconocido
            };
        }

        return new Parsed(ParsedKind.Battery, v < 10 ? v * 10 + 5 : 100);
    }

    internal static Parsed ParseDualSenseReport(ReadOnlySpan<byte> r, bool bluetooth)
    {
        if (r.Length == 0)
        {
            return default;
        }

        if (!bluetooth && r[0] == 0x01 && r.Length >= 55)
        {
            return DualSenseBattery(r[53]);
        }

        if (bluetooth)
        {
            if (r[0] == 0x31 && r.Length >= 78 && Crc32Matches(r))
            {
                return DualSenseBattery(r[54]);
            }

            if (r[0] == 0x01)
            {
                return new Parsed(ParsedKind.NoBatteryInThisMode);
            }
        }

        return default;
    }

    /// <summary>Linux dualsense_parse_report: bits 0-3 nivel en décimas, bits 4-7 estado de carga.</summary>
    internal static Parsed DualSenseBattery(byte b)
    {
        int level = Math.Min((b & 0x0F) * 10 + 5, 100);
        return (b >> 4) switch
        {
            0x0 => new Parsed(ParsedKind.Battery, level),
            0x1 => new Parsed(ParsedKind.Battery, level, Charging: true),
            0x2 => new Parsed(ParsedKind.Battery, 100),
            0xA or 0xB => new Parsed(ParsedKind.Battery, level), // no carga por tensión o temperatura
            _ => default, // error de carga
        };
    }

    /// <summary>
    /// Switch Pro / Joy-Con por Bluetooth: en los informes completos (0x30, 0x31) y en las
    /// respuestas (0x21), el byte 2 lleva la batería (nibble alto: 8 llena, 6 media, 4 baja,
    /// 2 crítica, 0 vacía) y el bit 4 indica carga. El informe simple (0x3F) no la lleva.
    /// </summary>
    internal static Parsed ParseSwitchReport(ReadOnlySpan<byte> r)
    {
        if (r.Length >= 3 && r[0] is 0x30 or 0x31 or 0x21)
        {
            int level = ((r[2] & 0xE0) >> 4) * 100 / 8;
            return new Parsed(ParsedKind.Battery, Math.Min(level, 100), Charging: (r[2] & 0x10) != 0);
        }

        return r.Length > 0 && r[0] == 0x3F ? new Parsed(ParsedKind.NoBatteryInThisMode) : default;
    }

    /// <summary>8BitDo Ultimate 2 Wireless: byte 14 = % (bits 0-6) y carga (bit 7) en su informe 0x01 (BT) o 0x04 (USB).</summary>
    internal static Parsed Parse8BitDoReport(ReadOnlySpan<byte> r, bool bluetooth)
    {
        byte expected = bluetooth ? (byte)0x01 : (byte)0x04;
        if (r.Length >= 34 && r[0] == expected)
        {
            int level = r[14] & 0x7F;
            if (level is > 0 and <= 100) // 0 es relleno
            {
                return new Parsed(ParsedKind.Battery, level, Charging: (r[14] & 0x80) != 0);
            }
        }

        return default;
    }

    /// <summary>CRC-32 de los informes completos por Bluetooth: semilla 0xA1 + bytes 0..73, comparado con 74..77.</summary>
    internal static bool Crc32Matches(ReadOnlySpan<byte> r)
    {
        if (r.Length < 78)
        {
            return false;
        }

        uint crc = Crc32Update(0xFFFFFFFF, 0xA1);
        foreach (byte b in r[..74])
        {
            crc = Crc32Update(crc, b);
        }

        crc = ~crc;
        uint expected = (uint)(r[74] | (r[75] << 8) | (r[76] << 16) | (r[77] << 24));
        return crc == expected;
    }

    private static uint Crc32Update(uint crc, byte b)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return crc;
    }
}
