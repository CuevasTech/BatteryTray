using System.Text;
using BatteryTray.Core;
using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb.Protocols.Logitech;

/// <summary>
/// Logitech por HID++: aparatos en receptores Lightspeed, Bolt, Unifying y Nano, y aparatos
/// conectados directamente (ratones por cable, receptores de cascos G).
///
/// Fuentes del protocolo: Solaar (GPL-2.0-or-later), libratbag (MIT), cvuchener/hidpp
/// (GPL-3.0-or-later), LGSTrayBattery (GPL-3.0) y el controlador de Linux (solo como
/// documentación). Implementación propia.
///
/// Seguridad (solo lectura). Únicamente se envía:
/// - al receptor: lectura de registros (0x81 sobre 0x02; 0x83 sobre 0xB5 subregistros 03, 2x, 4x, 5x, 6x);
/// - a cada aparato: ping, búsqueda de características (raíz), nombre y tipo (0x0005),
///   identificación (0x0003 función 0), y las lecturas de batería (0x1000 f0, 0x1001 f0,
///   0x1004 f0/f1, 0x1F20 f0) o los registros 0x0D/0x07 de los aparatos antiguos.
/// Nunca se escriben registros (ni siquiera los que activan avisos, que sí envían otras apps),
/// ni comandos DJ, ni funciones que cambien ajustes. Nada se envía a webcams ni a luces Litra.
/// </summary>
internal sealed class LogitechHidpp : IHidBatteryProtocol
{
    public const ushort VendorId = 0x046D;
    private const byte ReceiverIndex = 0xFF;
    private const ushort BoltReceiver = 0xC548;
    private const ushort PowerPlayReceiver = 0xC53A;
    private static readonly TimeSpan s_cacheTime = TimeSpan.FromMinutes(5);

    private readonly IHidOpener _opener;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<HidBatteryReading> Readings)> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<ushort, byte>> _features = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _identityByUnit = [];
    private byte _mark;

    public LogitechHidpp(IHidOpener? opener = null, TimeProvider? time = null)
    {
        _opener = opener ?? RealHidOpener.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string Name => "logitech";

    /// <summary>Receptores: todos los PID 0xC5xx (Unifying, Nano, Lightspeed, Bolt) salvo los sin HID++.</summary>
    internal static bool IsReceiver(ushort productId) => productId is >= 0xC500 and <= 0xC5FF && productId != 0xC542;

    public bool Supports(HidDeviceGroup device) =>
        device.VendorId == VendorId
        && device.IsUsb
        && device.ProductId is not (>= 0x0800 and <= 0x09FF) // webcams: las preguntas HID++ las pueden bloquear
        && device.ProductId is not (>= 0xC900 and <= 0xC90F) // luces Litra: no son HID++
        && FindLong(device) is not null;

    /// <summary>Colección corta (informe 0x10 de 7 bytes).</summary>
    private static HidCollection? FindShort(HidDeviceGroup device, HidCollection? longCollection)
    {
        // Los cascos con colección larga 0xFF43 (o la de controles del G535) solo hablan por la larga.
        if (longCollection is { UsagePage: 0xFF43 or 0x000C })
        {
            return null;
        }

        return device.Collections
            .Where(c => c.UsagePage == 0xFF00 && c.Usage == 0x0001 && c.InputReportLength == 7)
            .OrderByDescending(c => c.InterfaceNumber == longCollection?.InterfaceNumber)
            .FirstOrDefault();
    }

    /// <summary>Colección larga (informe 0x11 de 20 bytes).</summary>
    private static HidCollection? FindLong(HidDeviceGroup device) =>
        device.Find(c => c.UsagePage == 0xFF00 && c.Usage == 0x0002 && c.InputReportLength == 20)
        ?? device.Find(c => c.UsagePage == 0xFF43 && c.Usage is 0x0202 or 0x0602 && c.InputReportLength == 20)
        // Los G535 llevan HID++ en su colección de controles multimedia (interfaz 3).
        ?? (device.ProductId == 0x0AC4 ? device.Find(c => c.UsagePage == 0x000C && c.Usage == 0x0001 && c.InterfaceNumber == 3 && c.OutputReportLength >= 20) : null);

    public IReadOnlyList<HidBatteryReading> Read(HidDeviceGroup device, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            // No hace falta preguntar más a menudo: la batería cambia despacio y así no se
            // molesta a los aparatos ni a su ahorro de energía.
            if (_cache.TryGetValue(device.Key, out var cached) && now - cached.At < s_cacheTime)
            {
                return cached.Readings;
            }
        }

        HidCollection? longCollection = FindLong(device);
        HidCollection? shortCollection = FindShort(device, longCollection);
        IHidChannel? longChannel = longCollection is null ? null : _opener.Open(longCollection);
        IHidChannel? shortChannel = shortCollection is null ? null : _opener.Open(shortCollection);
        using var transport = new HidppTransport(shortChannel, longChannel);
        if (!transport.HasLong)
        {
            return [];
        }

        transport.Flush();
        IReadOnlyList<HidBatteryReading> readings = IsReceiver(device.ProductId)
            ? ReadReceiver(transport, device, cancellationToken)
            : ReadDirect(transport, device, cancellationToken);

        lock (_gate)
        {
            // Si algún aparato no respondió, se vuelve a probar en el siguiente ciclo.
            bool complete = readings.Count > 0 && readings.All(r => r.Level is not null || !r.Connected);
            if (complete)
            {
                _cache[device.Key] = (now, readings);
            }
            else
            {
                _cache.Remove(device.Key);
            }
        }

        return readings;
    }

    // ------------------------------------------------------------ receptor

    private List<HidBatteryReading> ReadReceiver(HidppTransport t, HidDeviceGroup receiver, CancellationToken ct)
    {
        bool bolt = receiver.ProductId == BoltReceiver;
        string receiverIdentity = HidIds.Identity(receiver);
        int slots = 6;
        if (!bolt && t.ReadLongRegister(ReceiverIndex, 0xB5, 0x03) is { Ok: true } info && info.Params.Length > 6 && info.Params[6] is >= 1 and <= 6)
        {
            slots = info.Params[6];
        }

        if (receiver.ProductId == PowerPlayReceiver)
        {
            slots = 7; // la alfombrilla PowerPlay es el aparato 7
        }

        var readings = new List<HidBatteryReading>();
        bool anyPairingInfo = false;
        for (byte slot = 1; slot <= slots; slot++)
        {
            ct.ThrowIfCancellationRequested();
            Pairing? pairing = ReadPairing(t, bolt, slot);
            if (pairing is null)
            {
                continue;
            }

            anyPairingInfo = true;
            if (pairing.Value.Empty)
            {
                continue;
            }

            string identity = $"{receiverIdentity}:{pairing.Value.Wpid:X4}";
            if (pairing.Value.Offline)
            {
                readings.Add(new HidBatteryReading(identity, pairing.Value.Name ?? "Logitech", pairing.Value.Kind, null, false, Connected: false));
                continue;
            }

            if (ReadDevice(t, slot, $"{receiver.Key}:{slot}", pairing.Value.Wpid) is DeviceInfo d)
            {
                if (d.UnitId is uint unit)
                {
                    lock (_gate)
                    {
                        _identityByUnit[unit] = identity;
                    }
                }

                readings.Add(new HidBatteryReading(identity, d.Name ?? pairing.Value.Name ?? "Logitech", d.Kind ?? pairing.Value.Kind, d.State.Level, d.State.Charging, Connected: d.Online && d.State.On));
            }
        }

        // Algunos receptores no dan la lista de emparejados: se pregunta a cada posición.
        if (!anyPairingInfo)
        {
            for (byte slot = 1; slot <= 6; slot++)
            {
                ct.ThrowIfCancellationRequested();
                if (ReadDevice(t, slot, $"{receiver.Key}:{slot}", 0) is DeviceInfo d && d.Online)
                {
                    string identity = $"{receiverIdentity}:slot{slot}";
                    readings.Add(new HidBatteryReading(identity, d.Name ?? "Logitech", d.Kind ?? DeviceKind.Unknown, d.State.Level, d.State.Charging, Connected: d.State.On));
                }
            }
        }

        return readings;
    }

    internal readonly record struct Pairing(bool Empty, bool Offline, ushort Wpid, DeviceKind Kind, string? Name);

    /// <summary>Lee la información de emparejado de una posición (null si el receptor no la da).</summary>
    private static Pairing? ReadPairing(HidppTransport t, bool bolt, byte slot)
    {
        HidppReply r = bolt
            ? t.ReadLongRegister(ReceiverIndex, 0xB5, (byte)(0x50 + slot))
            : t.ReadLongRegister(ReceiverIndex, 0xB5, (byte)(0x20 + slot - 1));
        if (r.Status == HidppStatus.Error)
        {
            return new Pairing(Empty: true, false, 0, DeviceKind.Unknown, null); // posición vacía
        }

        if (!r.Ok || r.Params.Length < 8)
        {
            return null;
        }

        return bolt ? ParseBoltPairing(r.Params, ReadBoltName(t, slot)) : ParseUnifyingPairing(r.Params, ReadCodename(t, slot));
    }

    /// <summary>Unifying/Nano/Lightspeed: [3..4] wpid (big-endian), [7] tipo en el nibble bajo.</summary>
    internal static Pairing ParseUnifyingPairing(ReadOnlySpan<byte> p, string? name)
    {
        ushort wpid = (ushort)((p[3] << 8) | p[4]);
        return new Pairing(wpid == 0, false, wpid, KindFromPairing(p[7] & 0x0F), name);
    }

    /// <summary>Bolt: [1] información (nibble bajo = tipo, bit 6 = desconectado), [2..3] wpid (little-endian).</summary>
    internal static Pairing ParseBoltPairing(ReadOnlySpan<byte> p, string? name)
    {
        ushort wpid = (ushort)(p[2] | (p[3] << 8));
        return new Pairing(wpid == 0, (p[1] & 0x40) != 0, wpid, KindFromPairing(p[1] & 0x0F), name);
    }

    private static string? ReadCodename(HidppTransport t, byte slot)
    {
        HidppReply r = t.ReadLongRegister(ReceiverIndex, 0xB5, (byte)(0x40 + slot - 1));
        return r.Ok && r.Params.Length > 2 ? Ascii(r.Params.AsSpan(2), r.Params[1]) : null;
    }

    private static string? ReadBoltName(HidppTransport t, byte slot)
    {
        HidppReply r = t.ReadLongRegister(ReceiverIndex, 0xB5, (byte)(0x60 + slot), param: 0x01);
        return r.Ok && r.Params.Length > 3 ? Ascii(r.Params.AsSpan(3), r.Params[2]) : null;
    }

    // ------------------------------------------------------------ aparato directo

    private List<HidBatteryReading> ReadDirect(HidppTransport t, HidDeviceGroup device, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ReadDevice(t, ReceiverIndex, device.Key, device.ProductId) is not DeviceInfo d || !d.Online)
        {
            return [];
        }

        string identity = HidIds.Identity(device);
        if (d.UnitId is uint unit)
        {
            lock (_gate)
            {
                // El mismo ratón visto antes por su receptor: mismo id, así no sale dos veces.
                if (_identityByUnit.TryGetValue(unit, out string? known))
                {
                    identity = known;
                }
            }
        }

        string name = d.Name ?? device.Product ?? "Logitech";
        return [new HidBatteryReading(identity, name, d.Kind ?? HidKinds.Guess(device), d.State.Level, d.State.Charging, Connected: d.State.On)];
    }

    // ------------------------------------------------------------ un aparato HID++

    private readonly record struct DeviceInfo(bool Online, HidppBatteryState State, string? Name, DeviceKind? Kind, uint? UnitId);

    /// <summary>Pregunta a un aparato (por su índice en el receptor, o 0xFF si va directo). Null si no hay aparato.</summary>
    private DeviceInfo? ReadDevice(HidppTransport t, byte index, string cacheKey, ushort productId)
    {
        HidppReply ping = t.Ping(index, NextMark());
        if (ping.Status == HidppStatus.Error)
        {
            return ping.ErrorCode switch
            {
                0x01 => ReadHidpp10(t, index), // aparato antiguo HID++ 1.0
                0x08 => null, // no hay aparato en esa posición
                _ => new DeviceInfo(false, default, null, null, null), // dormido, apagado o fuera de alcance
            };
        }

        if (!ping.Ok || ping.Params.Length < 2)
        {
            return new DeviceInfo(false, default, null, null, null);
        }

        if (ping.Params[0] < 2)
        {
            return ReadHidpp10(t, index);
        }

        (string? name, DeviceKind? kind) = ReadNameAndKind(t, index, cacheKey);
        uint? unit = ReadUnitId(t, index, cacheKey);
        HidppBatteryState state = ReadBattery(t, index, cacheKey, productId);
        return new DeviceInfo(true, state, name, kind, unit);
    }

    private DeviceInfo ReadHidpp10(HidppTransport t, byte index)
    {
        HidppReply r = t.ReadRegister(index, 0x0D);
        if (r.Ok)
        {
            return new DeviceInfo(true, HidppBattery.FromRegister0D(r.Params), null, null, null);
        }

        r = t.ReadRegister(index, 0x07);
        return new DeviceInfo(true, r.Ok ? HidppBattery.FromRegister07(r.Params) : default, null, null, null);
    }

    private HidppBatteryState ReadBattery(HidppTransport t, byte index, string cacheKey, ushort productId)
    {
        if (Feature(t, index, cacheKey, HidppBattery.UnifiedBattery) is byte unified)
        {
            HidppReply caps = t.Call(index, unified, 0, []);
            HidppReply status = t.Call(index, unified, 1, []);
            if (status.Ok)
            {
                bool hasPercentage = caps.Ok && caps.Params.Length > 1 && (caps.Params[1] & 0x02) != 0;
                return HidppBattery.FromUnifiedBattery(status.Params, hasPercentage);
            }

            Forget(cacheKey, status);
        }

        if (Feature(t, index, cacheKey, HidppBattery.BatteryStatus) is byte status1000)
        {
            HidppReply r = t.Call(index, status1000, 0, []);
            if (r.Ok)
            {
                return HidppBattery.FromBatteryStatus(r.Params);
            }

            Forget(cacheKey, r);
        }

        if (Feature(t, index, cacheKey, HidppBattery.BatteryVoltage) is byte voltage)
        {
            HidppReply r = t.Call(index, voltage, 0, []);
            if (r.Ok)
            {
                return HidppBattery.FromBatteryVoltage(r.Params);
            }

            Forget(cacheKey, r);
        }

        if (Feature(t, index, cacheKey, HidppBattery.AdcMeasurement) is byte adc)
        {
            HidppReply r = t.Call(index, adc, 0, []);
            if (r.Ok)
            {
                return HidppBattery.FromAdcMeasurement(r.Params, productId);
            }

            // Error 0x05 con el receptor puesto: los cascos están apagados.
            if (r.Status == HidppStatus.Error && r.ErrorCode == 0x05)
            {
                return new HidppBatteryState(null, false, On: false);
            }

            Forget(cacheKey, r);
        }

        return new HidppBatteryState(null, false);
    }

    private (string? Name, DeviceKind? Kind) ReadNameAndKind(HidppTransport t, byte index, string cacheKey)
    {
        if (Feature(t, index, cacheKey, 0x0005) is not byte fi)
        {
            return (null, null);
        }

        DeviceKind? kind = null;
        HidppReply type = t.Call(index, fi, 2, []);
        if (type.Ok && type.Params.Length > 0)
        {
            kind = KindFromDeviceType(type.Params[0]);
        }

        HidppReply length = t.Call(index, fi, 0, []);
        if (!length.Ok || length.Params.Length == 0 || length.Params[0] is 0 or > 64)
        {
            return (null, kind);
        }

        int total = length.Params[0];
        var bytes = new List<byte>(total);
        while (bytes.Count < total)
        {
            HidppReply chunk = t.Call(index, fi, 1, [(byte)bytes.Count]);
            if (!chunk.Ok || chunk.Params.Length == 0)
            {
                break;
            }

            int take = Math.Min(chunk.Params.Length, total - bytes.Count);
            bytes.AddRange(chunk.Params.AsSpan(0, take).ToArray());
        }

        string name = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0').Trim();
        if (name.Length == 0)
        {
            return (null, kind);
        }

        return (name.Contains("Logi", StringComparison.OrdinalIgnoreCase) ? name : "Logitech " + name, kind);
    }

    private uint? ReadUnitId(HidppTransport t, byte index, string cacheKey)
    {
        if (Feature(t, index, cacheKey, 0x0003) is not byte fi)
        {
            return null;
        }

        HidppReply r = t.Call(index, fi, 0, []);
        if (!r.Ok || r.Params.Length < 5)
        {
            return null;
        }

        uint unit = (uint)((r.Params[1] << 24) | (r.Params[2] << 16) | (r.Params[3] << 8) | r.Params[4]);
        return unit == 0 ? null : unit;
    }

    /// <summary>Índice de una característica (raíz, función 0), guardado por aparato. Null si no la tiene.</summary>
    private byte? Feature(HidppTransport t, byte index, string cacheKey, ushort feature)
    {
        lock (_gate)
        {
            if (_features.TryGetValue(cacheKey, out var known) && known.TryGetValue(feature, out byte cached))
            {
                return cached == 0 ? null : cached;
            }
        }

        HidppReply r = t.Call(index, 0x00, 0, [(byte)(feature >> 8), (byte)feature]);
        if (!r.Ok || r.Params.Length == 0)
        {
            return null; // sin respuesta: no se guarda, se volverá a preguntar
        }

        lock (_gate)
        {
            if (!_features.TryGetValue(cacheKey, out var map))
            {
                _features[cacheKey] = map = [];
            }

            map[feature] = r.Params[0];
        }

        return r.Params[0] == 0 ? null : r.Params[0];
    }

    /// <summary>Con el error "índice de característica no válido" se olvidan los índices guardados.</summary>
    private void Forget(string cacheKey, HidppReply reply)
    {
        if (reply.Status == HidppStatus.Error && reply.ErrorCode == 0x06)
        {
            lock (_gate)
            {
                _features.Remove(cacheKey);
            }
        }
    }

    private byte NextMark()
    {
        lock (_gate)
        {
            _mark = (byte)(_mark == 0xFF ? 0x5A : _mark + 1);
            return _mark;
        }
    }

    /// <summary>Tipo de 0x0005 función 2.</summary>
    internal static DeviceKind KindFromDeviceType(byte type) => type switch
    {
        0 or 2 => DeviceKind.Keyboard,
        3 or 4 or 5 => DeviceKind.Mouse,
        8 => DeviceKind.Headphones,
        10 or 11 or 12 => DeviceKind.Gamepad,
        14 => DeviceKind.Speaker,
        _ => DeviceKind.Unknown,
    };

    /// <summary>Tipo de la información de emparejado (HID++ 1.0).</summary>
    internal static DeviceKind KindFromPairing(int kind) => kind switch
    {
        1 or 3 => DeviceKind.Keyboard,
        2 or 8 or 9 => DeviceKind.Mouse,
        0xB or 0xC => DeviceKind.Gamepad,
        0xD => DeviceKind.Headphones,
        _ => DeviceKind.Unknown,
    };

    private static string? Ascii(ReadOnlySpan<byte> bytes, int length)
    {
        int n = Math.Min(Math.Max(0, length), bytes.Length);
        var sb = new StringBuilder(n);
        foreach (byte b in bytes[..n])
        {
            if (b is >= 0x20 and <= 0x7E)
            {
                sb.Append((char)b);
            }
        }

        string s = sb.ToString().Trim();
        return s.Length == 0 ? null : (s.Contains("Logi", StringComparison.OrdinalIgnoreCase) ? s : "Logitech " + s);
    }
}
