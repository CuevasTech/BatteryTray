namespace BatteryTray.Windows.Providers.Usb.Protocols.Logitech;

/// <summary>Una lectura de batería de un aparato Logitech.</summary>
/// <param name="Level">0-100, o null si el aparato no da un nivel ahora mismo.</param>
/// <param name="On">False si el aparato está apagado (p. ej. cascos apagados con el receptor puesto).</param>
internal readonly record struct HidppBatteryState(int? Level, bool Charging, bool On = true);

/// <summary>
/// Interpreta las respuestas de las características de batería de HID++.
/// Fuentes: Linux hid-logitech-hidpp.c (solo como documentación), Solaar (GPL-2.0-or-later),
/// libratbag (MIT) y HeadsetControl (GPL-3.0) para la curva de los cascos.
/// </summary>
internal static class HidppBattery
{
    public const ushort BatteryStatus = 0x1000;
    public const ushort BatteryVoltage = 0x1001;
    public const ushort UnifiedBattery = 0x1004;
    public const ushort AdcMeasurement = 0x1F20;

    /// <summary>
    /// 0x1000 BATTERY_STATUS, función 0: [0] nivel %, [1] siguiente nivel, [2] estado
    /// (0 descargando, 1 cargando, 2 última fase, 3 completa, 4 carga lenta, 5-7 errores).
    /// Con un estado distinto de 0 el nivel suele venir a 0, que significa "desconocido".
    /// </summary>
    public static HidppBatteryState FromBatteryStatus(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3)
        {
            return new HidppBatteryState(null, false);
        }

        int level = p[0];
        byte status = p[2];
        if (status == 3)
        {
            return new HidppBatteryState(100, false);
        }

        bool charging = status is 1 or 2 or 4;
        return new HidppBatteryState(level is > 0 and <= 100 ? level : null, charging);
    }

    /// <summary>
    /// 0x1001 BATTERY_VOLTAGE, función 0: [0..1] milivoltios (big-endian), [2] indicadores:
    /// bit 7 = alimentación externa y entonces bits 0-2: 0 cargando, 1 llena, 2 sin cargar.
    /// El porcentaje sale de la tabla de tensión (aproximado).
    /// </summary>
    public static HidppBatteryState FromBatteryVoltage(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3)
        {
            return new HidppBatteryState(null, false);
        }

        int millivolts = (p[0] << 8) | p[1];
        byte flags = p[2];
        bool external = (flags & 0x80) != 0;
        int status = flags & 0x07;
        if (external && status == 1)
        {
            return new HidppBatteryState(100, false);
        }

        return new HidppBatteryState(VoltageToPercent(millivolts), external && status == 0);
    }

    /// <summary>
    /// 0x1004 UNIFIED_BATTERY. Capacidades (función 0): [1] bit 1 = da porcentaje. Estado
    /// (función 1): [0] %, [1] nivel por bits (8 lleno, 4 bueno, 2 bajo, 1 crítico),
    /// [2] carga (0 descargando, 1-2 cargando, 3 completa).
    /// </summary>
    public static HidppBatteryState FromUnifiedBattery(ReadOnlySpan<byte> status, bool hasPercentage)
    {
        if (status.Length < 3)
        {
            return new HidppBatteryState(null, false);
        }

        if (status[2] == 3)
        {
            return new HidppBatteryState(100, false);
        }

        int? level = hasPercentage && status[0] <= 100
            ? status[0]
            : status[1] switch
            {
                >= 8 => 90,
                >= 4 => 50,
                >= 2 => 20,
                1 => 5,
                _ => null,
            };
        return new HidppBatteryState(level, status[2] is 1 or 2);
    }

    /// <summary>
    /// 0x1F20 ADC_MEASUREMENT (cascos G): [0..1] milivoltios (big-endian), [2] indicadores:
    /// bit 0 = conectado; 0x03 cargando, 0x07 llena. Sin el bit 0, los cascos están apagados.
    /// </summary>
    public static HidppBatteryState FromAdcMeasurement(ReadOnlySpan<byte> p, ushort productId = 0)
    {
        if (p.Length < 3 || (p[2] & 0x01) == 0)
        {
            return new HidppBatteryState(null, false, On: false);
        }

        if (p[2] == 0x07)
        {
            return new HidppBatteryState(100, false);
        }

        int millivolts = (p[0] << 8) | p[1];
        int? level = s_headsetCurves.TryGetValue(productId, out (int Percent, int Mv)[]? curve)
            ? Spline(curve, millivolts)
            : HeadsetVoltageToPercent(millivolts);
        return new HidppBatteryState(level, p[2] == 0x03);
    }

    /// <summary>
    /// Aparatos antiguos HID++ 1.0. Registro 0x0D: [0] %, [2] bits 6-7: 1 cargando, 2 llena.
    /// </summary>
    public static HidppBatteryState FromRegister0D(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3 || p[0] > 100)
        {
            return new HidppBatteryState(null, false);
        }

        int state = p[2] >> 6;
        return state == 2 ? new HidppBatteryState(100, false) : new HidppBatteryState(p[0], state == 1);
    }

    /// <summary>Registro 0x07: [0] nivel (7 → 90, 5 → 50, 3 → 20, 1 → 5), [1] carga (0x21/0x24/0x25 cargando, 0x22/0x26 llena).</summary>
    public static HidppBatteryState FromRegister07(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2)
        {
            return new HidppBatteryState(null, false);
        }

        if (p[1] is 0x22 or 0x26)
        {
            return new HidppBatteryState(100, false);
        }

        int? level = p[0] switch
        {
            7 => 90,
            5 => 50,
            3 => 20,
            1 => 5,
            _ => null,
        };
        return new HidppBatteryState(level, p[1] is 0x21 or 0x24 or 0x25);
    }

    /// <summary>
    /// Tabla de tensión → porcentaje de las baterías de litio de los ratones Logitech
    /// (de 100 % a 1 %, en mV). Es la del controlador de Linux y LGSTrayBattery.
    /// </summary>
    private static readonly ushort[] s_voltageTable =
    [
        4186, 4156, 4143, 4133, 4122, 4113, 4103, 4094, 4086, 4075,
        4067, 4059, 4051, 4043, 4035, 4027, 4019, 4011, 4003, 3997,
        3989, 3983, 3976, 3969, 3961, 3955, 3949, 3942, 3935, 3929,
        3922, 3916, 3909, 3902, 3896, 3890, 3883, 3877, 3870, 3865,
        3859, 3853, 3848, 3842, 3837, 3833, 3828, 3824, 3819, 3815,
        3811, 3808, 3804, 3800, 3797, 3793, 3790, 3787, 3784, 3781,
        3778, 3775, 3772, 3770, 3767, 3764, 3762, 3759, 3757, 3754,
        3751, 3748, 3744, 3741, 3737, 3734, 3730, 3726, 3724, 3720,
        3717, 3714, 3710, 3706, 3702, 3697, 3693, 3688, 3683, 3677,
        3671, 3666, 3662, 3658, 3654, 3646, 3633, 3612, 3579, 3537,
    ];

    /// <summary>Porcentaje = 100 − i para el primer i con mV ≥ tabla[i]; por debajo de todo, 0.</summary>
    public static int? VoltageToPercent(int millivolts)
    {
        if (millivolts is < 2500 or > 5000)
        {
            return null; // fuera de lo posible: dato erróneo
        }

        for (int i = 0; i < s_voltageTable.Length; i++)
        {
            if (millivolts >= s_voltageTable[i])
            {
                return 100 - i;
            }
        }

        return 0;
    }

    // Curvas de cada modelo de cascos (porcentaje ← mV), de HeadsetControl (logitech_calibrations.hpp).
    private static readonly (int Percent, int Mv)[] s_g533 = [(100, 4200), (50, 3850), (30, 3790), (20, 3750), (5, 3680), (0, 3330)];
    private static readonly (int Percent, int Mv)[] s_g535 = [(100, 4175), (50, 3817), (30, 3766), (20, 3730), (5, 3664), (0, 3310)];
    private static readonly (int Percent, int Mv)[] s_g933 = [(100, 4100), (80, 3950), (60, 3850), (40, 3750), (20, 3650), (10, 3500), (5, 3300), (0, 3150)];
    private static readonly (int Percent, int Mv)[] s_gpro = [(100, 4150), (50, 3830), (30, 3780), (20, 3740), (5, 3670), (0, 3320)];

    private static readonly Dictionary<ushort, (int Percent, int Mv)[]> s_headsetCurves = new()
    {
        [0x0A66] = s_g533,
        [0x0AC4] = s_g535,
        [0x0A5C] = s_g933, // G633
        [0x0A89] = s_g933, // G635
        [0x0AB5] = s_g933, // G733
        [0x0AFE] = s_g933,
        [0x0B1F] = s_g933,
        [0x0A5B] = s_g933, // G933
        [0x0A87] = s_g933, // G935
        [0x0AA7] = s_gpro, // G PRO
        [0x0AAA] = s_gpro, // G PRO X
        [0x0ABA] = s_gpro, // G PRO X Wireless
        [0x0AFB] = s_gpro,
        [0x0AFC] = s_gpro,
    };

    /// <summary>Interpolación por tramos sobre una tabla de tensiones descendentes; null por debajo del mínimo.</summary>
    internal static int? Spline((int Percent, int Mv)[] table, int millivolts)
    {
        if (millivolts < table[^1].Mv || millivolts > 5000)
        {
            return null; // por debajo de la tabla: lectura no válida
        }

        if (millivolts >= table[0].Mv)
        {
            return table[0].Percent;
        }

        for (int i = 0; i < table.Length - 1; i++)
        {
            (int pHigh, int vHigh) = table[i];
            (int pLow, int vLow) = table[i + 1];
            if (millivolts >= vLow)
            {
                return pLow + (int)((float)(millivolts - vLow) / (vHigh - vLow) * (pHigh - pLow));
            }
        }

        return table[^1].Percent;
    }

    /// <summary>Curva de los cascos G933/G935/G633 (HeadsetControl, la misma que usa Linux).</summary>
    public static int? HeadsetVoltageToPercent(int millivolts)
    {
        if (millivolts is < 2500 or > 5000)
        {
            return null;
        }

        double x = millivolts;
        double percent = millivolts switch
        {
            <= 3525 => (0.03 * x) - 101,
            > 4030 => 100,
            _ => (3.7268473047e-9 * x * x * x * x) - (5.605626214573775e-5 * x * x * x)
                + (0.3156051902814949 * x * x) - (788.0937250298629 * x) + 736315.3077118985,
        };
        return (int)Math.Round(Math.Clamp(percent, 0, 100));
    }
}
