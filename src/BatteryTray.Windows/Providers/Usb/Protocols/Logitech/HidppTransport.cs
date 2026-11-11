using System.Diagnostics;

namespace BatteryTray.Windows.Providers.Usb.Protocols.Logitech;

/// <summary>Resultado de una petición HID++.</summary>
internal readonly record struct HidppReply(HidppStatus Status, byte[] Params, byte ErrorCode = 0)
{
    public bool Ok => Status == HidppStatus.Ok;
}

internal enum HidppStatus
{
    Ok,
    Error,
    Timeout,
    TransportError,
}

/// <summary>
/// Canal HID++ de un receptor o aparato Logitech: la colección corta (informe 0x10, 7 bytes)
/// y la larga (0x11, 20 bytes). Windows separa cada una en su propia ruta, y las respuestas
/// pueden llegar por la otra (p. ej. un error a una petición larga llega por la corta), así
/// que se escucha en las dos.
///
/// Por el canal largo también llegan las respuestas a G HUB u Options+: solo se acepta la
/// que coincide exactamente con la petición (índice de aparato, función y nuestro id de software).
/// Las peticiones van de una en una.
/// </summary>
internal sealed class HidppTransport(IHidChannel? shortChannel, IHidChannel? longChannel) : IDisposable
{
    /// <summary>Nuestro id de software (bits bajos del byte 3). Ninguna herramienta conocida usa el 0x0C.</summary>
    public const byte SoftwareId = 0x0C;

    public const byte ShortReport = 0x10;
    public const byte LongReport = 0x11;
    private const int ShortLength = 7;
    private const int LongLength = 20;
    private const int PollSliceMs = 15;

    public bool HasShort => shortChannel is not null;

    public bool HasLong => longChannel is not null;

    /// <summary>Descarta lo que hubiera pendiente de antes.</summary>
    public void Flush()
    {
        shortChannel?.FlushInput();
        longChannel?.FlushInput();
    }

    /// <summary>
    /// Envía un mensaje y espera la respuesta que reconozca <paramref name="match"/>
    /// (devuelve null para los informes que no son la respuesta).
    /// </summary>
    public HidppReply Request(byte[] frame, Func<byte[], HidppReply?> match, int timeoutMs)
    {
        IHidChannel? channel = frame[0] == ShortReport ? shortChannel : longChannel;
        if (channel is null)
        {
            return new HidppReply(HidppStatus.TransportError, []);
        }

        byte[] padded = new byte[frame[0] == ShortReport ? ShortLength : LongLength];
        frame.AsSpan(0, Math.Min(frame.Length, padded.Length)).CopyTo(padded);
        if (!channel.Write(padded, timeoutMs))
        {
            return new HidppReply(HidppStatus.TransportError, []);
        }

        var clock = Stopwatch.StartNew();
        byte[] buffer = new byte[64];
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            foreach (IHidChannel? c in (ReadOnlySpan<IHidChannel?>)[longChannel, shortChannel])
            {
                if (c is null)
                {
                    continue;
                }

                int n = c.Read(buffer, PollSliceMs);
                if (n < 0)
                {
                    return new HidppReply(HidppStatus.TransportError, []);
                }

                if (n >= ShortLength && match(buffer[..n]) is HidppReply reply)
                {
                    return reply;
                }
            }
        }

        return new HidppReply(HidppStatus.Timeout, []);
    }

    // ------------------------------------------------------------ peticiones concretas

    /// <summary>HID++ 2.0: función <paramref name="function"/> de la característica con índice <paramref name="featureIndex"/>.</summary>
    public HidppReply Call(byte device, byte featureIndex, byte function, ReadOnlySpan<byte> parameters, int timeoutMs = 2000)
    {
        byte fnSw = (byte)((function << 4) | SoftwareId);
        byte[] frame = new byte[LongLength];
        frame[0] = LongReport;
        frame[1] = device;
        frame[2] = featureIndex;
        frame[3] = fnSw;
        parameters[..Math.Min(parameters.Length, 16)].CopyTo(frame.AsSpan(4));
        if (!HasLong)
        {
            return new HidppReply(HidppStatus.TransportError, []);
        }

        return Request(frame, r => MatchCall(r, device, featureIndex, fnSw), timeoutMs);
    }

    internal static HidppReply? MatchCall(byte[] r, byte device, byte featureIndex, byte fnSw)
    {
        if (r[1] != device)
        {
            return null;
        }

        // Respuesta correcta.
        if (r[0] is LongReport or 0x12 && r[2] == featureIndex && r[3] == fnSw)
        {
            return new HidppReply(HidppStatus.Ok, r[4..]);
        }

        // Error HID++ 2.0 (FF) o error HID++ 1.0 (8F), que también usa el receptor para "no se puede llegar al aparato".
        if (r.Length >= 6 && ((r[0] >= LongReport && r[2] == 0xFF) || (r[0] == ShortReport && r[2] == 0x8F)) && r[3] == featureIndex && r[4] == fnSw)
        {
            return new HidppReply(HidppStatus.Error, [], r[5]);
        }

        return null;
    }

    /// <summary>Ping (raíz, función 1): devuelve la versión del protocolo o el error.</summary>
    public HidppReply Ping(byte device, byte mark, int timeoutMs = 2000)
    {
        const byte fnSw = 0x10 | SoftwareId;
        byte[] frame = HasShort
            ? [ShortReport, device, 0x00, fnSw, 0x00, 0x00, mark]
            : [LongReport, device, 0x00, fnSw, 0x00, 0x00, mark];
        return Request(frame, r => MatchPing(r, device, mark), timeoutMs);
    }

    internal static HidppReply? MatchPing(byte[] r, byte device, byte mark)
    {
        const byte fnSw = 0x10 | SoftwareId;
        if (r[1] != device)
        {
            return null;
        }

        if (r[0] is ShortReport or LongReport && r[2] == 0x00 && r[3] == fnSw && r[6] == mark)
        {
            return new HidppReply(HidppStatus.Ok, r[4..]);
        }

        if (r.Length >= 6 && ((r[0] == ShortReport && r[2] == 0x8F) || (r[0] >= LongReport && r[2] == 0xFF)) && r[3] == 0x00 && r[4] == fnSw)
        {
            return new HidppReply(HidppStatus.Error, [], r[5]);
        }

        return null;
    }

    /// <summary>Lee un registro corto del receptor (HID++ 1.0, sub-id 0x81).</summary>
    public HidppReply ReadRegister(byte device, byte register, int timeoutMs = 1000) =>
        RegisterRequest(device, 0x81, register, 0, 0, timeoutMs);

    /// <summary>Lee un registro largo del receptor (sub-id 0x83); la respuesta llega por el canal largo.</summary>
    public HidppReply ReadLongRegister(byte device, byte register, byte subRegister, byte param = 0, int timeoutMs = 2000) =>
        RegisterRequest(device, 0x83, register, subRegister, param, timeoutMs);

    private HidppReply RegisterRequest(byte device, byte subId, byte register, byte p0, byte p1, int timeoutMs)
    {
        if (!HasShort)
        {
            return new HidppReply(HidppStatus.TransportError, []);
        }

        byte[] frame = [ShortReport, device, subId, register, p0, p1, 0x00];
        bool checkSub = subId == 0x83 && register == 0xB5;
        return Request(frame, r => MatchRegister(r, device, subId, register, checkSub ? p0 : null), timeoutMs);
    }

    internal static HidppReply? MatchRegister(byte[] r, byte device, byte subId, byte register, byte? subRegister)
    {
        if (r[1] != device)
        {
            return null;
        }

        if (r[2] == subId && r[3] == register && (subRegister is null || r[4] == subRegister))
        {
            return new HidppReply(HidppStatus.Ok, r[4..]);
        }

        if (r[0] == ShortReport && r[2] == 0x8F && r[3] == subId && r[4] == register)
        {
            return new HidppReply(HidppStatus.Error, [], r[5]);
        }

        return null;
    }

    public void Dispose()
    {
        shortChannel?.Dispose();
        longChannel?.Dispose();
    }
}
