using BatteryTray.Core;
using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb.Protocols;

/// <summary>
/// Cascos Razer BlackShark V2 Pro (2020) y receptor Nari: informe de característica 0xFF
/// de 64 bytes en la colección del fabricante (0xFF00/0x01). Se envía la consulta, se
/// espera unos 100 ms y se lee la respuesta.
/// Fuente: HaloBattery (providers/blackshark2020.py, MIT) y RazerNariBatteryLevel
/// (solo como documentación). Implementación propia.
///
/// No se incluyen Barracuda ni BlackShark V2 Pro (2023): para leer su batería hay que
/// cambiar el receptor a "modo remoto", y la app nunca cambia el estado de un aparato.
/// </summary>
internal sealed class RazerHeadset : IHidBatteryProtocol
{
    private const byte ReportId = 0xFF;
    private static readonly byte[] s_request = [0xFF, 0x0A, 0x00, 0xFD, 0x04, 0x12, 0xF1, 0x02, 0x05];
    private static readonly byte[] s_replyPrefix = [0xFF, 0x0F, 0x05, 0xFE, 0x12, 0x04, 0x1F, 0x08, 0x05];

    private static readonly Dictionary<ushort, string> s_models = new()
    {
        [0x0528] = "Razer BlackShark V2 Pro",
        [0x052E] = "Razer BlackShark V2 Pro",
        [0x051C] = "Razer Nari",
    };

    private readonly IHidOpener _opener;

    public RazerHeadset(IHidOpener? opener = null) => _opener = opener ?? RealHidOpener.Instance;

    public string Name => "razerheadset";

    public bool Supports(HidDeviceGroup device) =>
        device.VendorId == Razer.VendorId
        && device.IsUsb
        && s_models.ContainsKey(device.ProductId)
        && device.Collections.Any(IsControl);

    private static bool IsControl(HidCollection c) => c.UsagePage == 0xFF00 && c.Usage == 0x01 && c.FeatureReportLength >= 15;

    public IReadOnlyList<HidBatteryReading> Read(HidDeviceGroup device, CancellationToken cancellationToken)
    {
        string name = s_models[device.ProductId];
        foreach (HidCollection collection in device.Collections.Where(IsControl))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using IHidChannel? channel = _opener.OpenForQuery(collection) ?? _opener.Open(collection);
            if (channel is null)
            {
                continue;
            }

            byte[] request = new byte[collection.FeatureReportLength];
            s_request.CopyTo(request, 0);
            if (!channel.SetFeature(request))
            {
                continue;
            }

            Thread.Sleep(100);
            byte[] reply = new byte[collection.FeatureReportLength];
            reply[0] = ReportId;
            if (!channel.GetFeature(reply))
            {
                continue;
            }

            (int? level, bool charging, bool on) = Parse(reply);
            return [new HidBatteryReading(HidIds.Identity(device), name, DeviceKind.Headphones, level, charging, Connected: on)];
        }

        return [];
    }

    /// <summary>
    /// Byte 11 = estado (1 batería, 2 baja, 3 muy baja, 6 llena, 9 cargando); byte 14 = nivel
    /// en pasos de 100/80/50/30/10. Una respuesta que empieza por FF 01 00 FE es de los cascos apagados.
    /// </summary>
    internal static (int? Level, bool Charging, bool On) Parse(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 15 || !reply[..s_replyPrefix.Length].SequenceEqual(s_replyPrefix))
        {
            return (null, false, false);
        }

        byte state = reply[11];
        byte level = reply[14];
        if (level > 100)
        {
            return (null, false, true);
        }

        return (state == 0x06 ? 100 : level, state == 0x09, true);
    }
}
