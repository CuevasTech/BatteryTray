using System.Diagnostics;
using BatteryTray.Core;
using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb.Protocols;

/// <summary>Lo que da un casco: nivel (o null), si carga y si está encendido.</summary>
internal readonly record struct HeadsetResult(int? Level, bool Charging, bool On = true)
{
    public static HeadsetResult Off { get; } = new(null, false, On: false);
}

/// <summary>
/// E/S con la convención de hidapi (la que usan HeadsetControl y los demás proyectos): en
/// los informes sin número (id 0) se quita ese 0 al leer, así los índices de bytes coinciden
/// con los documentados.
/// </summary>
internal sealed class HeadsetIo(IHidChannel channel, bool stripReportZero = true)
{
    public HidCollection Collection => channel.Collection;

    public IHidChannel Channel => channel;

    public bool Send(params byte[] request) => channel.Write(request, 1000);

    public byte[]? Receive(int timeoutMs)
    {
        byte[] buffer = new byte[Math.Max(channel.Collection.InputReportLength, 64)];
        int n = channel.Read(buffer, timeoutMs);
        if (n <= 0)
        {
            return null;
        }

        return stripReportZero && buffer[0] == 0x00 ? buffer[1..n] : buffer[..n];
    }

    /// <summary>Descarta lo pendiente, envía y espera una respuesta que cumpla <paramref name="accept"/>.</summary>
    public byte[]? Exchange(byte[] request, Func<byte[], bool> accept, int timeoutMs = 1000, int maxReads = 8, int delayMs = 0)
    {
        channel.FlushInput();
        if (!Send(request))
        {
            return null;
        }

        if (delayMs > 0)
        {
            Thread.Sleep(delayMs);
        }

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < maxReads && clock.ElapsedMilliseconds < timeoutMs; i++)
        {
            byte[]? r = Receive((int)Math.Max(1, timeoutMs - clock.ElapsedMilliseconds));
            if (r is null)
            {
                return null;
            }

            if (accept(r))
            {
                return r;
            }
        }

        return null;
    }

    public bool SetFeature(byte[] report)
    {
        byte[] buffer = new byte[Math.Max(channel.Collection.FeatureReportLength, report.Length)];
        report.CopyTo(buffer, 0);
        return channel.SetFeature(buffer);
    }

    public bool GetInputReport(byte[] buffer) => channel.GetInputReport(buffer);
}

/// <summary>
/// Cascos inalámbricos con receptor USB de varias marcas.
///
/// Los formatos vienen de HeadsetControl (Denis Arnst y colaboradores, GPL-3.0), de los
/// controladores de Linux hid-steelseries-arctis.c y hid-corsair-void.c (como documentación)
/// y de OpenLinkHub (GPL-3.0-or-later) para Corsair. Implementación propia.
///
/// Seguridad: solo consultas de estado (GET). Se han dejado fuera a propósito los cascos que
/// para leer la batería necesitan cambiar el modo del receptor o enviar secuencias de inicio
/// de la app del fabricante (algunas han llegado a cambiar ajustes de forma permanente).
/// Cada respuesta se comprueba por su cabecera, porque las respuestas a la app del fabricante
/// también llegan a nuestra copia.
/// </summary>
internal sealed class Headsets : IHidBatteryProtocol
{
    /// <summary>Un modelo: nombre, VID, PID, cómo encontrar su colección y cómo leerlo.</summary>
    internal sealed record Model(string Name, ushort VendorId, ushort[] ProductIds, Func<HidCollection, bool> Collection, Func<HeadsetIo, ushort, HeadsetResult?> Read);

    private static Func<HidCollection, bool> Usage(ushort page, ushort usage, int? mi = null) =>
        c => c.UsagePage == page && c.Usage == usage && (mi is null || c.InterfaceNumber == mi) && c.OutputReportLength > 0;

    private static Func<HidCollection, bool> Interface(int mi) =>
        c => c.InterfaceNumber == mi && c.IsVendorDefined && c.OutputReportLength > 0;

    private static Func<HidCollection, bool> AnyVendorOutput() => c => c.IsVendorDefined && c.OutputReportLength > 0;

    internal static IReadOnlyList<Model> Models { get; } =
    [
        // ---------------------------------------------------------------- SteelSeries
        new("SteelSeries Arctis 1 Wireless", 0x1038, [0x12B3, 0x12B6, 0x12D7, 0x12D5], Usage(0xFF43, 0x0202, 3), ArctisLegacy1),
        new("SteelSeries Arctis 7", 0x1038, [0x1260, 0x12AD], Usage(0xFF43, 0x0202, 5), ArctisLegacy7),
        new("SteelSeries Arctis 9", 0x1038, [0x12C2, 0x12C4], Usage(0xFFC0, 0x0001, 0), Arctis9),
        new("SteelSeries Arctis 7+", 0x1038, [0x220E, 0x2212, 0x2216, 0x2236], Usage(0xFFC0, 0x0001, 3), Arctis7Plus),
        new("SteelSeries Arctis Nova 7", 0x1038, [0x2202, 0x22A1, 0x227E, 0x2206, 0x2258, 0x229E, 0x22AD, 0x223A, 0x22A9, 0x227A, 0x22A4, 0x22A5, 0x220A, 0x22A7, 0x2298], Usage(0xFFC0, 0x0001, 3), Nova7),
        new("SteelSeries Arctis Nova 5", 0x1038, [0x2232, 0x2253, 0x2255, 0x2264], Usage(0xFFC0, 0x0001, 3), Nova5),
        new("SteelSeries Arctis Nova 3 Wireless", 0x1038, [0x2269, 0x226D], Usage(0xFFC0, 0x0001, 3), Nova5),
        new("SteelSeries Arctis GameBuds", 0x1038, [0x230A], Usage(0xFFC0, 0x0001, 3), GameBuds),
        new("SteelSeries Arctis Nova Pro Wireless", 0x1038, [0x12E0, 0x12E5], Interface(4), NovaProWireless),

        // ---------------------------------------------------------------- Corsair
        new("Corsair Void", 0x1B1C, [0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23, 0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56], Usage(0xFFC5, 0x0001, 3), CorsairVoid),
        new("Corsair Virtuoso", 0x1B1C, [0x0A64, 0x0A62, 0x0A3E, 0x0A3D], Usage(0xFF42, 0x0001, 3), CorsairBragi),

        // ---------------------------------------------------------------- HyperX
        new("HyperX Cloud Alpha Wireless", 0x03F0, [0x098D], AnyVendorOutput(), CloudAlphaWireless),
        new("HyperX Cloud II Wireless", 0x03F0, [0x0696, 0x018B], Usage(0xFF90, 0x0303, 0), CloudIIWireless),
        new("HyperX Cloud Alpha 2 Wireless", 0x03F0, [0x08BE], Usage(0xFF13, 0xFF00, 2), CloudAlpha2Wireless),
        new("HyperX Cloud Flight", 0x0951, [0x16C4, 0x1723], AnyVendorOutput(), CloudFlight),

        // ---------------------------------------------------------------- ASTRO (Logitech)
        new("ASTRO A50", 0x046D, [0x0B1C], Usage(0xFF32, 0x0074, 8), AstroA50Gen5),
        new("ASTRO A50", 0x9886, [0x002C], Usage(0xFF32, 0x0074, 6), AstroA50Gen4),

        // ---------------------------------------------------------------- Audeze, Sony, MCHOSE
        new("Audeze Maxwell", 0x3329, [0x4B19, 0x4B18], Usage(0xFF13, 0x0001, 0), AudezeMaxwell),
        new("Audeze Maxwell 2", 0x3329, [0x4B29, 0x4B28], Usage(0xFF13, 0x0001, 5), AudezeMaxwell),
        new("Sony INZONE H5", 0x054C, [0x0EBF], Usage(0xFF04, 0x0002), SonyInzone),
        new("Sony INZONE H9 II", 0x054C, [0x0FA8], Usage(0xFF04, 0x0001, 5), SonyInzone),
        new("MCHOSE X9", 0x3837, [0x6045], Usage(0xFF90, 0x0001), Mchose),
        new("MCHOSE V9 Pro", 0x291D, [0x385D], Usage(0xFF90, 0x0001), Mchose),
    ];

    private readonly IHidOpener _opener;
    private readonly Dictionary<string, ushort> _sonyTid = new(StringComparer.Ordinal);

    public Headsets(IHidOpener? opener = null) => _opener = opener ?? RealHidOpener.Instance;

    public string Name => "headset";

    internal static Model? Find(HidDeviceGroup device) =>
        Models.FirstOrDefault(m => m.VendorId == device.VendorId && m.ProductIds.Contains(device.ProductId));

    public bool Supports(HidDeviceGroup device) =>
        device.IsUsb && Find(device) is Model m && device.Collections.Any(m.Collection);

    public IReadOnlyList<HidBatteryReading> Read(HidDeviceGroup device, CancellationToken cancellationToken)
    {
        Model model = Find(device)!;
        foreach (HidCollection collection in device.Collections.Where(model.Collection))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using IHidChannel? channel = _opener.Open(collection);
            if (channel is null)
            {
                continue;
            }

            HeadsetResult? result = model.Read(new HeadsetIo(channel), device.ProductId);
            if (result is HeadsetResult r)
            {
                string name = device.Product is string p && p.Length > 3 && !p.Contains("receiver", StringComparison.OrdinalIgnoreCase) ? p : model.Name;
                return [new HidBatteryReading(HidIds.Identity(device), name, DeviceKind.Headphones, r.Level, r.Charging, Connected: r.On)];
            }
        }

        // Sin respuesta: apagado o fuera de alcance (se mantiene el último nivel conocido).
        return [new HidBatteryReading(HidIds.Identity(device), model.Name, DeviceKind.Headphones, null, false, Connected: false)];
    }

    // ================================================================ lectores

    private static int Clamp100(int v) => Math.Clamp(v, 0, 100);

    /// <summary>map() de HeadsetControl: recorta a [a, b] y escala a [c, d] con enteros.</summary>
    internal static int Map(int x, int a, int b, int c, int d)
    {
        x = Math.Clamp(x, a, b);
        return c + ((x - a) * (d - c) / (b - a));
    }

    /// <summary>Arctis 1 / 7X / 7P: 06 12 → [2] = 1 desconectado, [3] = %.</summary>
    internal static HeadsetResult? ArctisLegacy1(HeadsetIo io, ushort pid)
    {
        byte[]? r = io.Exchange([0x06, 0x12], x => x.Length >= 4 && x[0] == 0x06 && x[1] == 0x12);
        return r is null ? null : r[2] == 0x01 ? HeadsetResult.Off : new HeadsetResult(Clamp100(r[3]), false);
    }

    /// <summary>Arctis 7 (2017/2019): 06 18 → [2] = %.</summary>
    internal static HeadsetResult? ArctisLegacy7(HeadsetIo io, ushort pid)
    {
        byte[]? r = io.Exchange([0x06, 0x18], x => x.Length >= 3 && x[0] == 0x06 && x[1] == 0x18);
        return r is null ? null : new HeadsetResult(Clamp100(r[2]), false);
    }

    /// <summary>Arctis 9: 00 20 → [3] de 0x64 a 0x9A, [4] = 1 cargando, 0xFF apagado.</summary>
    internal static HeadsetResult? Arctis9(HeadsetIo io, ushort pid)
    {
        byte[]? r = io.Exchange([0x00, 0x20], x => x.Length >= 5);
        if (r is null)
        {
            return null;
        }

        return r[4] == 0xFF ? HeadsetResult.Off : new HeadsetResult(Map(r[3], 0x64, 0x9A, 0, 100), r[4] == 0x01);
    }

    private static byte[]? NovaQuery(HeadsetIo io) =>
        io.Exchange([0x00, 0xB0], x => x.Length >= 5 && x[0] == 0xB0);

    /// <summary>Arctis 7+: [1] = 1 apagado, [2] de 0 a 4, [3] = 1 cargando.</summary>
    internal static HeadsetResult? Arctis7Plus(HeadsetIo io, ushort pid)
    {
        byte[]? r = NovaQuery(io);
        return r is null ? null : r[1] == 0x01 ? HeadsetResult.Off : new HeadsetResult(Clamp100(r[2] * 25), r[3] == 0x01);
    }

    private static readonly HashSet<ushort> s_nova7Discrete = [0x2202, 0x2206, 0x220A, 0x223A, 0x227A, 0x22A4];

    /// <summary>Arctis Nova 7: [3] = 0 apagado, 1/2 cargando; [2] = % (o de 0 a 4 en los primeros modelos).</summary>
    internal static HeadsetResult? Nova7(HeadsetIo io, ushort pid)
    {
        byte[]? r = NovaQuery(io);
        if (r is null)
        {
            return null;
        }

        if (r[3] == 0x00)
        {
            return HeadsetResult.Off;
        }

        int level = s_nova7Discrete.Contains(pid) ? r[2] * 25 : r[2];
        return new HeadsetResult(Clamp100(level), r[3] is 0x01 or 0x02);
    }

    /// <summary>Arctis Nova 5 / Nova 3 Wireless: [1] = 2 apagado, [3] = %, [4] = 1 cargando.</summary>
    internal static HeadsetResult? Nova5(HeadsetIo io, ushort pid)
    {
        byte[]? r = NovaQuery(io);
        return r is null ? null : r[1] == 0x02 ? HeadsetResult.Off : new HeadsetResult(Clamp100(r[3]), r[4] == 0x01);
    }

    /// <summary>GameBuds: [3]/[4] estado de cada auricular (3 fuera del estuche), [5]/[6] su %. Se da el menor.</summary>
    internal static HeadsetResult? GameBuds(HeadsetIo io, ushort pid)
    {
        byte[]? r = io.Exchange([0x00, 0xB0], x => x.Length >= 7 && x[0] == 0xB0);
        if (r is null)
        {
            return null;
        }

        var active = new List<int>();
        if (r[3] == 0x03)
        {
            active.Add(Clamp100(r[5]));
        }

        if (r[4] == 0x03)
        {
            active.Add(Clamp100(r[6]));
        }

        return active.Count == 0 ? HeadsetResult.Off : new HeadsetResult(active.Min(), false);
    }

    /// <summary>Nova Pro Wireless: 06 B0 → [6] de 0 a 8, [15] = 1 apagado, 2 cargando por cable, 8 conectado.</summary>
    internal static HeadsetResult? NovaProWireless(HeadsetIo io, ushort pid)
    {
        byte[]? r = io.Exchange([0x06, 0xB0], x => x.Length >= 16 && x[0] == 0x06 && x[1] == 0xB0);
        if (r is null)
        {
            return null;
        }

        return r[15] == 0x01 ? HeadsetResult.Off : new HeadsetResult(Map(r[6], 0, 8, 0, 100), r[15] == 0x02);
    }

    /// <summary>
    /// Corsair Void: C9 64 → informe 0x64: [2] bits 0-6 = %, [4] = 0 apagado, 4 llena,
    /// 5 cargando (1, 2 y 3 normal, baja y crítica).
    /// </summary>
    internal static HeadsetResult? CorsairVoid(HeadsetIo io, ushort pid)
    {
        byte[]? r = io.Exchange([0xC9, 0x64], x => x.Length >= 5 && x[0] == 0x64);
        if (r is null)
        {
            return null;
        }

        return r[4] switch
        {
            0 => HeadsetResult.Off,
            4 => new HeadsetResult(100, false),
            _ => new HeadsetResult(Clamp100(r[2] & 0x7F), r[4] == 5),
        };
    }

    /// <summary>
    /// Corsair Virtuoso (Bragi): GET de la propiedad 0x0F (batería en décimas de %) y 0x10
    /// (1 = cargando), con destino 0x09 (cascos a través del receptor) u 0x08 (por cable).
    /// </summary>
    internal static HeadsetResult? CorsairBragi(HeadsetIo io, ushort pid)
    {
        byte target = pid == 0x0A62 ? (byte)0x08 : (byte)0x09;
        uint? battery = BragiGet(io, target, 0x0F) ?? BragiGet(io, target == 0x09 ? (byte)0x08 : (byte)0x09, 0x0F);
        if (battery is not uint value || value > 1000)
        {
            return null;
        }

        bool charging = BragiGet(io, target, 0x10) == 1;
        return new HeadsetResult(Clamp100((int)(value / 10)), charging);
    }

    private static uint? BragiGet(HeadsetIo io, byte target, byte property)
    {
        byte[]? r = io.Exchange([0x02, target, 0x02, property, 0x00], x => x.Length >= 8 && x[0] == 0x01 && x[2] == 0x02, timeoutMs: 300);
        if (r is null || r[3] != 0x00)
        {
            return null; // 0x05: no existe la propiedad (no hay cascos)
        }

        return (uint)(r[4] | (r[5] << 8) | (r[6] << 16) | (r[7] << 24));
    }

    /// <summary>HyperX Cloud Alpha Wireless: 21 BB 03 (conexión), 21 BB 0C (carga), 21 BB 0B (%).</summary>
    internal static HeadsetResult? CloudAlphaWireless(HeadsetIo io, ushort pid)
    {
        byte[]? Ask(byte cmd) => io.Exchange([0x21, 0xBB, cmd], x => x.Length >= 4 && x[0] == 0x21 && x[1] == 0xBB && x[2] == cmd, timeoutMs: 2000);

        byte[]? connection = Ask(0x03);
        if (connection is null)
        {
            return null;
        }

        if (connection[3] == 0x01)
        {
            return HeadsetResult.Off;
        }

        bool charging = Ask(0x0C) is byte[] c && c[3] == 0x01;
        byte[]? level = Ask(0x0B);
        return new HeadsetResult(level is null ? null : Clamp100(level[3]), charging);
    }

    /// <summary>HyperX Cloud II Wireless: 06 FF BB 02 → [7] = %; 06 FF BB 03 → [4] = 1 cargando.</summary>
    internal static HeadsetResult? CloudIIWireless(HeadsetIo io, ushort pid)
    {
        byte[]? level = io.Exchange([0x06, 0xFF, 0xBB, 0x02], x => x.Length >= 8 && x[0] == 0x06 && x[1] == 0xFF && x[2] == 0xBB && x[3] == 0x02, delayMs: 100);
        if (level is null)
        {
            return null;
        }

        byte[]? charge = io.Exchange([0x06, 0xFF, 0xBB, 0x03], x => x.Length >= 5 && x[0] == 0x06 && x[1] == 0xFF && x[2] == 0xBB && x[3] == 0x03, delayMs: 100);
        return new HeadsetResult(Clamp100(level[7]), charge is not null && charge[4] == 0x01);
    }

    /// <summary>HyperX Cloud Alpha 2 Wireless: 52 01 → 53 01 conectado; 50 02 → 51 02 % carga.</summary>
    internal static HeadsetResult? CloudAlpha2Wireless(HeadsetIo io, ushort pid)
    {
        byte[]? link = io.Exchange([0x52, 0x01], x => x.Length >= 3 && x[0] == 0x53 && x[1] == 0x01, maxReads: 16);
        if (link is null)
        {
            return null;
        }

        if (link[2] == 0x00)
        {
            return HeadsetResult.Off;
        }

        byte[]? battery = io.Exchange([0x50, 0x02], x => x.Length >= 4 && x[0] == 0x51 && x[1] == 0x02, maxReads: 16);
        return battery is null ? null : new HeadsetResult(Clamp100(battery[2]), battery[3] == 0x01);
    }

    /// <summary>HyperX Cloud Flight: 21 FF 05 → [3..4] mV; por encima de 4107 mV está cargando.</summary>
    internal static HeadsetResult? CloudFlight(HeadsetIo io, ushort pid)
    {
        byte[]? r = io.Exchange([0x21, 0xFF, 0x05], x => x.Length >= 5, timeoutMs: 2000, maxReads: 1);
        if (r is null)
        {
            return null;
        }

        int mv = (r[3] << 8) | r[4];
        return mv > 4107 ? new HeadsetResult(null, true) : new HeadsetResult(CloudFlightPercent(mv), false);
    }

    internal static int CloudFlightPercent(int mv)
    {
        double v = mv;
        double p = mv switch
        {
            <= 3648 => 0.00125 * v,
            > 3975 => 100,
            _ => (2.547505e-8 * v * v * v * v) - (3.900299e-4 * v * v * v) + (2.238321 * v * v) - (5706.256 * v) + 5452299,
        };
        return (int)Math.Round(Math.Clamp(p, 0, 100));
    }

    /// <summary>ASTRO A50 (5.ª gen.): 02 0C 03 00 06 0C → respuesta 02 0C .. .. 06: [6] = %, [8] ≠ 0 en la base.</summary>
    internal static HeadsetResult? AstroA50Gen5(HeadsetIo io, ushort pid)
    {
        byte[]? r = io.Exchange([0x02, 0x0C, 0x03, 0x00, 0x06, 0x0C], x => x.Length >= 9 && x[0] == 0x02 && x[1] == 0x0C && x[4] == 0x06 && x[2] > 3);
        return r is null ? null : new HeadsetResult(Clamp100(r[6]), r[8] != 0);
    }

    /// <summary>ASTRO A50 (4.ª gen.): 02 54 → [3] bit 1 = cascos enlazados; 02 7C → [3] bits 0-6 %, bit 7 cargando.</summary>
    internal static HeadsetResult? AstroA50Gen4(HeadsetIo io, ushort pid)
    {
        byte[]? link = io.Exchange([0x02, 0x54], x => x.Length >= 4 && x[0] == 0x02);
        if (link is null)
        {
            return null;
        }

        if ((link[3] & 0x02) == 0)
        {
            return HeadsetResult.Off;
        }

        byte[]? r = io.Exchange([0x02, 0x7C], x => x.Length >= 4 && x[0] == 0x02);
        return r is null ? null : new HeadsetResult(Clamp100(r[3] & 0x7F), (r[3] & 0x80) != 0);
    }

    /// <summary>
    /// Audeze Maxwell: se envía la consulta y se leen los fragmentos que devuelve el informe 0x07
    /// ([1] = bytes válidos, desde [3]); el mensaje de resultado es 05 5D 05 00 D6 0C 00 00 %.
    /// </summary>
    internal static HeadsetResult? AudezeMaxwell(HeadsetIo io, ushort pid)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!io.Send(0x06, 0x07, 0x80, 0x05, 0x5A, 0x03, 0x00, 0xD6, 0x0C, 0x00))
            {
                return null;
            }

            var message = new List<byte>();
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1250)
            {
                Thread.Sleep(50);
                byte[] report = new byte[Math.Max(io.Collection.InputReportLength, 62)];
                report[0] = 0x07;
                if (!io.GetInputReport(report))
                {
                    continue;
                }

                int length = report[1];
                if (length == 0 || 3 + length > report.Length)
                {
                    continue;
                }

                message.AddRange(report.AsSpan(3, length).ToArray());
                if (ParseAudeze(message.ToArray()) is int level)
                {
                    return new HeadsetResult(level, false);
                }
            }
        }

        return HeadsetResult.Off;
    }

    /// <summary>Busca en los fragmentos el mensaje 05 5D … D6 0C 00 00 % (0-100).</summary>
    internal static int? ParseAudeze(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> header = [0x05, 0x5D, 0x05, 0x00, 0xD6, 0x0C, 0x00, 0x00];
        int at = data.IndexOf(header);
        if (at < 0 || at + header.Length >= data.Length)
        {
            return null;
        }

        byte level = data[at + header.Length];
        return level <= 100 ? level : null;
    }

    /// <summary>
    /// Sony INZONE H5 / H9 II: petición 02 0C 01 00 FC 08 96 C3 41 04 01 &lt;TID&gt; &lt;suma&gt;;
    /// la respuesta trae el mismo TID y [13] = cargando, [14] = % (0xFF = apagado).
    /// </summary>
    internal static HeadsetResult? SonyInzone(HeadsetIo io, ushort pid)
    {
        ushort tid = 2;
        byte[] request = BuildInzoneRequest(tid);
        byte[]? r = io.Exchange(request, x => x.Length >= 15 && x[0] == 0x02 && x[2] == 0x04 && x[3] == 0xFF && x[6] == 0x96 && x[7] == 0xC3
            && (x[8] >> 4) == 1 && x[9] == 0x04 && x[11] == (byte)tid && x[12] == (byte)(tid >> 8) && x[10] is 0x10 or 0xA0, timeoutMs: 500 * 10, maxReads: 10);
        if (r is null)
        {
            return null;
        }

        byte level = r[14];
        return level == 0xFF ? HeadsetResult.Off : level > 100 ? null : new HeadsetResult(level, r[13] != 0);
    }

    internal static byte[] BuildInzoneRequest(ushort tid)
    {
        byte[] r = [0x02, 0x0C, 0x01, 0x00, 0xFC, 0x08, 0x96, 0xC3, 0x41, 0x04, 0x01, (byte)tid, (byte)(tid >> 8), 0x00];
        int sum = 0;
        for (int i = 6; i <= 12; i++)
        {
            sum += r[i];
        }

        r[13] = (byte)sum;
        return r;
    }

    /// <summary>MCHOSE X9 / V9 Pro: 55 65 01 → 55 65 % 02.</summary>
    internal static HeadsetResult? Mchose(HeadsetIo io, ushort pid)
    {
        byte[]? r = io.Exchange([0x55, 0x65, 0x01], x => x.Length >= 4 && x[0] == 0x55 && x[1] == 0x65 && x[3] == 0x02);
        return r is null || r[2] > 100 ? null : new HeadsetResult(r[2], false);
    }
}
