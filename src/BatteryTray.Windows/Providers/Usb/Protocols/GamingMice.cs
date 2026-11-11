using BatteryTray.Core;
using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb.Protocols;

/// <summary>
/// Ratones y teclados inalámbricos de juego con receptor USB: SteelSeries (Aerox, Prime,
/// Rival), HyperX (Pulsefire Dart, Haste, Haste 2), ASUS ROG/TUF y las familias de
/// receptores Compx/Nordic (Pulsar, ATK/VXE, VGN, Kysona, Lamzu Atlantis…) y SinoWealth
/// (Glorious, Lamzu, WLmouse, Attack Shark…).
///
/// Formatos de: rivalcfg (WTFPL), santeri3700 (CC0) y ngenuity-linux-cli (MIT) para HyperX,
/// G-Helper (GPL-3.0) para ASUS, el controlador de Linux hid-pulsar.c (GPL-2.0-or-later) para
/// Compx, y mxw (MIT) y HaloBattery (MIT) para SinoWealth. Implementación propia.
///
/// Seguridad: solo los modelos de la lista y solo la consulta de batería de cada uno (todas
/// documentadas como lectura). En varios protocolos el comando de batería está a un byte de
/// uno que cambia ajustes (p. ej. HyperX 50 02 frente a 50 01), así que las peticiones son
/// constantes fijas. Cada respuesta se valida (eco, suma de comprobación y rango).
/// </summary>
internal sealed class GamingMice : IHidBatteryProtocol
{
    internal sealed record Model(string Name, DeviceKind Kind, ushort VendorId, ushort[] ProductIds, Func<HidCollection, ushort, bool> Collection, Func<IHidChannel, ushort, HeadsetResult?> Read, Func<HidCollection, IHidOpener, IHidChannel?>? Open = null);

    private static Func<HidCollection, ushort, bool> Usage(ushort page, ushort usage, int? mi = null) =>
        (c, _) => c.UsagePage == page && c.Usage == usage && (mi is null || c.InterfaceNumber == mi) && c.OutputReportLength > 0;

    // ================================================================ SteelSeries

    private static readonly HashSet<ushort> s_steelCable = [0x183A, 0x187A, 0x1854, 0x185E, 0x1862, 0x185A, 0x1876, 0x1842, 0x184A, 0x1892, 0x172B];

    /// <summary>
    /// Aerox/Prime: 00 D2 (receptor) o 00 92 (cable) → [0] = eco, [1] = B: bit 7 cargando,
    /// (B &amp; 0x7F) de 1 a 21 = (n − 1) × 5 %. "40 FF" = ratón dormido.
    /// </summary>
    internal static HeadsetResult? SteelAerox(IHidChannel ch, ushort pid)
    {
        byte cmd = s_steelCable.Contains(pid) ? (byte)0x92 : (byte)0xD2;
        byte[]? r = SteelQuery(ch, cmd, x => x.Length >= 2 && x[0] == cmd);
        if (r is null)
        {
            return null;
        }

        int v = r[1] & 0x7F;
        return v is >= 1 and <= 21 ? new HeadsetResult((v - 1) * 5, (r[1] & 0x80) != 0) : null;
    }

    /// <summary>Aerox 3 Wireless (2.ª gen.): [0] = eco, [1] = cargando, [2] = % (0xFF = aún no se sabe).</summary>
    internal static HeadsetResult? SteelAeroxGen2(IHidChannel ch, ushort pid)
    {
        byte cmd = s_steelCable.Contains(pid) ? (byte)0x92 : (byte)0xD2;
        byte[]? r = SteelQuery(ch, cmd, x => x.Length >= 3 && x[0] == cmd);
        return r is null || r[2] > 100 ? null : new HeadsetResult(r[2], r[1] == 1);
    }

    /// <summary>Rival 3 Wireless / Rival 650: 00 AA 01 → nivel, 0, cargando (algunos con eco AA delante).</summary>
    internal static HeadsetResult? SteelRival(IHidChannel ch, ushort pid)
    {
        var io = new HeadsetIo(ch);
        byte[]? r = io.Exchange([0x00, 0xAA, 0x01], x => ParseRival(x) is not null, timeoutMs: 500);
        return r is null ? null : ParseRival(r);
    }

    internal static HeadsetResult? ParseRival(byte[] x)
    {
        ReadOnlySpan<byte> p = x.Length >= 4 && x[0] == 0xAA ? x.AsSpan(1) : x.AsSpan();
        return p.Length >= 3 && p[0] <= 100 && p[1] == 0 && p[2] <= 1 ? new HeadsetResult(p[0], p[2] == 1) : null;
    }

    /// <summary>Consulta SteelSeries con reintentos: el enlace se duerme 1-2 s sin movimiento y responde "40 FF".</summary>
    private static byte[]? SteelQuery(IHidChannel ch, byte cmd, Func<byte[], bool> accept)
    {
        var io = new HeadsetIo(ch);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[]? asleep = null;
            byte[]? r = io.Exchange([0x00, cmd], x =>
            {
                if (x.Length >= 2 && x[0] == 0x40 && x[1] == 0xFF)
                {
                    asleep = x;
                    return true;
                }

                return accept(x);
            }, timeoutMs: 500);
            if (r is not null && asleep is null)
            {
                return r;
            }

            Thread.Sleep(200);
        }

        return null;
    }

    // ================================================================ HyperX

    /// <summary>Pulsefire Dart / Haste Wireless: 00 51 → [0] = 0x51, [4] = %, [5] = carga (1 cargando, 2 llena).</summary>
    internal static HeadsetResult? HyperXLegacy(IHidChannel ch, ushort pid)
    {
        var io = new HeadsetIo(ch);
        byte[]? r = io.Exchange([0x00, 0x51], x => x.Length >= 6 && x[0] == 0x51, timeoutMs: 500);
        if (r is null || r[4] > 100)
        {
            return null;
        }

        if (r[5] == 2)
        {
            return new HeadsetResult(100, false);
        }

        return r[4] == 0 ? null : new HeadsetResult(r[4], r[5] == 1);
    }

    /// <summary>
    /// Pulsefire Haste 2 Wireless ("universal"): informe 0x50 con 02 → informe 0x51 02 % estado
    /// (1 cargando, 2 llena). Ojo: 50 01 cambiaría el umbral de batería baja; nunca se envía.
    /// </summary>
    internal static HeadsetResult? HyperXUniversal(IHidChannel ch, ushort pid)
    {
        var io = new HeadsetIo(ch);
        byte[]? r = io.Exchange([0x50, 0x02], x => x.Length >= 4 && x[0] == 0x51 && x[1] == 0x02, timeoutMs: 800, maxReads: 24);
        if (r is null || r[2] > 100)
        {
            return null;
        }

        return r[3] == 2 ? new HeadsetResult(100, false) : new HeadsetResult(r[2], r[3] == 1);
    }

    // ================================================================ ASUS ROG / TUF

    private static readonly HashSet<ushort> s_asusQuarters = [0x18E5, 0x18E3, 0x1908, 0x1906, 0x1960, 0x195E, 0x1A59, 0x1A57, 0x1949, 0x1947, 0x18A0];
    private static readonly HashSet<ushort> s_asusFalchion = [0x193C, 0x193E, 0x1B04];
    private const ushort AsusOmni = 0x1ACE;
    private const ushort AsusHarpe2Dongle = 0x1AD0;

    /// <summary>Id de informe del ratón ASUS: 3 en el receptor Omni y el del Harpe II Ace; 0 en el resto.</summary>
    private static byte AsusMouseReportId(ushort pid) => pid is AsusOmni or AsusHarpe2Dongle ? (byte)3 : (byte)0;

    /// <summary>
    /// Ratón ASUS: [rid 12 07] → respuesta [rid 12 07 …]: batería en [5] (×25 en los modelos
    /// antiguos; Strix Carry en [7] ×25), cargando si [10] ≠ 0. "FF AA" = dormido o desconectado.
    /// </summary>
    internal static HeadsetResult? AsusMouse(IHidChannel ch, ushort pid)
    {
        byte rid = AsusMouseReportId(pid);
        var io = new HeadsetIo(ch, stripReportZero: false);
        byte[]? r = io.Exchange([rid, 0x12, 0x07], x => x.Length >= 11 && x[0] == rid && ((x[1] == 0x12 && x[2] == 0x07) || (x[1] == 0xFF && x[2] == 0xAA)), timeoutMs: 300 * 3);
        if (r is null || r[1] == 0xFF)
        {
            return null;
        }

        int level = pid == 0x18B4 ? r[7] * 25 : s_asusQuarters.Contains(pid) ? r[5] * 25 : r[5];
        bool charging = r[10] != 0;
        if (level > 100 || (level == 0 && !charging))
        {
            return null; // 0 sin cargar = en reposo, no "0 %"
        }

        return new HeadsetResult(level, charging);
    }

    /// <summary>Teclado ASUS: [rid 12 01] → [6] = % ([11] en los Falchion), [9] = 1 cargando.</summary>
    internal static HeadsetResult? AsusKeyboard(IHidChannel ch, ushort pid)
    {
        byte rid = pid == AsusOmni ? (byte)2 : (byte)0;
        var io = new HeadsetIo(ch, stripReportZero: false);
        byte[]? r = io.Exchange([rid, 0x12, 0x01], x => x.Length >= 12 && x[0] == rid && ((x[1] == 0x12 && x[2] == 0x01) || (x[1] == 0xFF && x[2] == 0xAA)), timeoutMs: 900);
        if (r is null || r[1] == 0xFF)
        {
            return null;
        }

        int level = s_asusFalchion.Contains(pid) ? r[11] : r[6];
        return level is > 0 and <= 100 ? new HeadsetResult(level, r[9] == 1) : null;
    }

    // ================================================================ Compx / Nordic (P1)

    /// <summary>
    /// Trama de 17 bytes (informe 8): 08 04 … 49 → respuesta 08 04 …: [6] = %, [7] = 1 por cable.
    /// Los 17 bytes de una trama válida suman 0x55.
    /// </summary>
    internal static HeadsetResult? Compx(IHidChannel ch, ushort pid)
    {
        var io = new HeadsetIo(ch, stripReportZero: false);
        byte[]? r = io.Exchange(CompxRequest(), x => ParseCompx(x) is not null, timeoutMs: 600);
        return r is null ? null : ParseCompx(r);
    }

    internal static byte[] CompxRequest()
    {
        byte[] r = new byte[17];
        r[0] = 0x08;
        r[1] = 0x04;
        r[16] = (byte)(0x55 - 0x08 - 0x04);
        return r;
    }

    internal static HeadsetResult? ParseCompx(byte[] x)
    {
        if (x.Length < 17 || x[0] != 0x08 || x[1] != 0x04 || x[6] > 100)
        {
            return null;
        }

        int sum = 0;
        for (int i = 0; i < 17; i++)
        {
            sum += x[i];
        }

        if ((sum & 0xFF) != 0x55)
        {
            return null; // se han visto tramas sueltas con valores basura: sin suma correcta no vale
        }

        bool cable = x[7] == 1;
        return new HeadsetResult(x[6], cable && x[6] < 100);
    }

    // ================================================================ SinoWealth (P2)

    /// <summary>
    /// Informe de característica de 65 bytes: 00 00 00 02 02 00 83 → respuesta [1] = A1 (bien),
    /// A0 ocupado, A4 dormido; [6] = 83, [7] = cargando, [8] = % (el 0 significa 1 %).
    /// </summary>
    internal static HeadsetResult? SinoWealth(IHidChannel ch, ushort pid)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[] request = new byte[65];
            request[3] = 0x02;
            request[4] = 0x02;
            request[6] = 0x83;
            if (!ch.SetFeature(request))
            {
                return null;
            }

            Thread.Sleep(80);
            byte[] reply = new byte[65];
            if (!ch.GetFeature(reply))
            {
                return null;
            }

            switch (reply[1])
            {
                case 0xA1 when reply[6] == 0x83:
                    int level = reply[8] == 0 ? 1 : reply[8];
                    return level <= 100 ? new HeadsetResult(level, reply[7] != 0) : null;
                case 0xA4:
                    return null; // sin enlace: dormido
            }

            Thread.Sleep(100); // ocupado
        }

        return null;
    }

    private static IHidChannel? OpenFeature(HidCollection c, IHidOpener opener) => opener.OpenForQuery(c);

    private static bool SinoWealthCollection(HidCollection c, ushort pid) => c.UsagePage == 0xFFFF && c.FeatureReportLength == 65;

    private static bool CompxCollection(HidCollection c, ushort pid) =>
        c.UsagePage is 0xFF02 or 0xFF04 && c.Usage == 0x0002 && c.InputReportLength == 17 && c.OutputReportLength == 17;

    // ================================================================ catálogo

    internal static IReadOnlyList<Model> Models { get; } =
    [
        new("SteelSeries Aerox / Prime Wireless", DeviceKind.Mouse, 0x1038, [0x1838, 0x183A, 0x1878, 0x187A, 0x1852, 0x1854, 0x185C, 0x185E, 0x1860, 0x1862, 0x1858, 0x185A, 0x1874, 0x1876, 0x1840, 0x1842, 0x1848, 0x184A], Usage(0xFFC0, 0x0001, 3), SteelAerox),
        new("SteelSeries Aerox 3 Wireless", DeviceKind.Mouse, 0x1038, [0x1890, 0x1892], Usage(0xFFC0, 0x0001, 3), SteelAeroxGen2),
        new("SteelSeries Rival 3 Wireless", DeviceKind.Mouse, 0x1038, [0x1830, 0x1872], Usage(0xFFC0, 0x0001, 3), SteelRival),
        new("SteelSeries Rival 650 Wireless", DeviceKind.Mouse, 0x1038, [0x1726, 0x172B], Usage(0xFFC0, 0x0001, 0), SteelRival),

        new("HyperX Pulsefire Dart", DeviceKind.Mouse, 0x0951, [0x16E1, 0x16E2], (c, _) => c.UsagePage is 0xFF00 or 0xFF13 && c.OutputReportLength >= 64, HyperXLegacy),
        new("HyperX Pulsefire Dart", DeviceKind.Mouse, 0x03F0, [0x068E, 0x088E], (c, _) => c.UsagePage is 0xFF00 or 0xFF13 && c.OutputReportLength >= 64, HyperXLegacy),
        new("HyperX Pulsefire Haste Wireless", DeviceKind.Mouse, 0x03F0, [0x028E, 0x048E], (c, _) => c.UsagePage == 0xFF00 && c.Usage == 0x0001 && c.OutputReportLength >= 64, HyperXLegacy),
        new("HyperX Pulsefire Haste 2 Wireless", DeviceKind.Mouse, 0x03F0, [0x0F98, 0x0D97], (c, _) => c.UsagePage == 0xFF00 && c.OutputReportLength >= 64 && c.InputReportLength >= 64, HyperXUniversal),

        new("ASUS ROG", DeviceKind.Mouse, 0x0B05,
            [0x1A1A, 0x1A18, 0x18E5, 0x18E3, 0x197F, 0x197D, 0x1A72, 0x1A70, 0x1B0C, 0x1B0A, 0x1A94, 0x1A92, 0x1C69, 0x1960, 0x195E, 0x1A59, 0x1A57,
             0x1A68, 0x1A66, 0x1908, 0x1906, 0x1979, 0x1977, 0x1949, 0x1947, 0x19F4, 0x1A8D, 0x1AF5, 0x1AF3, 0x1C57, 0x1C56],
            (c, _) => c.UsagePage == 0xFF01 && c.InterfaceNumber == 0 && c.OutputReportLength >= 64, AsusMouse),
        new("ASUS ROG Gladius II Wireless", DeviceKind.Mouse, 0x0B05, [0x18A0], (c, _) => c.UsagePage == 0xFF01 && c.InterfaceNumber == 2 && c.OutputReportLength >= 64, AsusMouse),
        new("ASUS ROG Strix Carry", DeviceKind.Mouse, 0x0B05, [0x18B4], (c, _) => c.UsagePage == 0xFF01 && c.InterfaceNumber == 1 && c.OutputReportLength >= 64, AsusMouse),
        new("ASUS ProArt MD301", DeviceKind.Mouse, 0x0B05, [0x1CD3, 0x1CD5], (c, _) => c.UsagePage == 0xFF01 && c.InterfaceNumber == 3 && c.OutputReportLength >= 64, AsusMouse),
        new("ASUS MD200", DeviceKind.Mouse, 0x0B05, [0x1A24], (c, _) => c.UsagePage == 0xFF01 && c.InterfaceNumber == 2 && c.OutputReportLength >= 64, AsusMouse),
        new("ASUS ROG", DeviceKind.Mouse, 0x0B05, [AsusOmni, AsusHarpe2Dongle], (c, _) => c.UsagePage == 0xFF01 && c.InterfaceNumber == 2 && c.CollectionNumber == 3, AsusMouse),
        new("ASUS ROG", DeviceKind.Keyboard, 0x0B05,
            [0x1A83, 0x1A85, 0x1B3F, 0x1CEF, 0x1C24, 0x1C25, 0x196B, 0x193C, 0x193E, 0x1B04, 0x1AAE, 0x1B78, 0x1A07],
            (c, _) => c.UsagePage == 0xFF00 && c.OutputReportLength >= 64 && c.InterfaceNumber == 1, AsusKeyboard),

        new("Pulsar", DeviceKind.Mouse, 0x3554, [0xF508, 0xF509, 0xF507], CompxCollection, Compx),
        new("Pulsar X2 CrazyLight", DeviceKind.Mouse, 0x3710, [0x5406, 0x3414, 0x3527], CompxCollection, Compx),
        new("Lamzu Atlantis", DeviceKind.Mouse, 0x3554, [0xF50D, 0xF510, 0xF50F], CompxCollection, Compx),
        new("VXE / VGN", DeviceKind.Mouse, 0x3554, [0xF58A, 0xF58C, 0xF58E, 0xF58F, 0xF503, 0xF502, 0xFB3E, 0xFB3D, 0xFB56, 0xFB57], CompxCollection, Compx),
        new("Kysona M600", DeviceKind.Mouse, 0x3554, [0xF57C, 0xF5D5, 0xF57D], CompxCollection, Compx),
        new("Zaopin / Scyrox", DeviceKind.Mouse, 0x3554, [0xF524, 0xF526, 0xF527, 0xF5F7, 0xF5F6], CompxCollection, Compx),
        new("ATK", DeviceKind.Mouse, 0x373B, [0x1031, 0x102E, 0x11D9, 0x11B6, 0x10C9, 0x1107, 0x104D, 0x103F, 0x1040, 0x104C, 0x1085, 0x1115], CompxCollection, Compx),
        new("Dareu", DeviceKind.Mouse, 0x260D, [0x1175, 0x1193, 0x1154, 0x1196], CompxCollection, Compx),
        new("Hitscan Hyperlight", DeviceKind.Mouse, 0x3770, [0x0200, 0x0100], CompxCollection, Compx),

        new("Glorious", DeviceKind.Mouse, 0x258A,
            [0x2022, 0x2011, 0x2024, 0x2013, 0x2023, 0x2012, 0x2025, 0x2014, 0x2027, 0x2015, 0x2029, 0x2017, 0x2033, 0x2019, 0x2034, 0x201A,
             0x2035, 0x201B, 0x2036, 0x201C, 0x2031, 0x2018, 0x2039, 0x201D],
            SinoWealthCollection, SinoWealth, OpenFeature),
        new("Lamzu", DeviceKind.Mouse, 0x373E, [0x001E, 0x001D, 0x001C], SinoWealthCollection, SinoWealth, OpenFeature),
        new("Attack Shark", DeviceKind.Mouse, 0x373E, [0x0046, 0x0047, 0x0021, 0x0022, 0x003A, 0x003B], SinoWealthCollection, SinoWealth, OpenFeature),
        new("Lamzu", DeviceKind.Mouse, 0x37B0, [0x0010, 0x000F, 0x0009, 0x000E, 0x000D, 0x0007, 0x0001, 0x0003, 0x0040], SinoWealthCollection, SinoWealth, OpenFeature),
        new("WLmouse", DeviceKind.Mouse, 0x36A7, [0xA887, 0xA868, 0xA880], SinoWealthCollection, SinoWealth, OpenFeature),
        new("MCHOSE A5 Pro Max", DeviceKind.Mouse, 0x2023, [0xF013, 0xF015, 0xF019], SinoWealthCollection, SinoWealth, OpenFeature),
    ];

    private readonly IHidOpener _opener;

    public GamingMice(IHidOpener? opener = null) => _opener = opener ?? RealHidOpener.Instance;

    public string Name => "mouse";

    internal static Model? Find(HidDeviceGroup device) =>
        Models.FirstOrDefault(m => m.VendorId == device.VendorId && m.ProductIds.Contains(device.ProductId));

    public bool Supports(HidDeviceGroup device) =>
        device.IsUsb && Find(device) is Model m && device.Collections.Any(c => m.Collection(c, device.ProductId));

    public IReadOnlyList<HidBatteryReading> Read(HidDeviceGroup device, CancellationToken cancellationToken)
    {
        Model model = Find(device)!;
        string name = UsefulName(device.Product) ?? model.Name;

        // Los de SinoWealth: primero la colección de uso 0 y luego la de uso 1.
        foreach (HidCollection collection in device.Collections.Where(c => model.Collection(c, device.ProductId)).OrderBy(c => c.Usage))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using IHidChannel? channel = model.Open?.Invoke(collection, _opener) ?? _opener.Open(collection);
            if (channel is null)
            {
                continue;
            }

            if (model.Read(channel, device.ProductId) is HeadsetResult r)
            {
                return [new HidBatteryReading(HidIds.Identity(device), name, model.Kind, r.Level, r.Charging, Connected: r.On)];
            }
        }

        // Dormido, apagado o sin enlace: se mantiene el último nivel conocido.
        return [new HidBatteryReading(HidIds.Identity(device), name, model.Kind, null, false)];
    }

    /// <summary>El nombre del receptor solo vale si no es genérico ("2.4G Wireless Receiver"…).</summary>
    internal static string? UsefulName(string? product)
    {
        if (string.IsNullOrWhiteSpace(product) || product.Length < 4)
        {
            return null;
        }

        string p = product.ToUpperInvariant();
        return p.Contains("RECEIVER", StringComparison.Ordinal) || p.Contains("DONGLE", StringComparison.Ordinal) || p.Contains("2.4G", StringComparison.Ordinal)
            || p.Contains("USB DEVICE", StringComparison.Ordinal) || p.Contains("WIRELESS DEVICE", StringComparison.Ordinal)
            ? null
            : product;
    }
}
