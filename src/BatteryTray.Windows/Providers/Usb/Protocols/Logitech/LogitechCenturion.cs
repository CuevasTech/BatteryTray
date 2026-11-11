using System.Diagnostics;
using BatteryTray.Core;
using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb.Protocols.Logitech;

/// <summary>
/// Cascos Logitech con el protocolo "Centurion" (G PRO X 2 LIGHTSPEED, G522 y G PRO X 3
/// LIGHTSPEED): tramas de 64 bytes en la colección 0xFFA0/0x0001. Al receptor se le pregunta
/// directamente; a los cascos, a través de su "puente" (característica 0x0003 del receptor).
///
/// Pasos (todos de lectura): 1) buscar el índice del puente; 2) buscar en los cascos la
/// característica de batería 0x0104; 3) leerla: [0] %, [2] = 1 o 2 cargando, 3 llena.
/// Fuente: HeadsetControl (logitech_centurion_protocol.hpp, GPL-3.0) y Solaar (centurion.py,
/// GPL-2.0-or-later). Implementación propia.
/// </summary>
internal sealed class LogitechCenturion : IHidBatteryProtocol
{
    private const int FrameLength = 64;
    private const byte SoftwareFn = 0x01; // función 0 con id de software 1, como el resto de herramientas

    private static readonly Dictionary<ushort, (string Name, byte[] Prefix)> s_models = new()
    {
        [0x0AF7] = ("Logitech G PRO X 2 LIGHTSPEED", [0x51]),
        [0x0B18] = ("Logitech G522 LIGHTSPEED", [0x50, 0x23]),
        [0x0B45] = ("Logitech G PRO X 3 LIGHTSPEED", [0x50, 0x23]),
    };

    private readonly IHidOpener _opener;

    public LogitechCenturion(IHidOpener? opener = null) => _opener = opener ?? RealHidOpener.Instance;

    public string Name => "logicenturion";

    private static HidCollection? Control(HidDeviceGroup device) =>
        device.Find(c => c.UsagePage == 0xFFA0 && c.Usage == 0x0001 && c.OutputReportLength >= FrameLength);

    public bool Supports(HidDeviceGroup device) =>
        device.VendorId == LogitechHidpp.VendorId && device.IsUsb && s_models.ContainsKey(device.ProductId) && Control(device) is not null;

    public IReadOnlyList<HidBatteryReading> Read(HidDeviceGroup device, CancellationToken cancellationToken)
    {
        (string name, byte[] prefix) = s_models[device.ProductId];
        string identity = HidIds.Identity(device);
        using IHidChannel? channel = _opener.Open(Control(device)!);
        if (channel is null)
        {
            return [];
        }

        HeadsetResult? r = ReadBattery(channel, prefix);
        return [new HidBatteryReading(identity, name, DeviceKind.Headphones, r?.Level, r?.Charging ?? false, Connected: r?.On ?? false)];
    }

    internal static HeadsetResult? ReadBattery(IHidChannel channel, byte[] prefix)
    {
        channel.FlushInput();

        // 1) Índice del puente: Root.getFeature(0x0003) al receptor.
        byte[]? bridgeReply = Exchange(channel, prefix, [0x00, SoftwareFn, 0x00, 0x03], p => p.Length >= 3 && p[0] == 0x00 && p[1] == SoftwareFn);
        if (bridgeReply is null || bridgeReply[2] == 0)
        {
            return null;
        }

        byte bridge = bridgeReply[2];

        // 2) Índice de la batería en los cascos: Root.getFeature(0x0104) a través del puente.
        byte[]? featureReply = Exchange(channel, prefix, [bridge, 0x11, 0x00, 0x05, 0x00, 0x00, SoftwareFn, 0x01, 0x04],
            p => p.Length >= 8 && p[0] == bridge && p[1] == 0x10 && p[5] == 0x00 && p[6] == SoftwareFn);
        if (featureReply is null || featureReply[7] == 0)
        {
            return HeadsetResult.Off; // el receptor responde pero los cascos no: apagados
        }

        byte battery = featureReply[7];

        // 3) Leer la batería.
        byte[]? status = Exchange(channel, prefix, [bridge, 0x11, 0x00, 0x03, 0x00, battery, SoftwareFn],
            p => p.Length >= 10 && p[0] == bridge && p[1] == 0x10 && p[5] == battery && p[6] == SoftwareFn);
        if (status is null)
        {
            return HeadsetResult.Off;
        }

        byte percent = status[7];
        byte state = status[9];
        if (percent > 100)
        {
            return null;
        }

        return state == 3 ? new HeadsetResult(100, false) : new HeadsetResult(percent, state is 1 or 2);
    }

    /// <summary>Construye la trama (prefijo, longitud, indicadores 0, carga útil), la envía y devuelve la carga útil de la respuesta aceptada.</summary>
    internal static byte[]? Exchange(IHidChannel channel, byte[] prefix, byte[] payload, Func<byte[], bool> accept)
    {
        if (!channel.Write(BuildFrame(prefix, payload), 1000))
        {
            return null;
        }

        var clock = Stopwatch.StartNew();
        byte[] buffer = new byte[Math.Max(channel.Collection.InputReportLength, FrameLength)];
        for (int i = 0; i < 8 && clock.ElapsedMilliseconds < 1500; i++)
        {
            int n = channel.Read(buffer, (int)Math.Max(1, 1500 - clock.ElapsedMilliseconds));
            if (n <= 0)
            {
                return null;
            }

            int start = prefix.Length + 2;
            if (n <= start || !buffer.AsSpan(0, prefix.Length).SequenceEqual(prefix))
            {
                continue;
            }

            byte[] reply = buffer[start..n];
            if (accept(reply))
            {
                return reply;
            }
        }

        return null;
    }

    internal static byte[] BuildFrame(byte[] prefix, byte[] payload)
    {
        byte[] frame = new byte[FrameLength];
        prefix.CopyTo(frame, 0);
        frame[prefix.Length] = (byte)(payload.Length + 1);
        frame[prefix.Length + 1] = 0x00;
        payload.CopyTo(frame, prefix.Length + 2);
        return frame;
    }
}
