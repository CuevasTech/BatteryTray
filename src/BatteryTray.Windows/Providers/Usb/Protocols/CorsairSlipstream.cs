using BatteryTray.Core;
using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb.Protocols;

/// <summary>
/// Ratones y teclados Corsair con receptor Slipstream (protocolo "Bragi") o por cable.
///
/// Solo lecturas de propiedades (GET, comando 0x02): 0x36 aparatos conectados al receptor,
/// 0x12 modelo, 0x0F batería (0-1000) y 0x10 estado (1 cargando, 2 descargando, 3 llena).
/// Formato de OpenLinkHub (GPL-3.0-or-later), ckb-next (solo como documentación) y HaloBattery
/// (MIT). Implementación propia.
///
/// Seguridad: nunca se cambia al "modo software" (lo hacen iCUE y otras apps y deja el aparato
/// sin iluminación propia), ni se abren "handles", ni se envía nada que empiece por 08 01.
/// Como iCUE puede estar preguntando a la vez, la batería se lee dos veces y deben coincidir.
/// </summary>
internal sealed class CorsairSlipstream : IHidBatteryProtocol
{
    public const ushort VendorId = 0x1B1C;
    private const byte Self = 0x08;

    private static readonly HashSet<ushort> s_receivers = [0x1B62, 0x1B65, 0x1B66, 0x1B7F, 0x1B81, 0x1B94, 0x1BA6, 0x1BDC, 0x2B00, 0x2B07];

    /// <summary>Modelos (PID del aparato, el mismo que da la propiedad 0x12 a través del receptor).</summary>
    private static readonly Dictionary<ushort, (string Name, DeviceKind Kind)> s_devices = new()
    {
        [0x1B4C] = ("Corsair Ironclaw RGB Wireless", DeviceKind.Mouse),
        [0x1B5E] = ("Corsair Harpoon RGB Wireless", DeviceKind.Mouse),
        [0x1B6E] = ("Corsair K57 RGB Wireless", DeviceKind.Keyboard),
        [0x1B7E] = ("Corsair Dark Core RGB Pro SE", DeviceKind.Mouse),
        [0x1B80] = ("Corsair Dark Core RGB Pro", DeviceKind.Mouse),
        [0x1B98] = ("Corsair Sabre RGB Pro Wireless", DeviceKind.Mouse),
        [0x1BAB] = ("Corsair K100 Air Wireless", DeviceKind.Keyboard),
        [0x1BB2] = ("Corsair Darkstar Wireless", DeviceKind.Mouse),
        [0x1BB5] = ("Corsair M65 RGB Ultra Wireless", DeviceKind.Mouse),
        [0x1BD6] = ("Corsair M65 RGB Ultra Wireless", DeviceKind.Mouse),
        [0x1BB6] = ("Corsair K70 Pro Mini Wireless", DeviceKind.Keyboard),
        [0x1BB8] = ("Corsair Nightsabre Wireless", DeviceKind.Mouse),
        [0x1BDB] = ("Corsair Scimitar RGB Elite Wireless", DeviceKind.Mouse),
        [0x1BF1] = ("Corsair M75 Wireless", DeviceKind.Mouse),
        [0x2B08] = ("Corsair M75 Wireless", DeviceKind.Mouse),
        [0x1BF2] = ("Corsair M75 Air", DeviceKind.Mouse),
        [0x1BFB] = ("Corsair M55 Wireless", DeviceKind.Mouse),
        [0x2B02] = ("Corsair K70 Core TKL Wireless", DeviceKind.Keyboard),
        [0x2B04] = ("Corsair MAKR 75", DeviceKind.Keyboard),
        [0x2B10] = ("Corsair K65 Plus Wireless", DeviceKind.Keyboard),
        [0x2B11] = ("Corsair K65 Plus Wireless", DeviceKind.Keyboard),
        [0x2B23] = ("Corsair K65 Plus Wireless", DeviceKind.Keyboard),
        [0x2B22] = ("Corsair Scimitar Elite Wireless SE", DeviceKind.Mouse),
        [0x2B2E] = ("Corsair Harpoon V2 Wireless", DeviceKind.Mouse),
        [0x2B32] = ("Corsair Ironclaw Wireless SE", DeviceKind.Mouse),
    };

    private readonly IHidOpener _opener;

    public CorsairSlipstream(IHidOpener? opener = null) => _opener = opener ?? RealHidOpener.Instance;

    public string Name => "corsair";

    private static HidCollection? Command(HidDeviceGroup device) =>
        device.Find(c => c.UsagePage == 0xFF42 && c.Usage == 0x0001 && c.OutputReportLength > 0);

    public bool Supports(HidDeviceGroup device) =>
        device.VendorId == VendorId && device.IsUsb
        && (s_receivers.Contains(device.ProductId) || s_devices.ContainsKey(device.ProductId))
        && Command(device) is not null;

    public IReadOnlyList<HidBatteryReading> Read(HidDeviceGroup device, CancellationToken cancellationToken)
    {
        using IHidChannel? channel = _opener.Open(Command(device)!);
        if (channel is null)
        {
            return [];
        }

        var io = new HeadsetIo(channel);
        var readings = new List<HidBatteryReading>();
        if (!s_receivers.Contains(device.ProductId))
        {
            // Aparato por cable: se le pregunta a él mismo.
            (string name, DeviceKind kind) = s_devices[device.ProductId];
            readings.Add(Reading(io, Self, HidIds.Identity(device), name, kind));
            return readings;
        }

        // Receptor: qué aparatos tiene conectados y la batería de cada uno.
        uint? connected = Get(io, Self, 0x36);
        if (connected is not uint bits)
        {
            return readings;
        }

        for (int n = 1; n < 8; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((bits & (1u << n)) == 0)
            {
                continue;
            }

            byte address = (byte)(Self | n);
            ushort pid = (ushort)(Get(io, address, 0x12) ?? 0);
            (string name, DeviceKind kind) = s_devices.TryGetValue(pid, out var known) ? known : ("Corsair", DeviceKind.Unknown);
            readings.Add(Reading(io, address, HidIds.Identity(device, $"{n}:{pid:X4}"), name, kind));
        }

        return readings;
    }

    private static HidBatteryReading Reading(HeadsetIo io, byte address, string identity, string name, DeviceKind kind)
    {
        uint? first = Get(io, address, 0x0F);
        uint? second = first is null ? null : Get(io, address, 0x0F);
        if (first is not uint level || second != first || level is 0 or > 1000)
        {
            return new HidBatteryReading(identity, name, kind, null, false); // dormido o respuesta dudosa
        }

        uint? status = Get(io, address, 0x10);
        return status == 3
            ? new HidBatteryReading(identity, name, kind, 100, false)
            : new HidBatteryReading(identity, name, kind, (int)Math.Round(level / 10.0), status == 1);
    }

    /// <summary>GET de una propiedad: 00 &lt;dirección&gt; 02 &lt;propiedad&gt; → &lt;origen&gt; 02 &lt;estado&gt; &lt;valor de 24 bits&gt;.</summary>
    internal static uint? Get(HeadsetIo io, byte address, byte property)
    {
        byte[]? r = io.Exchange([0x00, address, 0x02, property], x => x.Length >= 6 && (x[0] & 0x07) == (address & 0x07) && x[1] == 0x02, timeoutMs: 2500);
        if (r is null || r[2] != 0x00)
        {
            return null;
        }

        return (uint)(r[3] | (r[4] << 8) | (r[5] << 16));
    }
}
