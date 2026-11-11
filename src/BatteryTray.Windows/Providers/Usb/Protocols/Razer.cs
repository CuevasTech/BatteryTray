using BatteryTray.Core;
using BatteryTray.Windows.Native;

namespace BatteryTray.Windows.Providers.Usb.Protocols;

/// <summary>
/// Ratones, teclados y bases de Razer (receptores HyperSpeed y cable).
///
/// Protocolo: el informe de 90 bytes de Razer, que en Windows viaja como informe de
/// característica de 91 bytes (id 0 + 90). Se envía la consulta con SET_FEATURE, se espera
/// lo que pide cada modelo y se lee la respuesta con GET_FEATURE.
/// Fuente del protocolo: openrazer (driver/razercommon.h, razerchromacommon.c,
/// razermouse_driver.c/.h, razerkbd_driver.c; GPL-2.0-or-later) y las tablas de modelos de
/// razer-battery-report (MIT) y HaloBattery (MIT). Implementación propia.
///
/// Seguridad: solo modelos del catálogo y solo consultas (GET, id de comando ≥ 0x80):
/// 07/80 batería, 07/84 carga y 00/82 número de serie. Nunca se envían comandos de
/// configuración (modo, DPI, iluminación, emparejado...).
/// </summary>
internal sealed class Razer : IHidBatteryProtocol
{
    public const ushort VendorId = 0x1532;
    private const int ReportLength = 91;
    private const int SendAttempts = 3;
    private static readonly TimeSpan s_replyWindow = TimeSpan.FromSeconds(1);
    private const string SharedMutexName = @"Global\RazerLinkReadWriteGuardMutex";

    private readonly IHidOpener _opener;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _workingPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _serials = new(StringComparer.Ordinal);

    public Razer(IHidOpener? opener = null) => _opener = opener ?? RealHidOpener.Instance;

    public string Name => "razer";

    /// <summary>Un modelo: nombre, id de transacción, espera entre envío y lectura, si informa de carga e interfaz.</summary>
    internal sealed record Model(string Name, DeviceKind Kind, byte TransactionId, double WaitMs, bool ReportsCharging, int Interface);

    private static Model M(string name, byte tid, double wait, bool charging = true, int iface = 0) => new(name, DeviceKind.Mouse, tid, wait, charging, iface);

    private static Model K(string name, byte tid, double wait, int iface) => new(name, DeviceKind.Keyboard, tid, wait, true, iface);

    /// <summary>Catálogo (PID → modelo). Los de pilas AA no informan de carga.</summary>
    internal static IReadOnlyDictionary<ushort, Model> Catalog { get; } = new Dictionary<ushort, Model>
    {
        // Ratones (interfaz 0, colección de ratón).
        [0x001F] = M("Razer Naga Epic", 0xFF, 0.6),
        [0x0024] = M("Razer Mamba", 0xFF, 0.6),
        [0x0025] = M("Razer Mamba", 0xFF, 0.6),
        [0x0032] = M("Razer Ouroboros", 0xFF, 0.6),
        [0x003E] = M("Razer Naga Epic Chroma", 0xFF, 0.6),
        [0x003F] = M("Razer Naga Epic Chroma", 0xFF, 0.6),
        [0x0044] = M("Razer Mamba Chroma", 0xFF, 0.6),
        [0x0045] = M("Razer Mamba Chroma", 0xFF, 0.6),
        [0x0059] = M("Razer Lancehead", 0x3F, 0.6),
        [0x005A] = M("Razer Lancehead", 0x3F, 0.6),
        [0x0062] = M("Razer Atheris", 0x1F, 400, charging: false),
        [0x006F] = M("Razer Lancehead Wireless", 0x1F, 31),
        [0x0070] = M("Razer Lancehead Wireless", 0x1F, 31),
        [0x0072] = M("Razer Mamba Wireless", 0x3F, 31),
        [0x0073] = M("Razer Mamba Wireless", 0x3F, 31),
        [0x0077] = M("Razer Pro Click", 0x1F, 31),
        [0x0080] = M("Razer Pro Click", 0x1F, 31),
        [0x007A] = M("Razer Viper Ultimate", 0xFF, 59.9),
        [0x007B] = M("Razer Viper Ultimate", 0xFF, 59.9),
        [0x007C] = M("Razer DeathAdder V2 Pro", 0x3F, 59.9),
        [0x007D] = M("Razer DeathAdder V2 Pro", 0x3F, 59.9),
        [0x0083] = M("Razer Basilisk X HyperSpeed", 0xFF, 31, charging: false),
        [0x0086] = M("Razer Basilisk Ultimate", 0x1F, 31),
        [0x0088] = M("Razer Basilisk Ultimate", 0x1F, 31),
        [0x008F] = M("Razer Naga Pro", 0x1F, 0.6),
        [0x0090] = M("Razer Naga Pro", 0x1F, 31),
        [0x0094] = M("Razer Orochi V2", 0x1F, 400, charging: false),
        [0x0095] = M("Razer Orochi V2", 0x1F, 400, charging: false),
        [0x009A] = M("Razer Pro Click Mini", 0x1F, 31),
        [0x009C] = M("Razer DeathAdder V2 X HyperSpeed", 0x1F, 31, charging: false),
        [0x009E] = M("Razer Viper Mini SE", 0x1F, 59.9),
        [0x009F] = M("Razer Viper Mini SE", 0x1F, 59.9),
        [0x00A5] = M("Razer Viper V2 Pro", 0x1F, 31),
        [0x00A6] = M("Razer Viper V2 Pro", 0x1F, 31),
        [0x00A7] = M("Razer Naga V2 Pro", 0x1F, 31),
        [0x00A8] = M("Razer Naga V2 Pro", 0x1F, 31),
        [0x00AA] = M("Razer Basilisk V3 Pro", 0x1F, 31),
        [0x00AB] = M("Razer Basilisk V3 Pro", 0x1F, 31),
        [0x00AF] = M("Razer Cobra Pro", 0x1F, 31),
        [0x00B0] = M("Razer Cobra Pro", 0x1F, 31),
        [0x00B3] = M("Razer HyperPolling Wireless Dongle", 0x1F, 59.9),
        [0x00B4] = M("Razer Naga V2 HyperSpeed", 0x1F, 31, charging: false),
        [0x00B6] = M("Razer DeathAdder V3 Pro", 0x1F, 31),
        [0x00B7] = M("Razer DeathAdder V3 Pro", 0x1F, 31),
        [0x00C2] = M("Razer DeathAdder V3 Pro", 0x1F, 31),
        [0x00C3] = M("Razer DeathAdder V3 Pro", 0x1F, 31),
        [0x00B8] = M("Razer Viper V3 HyperSpeed", 0x1F, 59.9, charging: false),
        [0x00B9] = M("Razer Basilisk V3 X HyperSpeed", 0x1F, 31, charging: false),
        [0x00BE] = M("Razer DeathAdder V4 Pro", 0x1F, 31),
        [0x00BF] = M("Razer DeathAdder V4 Pro", 0x1F, 31),
        [0x00C0] = M("Razer Viper V3 Pro", 0x1F, 31),
        [0x00C1] = M("Razer Viper V3 Pro", 0x1F, 59.9),
        [0x00C4] = M("Razer DeathAdder V3 HyperSpeed", 0x1F, 31),
        [0x00C5] = M("Razer DeathAdder V3 HyperSpeed", 0x1F, 31),
        [0x00C7] = M("Razer Pro Click V2 Vertical", 0x1F, 31),
        [0x00C8] = M("Razer Pro Click V2 Vertical", 0x1F, 31),
        [0x00CC] = M("Razer Basilisk V3 Pro 35K", 0x1F, 31),
        [0x00CD] = M("Razer Basilisk V3 Pro 35K", 0x1F, 31),
        [0x00D0] = M("Razer Pro Click V2", 0x1F, 31),
        [0x00D1] = M("Razer Pro Click V2", 0x1F, 31),
        [0x00D3] = M("Razer Basilisk Mobile", 0x1F, 31, charging: false),
        [0x00D4] = M("Razer Basilisk Mobile", 0x1F, 31, charging: false),
        [0x00D6] = M("Razer Basilisk V3 Pro 35K", 0x1F, 31),
        [0x00D7] = M("Razer Basilisk V3 Pro 35K", 0x1F, 31),
        [0x00DA] = M("Razer Cobra HyperSpeed", 0x1F, 31),
        [0x00DB] = M("Razer Cobra HyperSpeed", 0x1F, 31),
        [0x00DE] = M("Razer Viper V3 Pro SE", 0x1F, 31),
        [0x00DF] = M("Razer Viper V3 Pro SE", 0x1F, 59.9),
        [0x00E2] = M("Razer Basilisk V4 Pro", 0x1F, 59.9, iface: 3),
        [0x00E3] = M("Razer Basilisk V4 Pro", 0x1F, 59.9, iface: 3),
        [0x00E5] = M("Razer Viper V4 Pro", 0x1F, 59.9, iface: 3),
        [0x00E6] = M("Razer Viper V4 Pro", 0x1F, 59.9, iface: 3),

        // Base Mouse Dock Pro: reenvía al ratón las consultas con el id de transacción del ratón.
        [0x00A4] = M("Razer Mouse Dock Pro", 0x1F, 31),

        // Teclados (cable: espera corta; receptor: 4,9 ms e id 0x9F).
        [0x025A] = K("Razer BlackWidow V3 Pro", 0x3F, 0.6, 2),
        [0x025C] = K("Razer BlackWidow V3 Pro", 0x9F, 4.9, 2),
        [0x0258] = K("Razer BlackWidow V3 Mini HyperSpeed", 0x1F, 0.6, 3),
        [0x0271] = K("Razer BlackWidow V3 Mini HyperSpeed", 0x9F, 4.9, 3),
        [0x0292] = K("Razer DeathStalker V2 Pro", 0x1F, 0.6, 3),
        [0x0290] = K("Razer DeathStalker V2 Pro", 0x9F, 4.9, 2),
        [0x0298] = K("Razer DeathStalker V2 Pro TKL", 0x1F, 0.6, 3),
        [0x0296] = K("Razer DeathStalker V2 Pro TKL", 0x9F, 4.9, 2),
        [0x02B9] = K("Razer BlackWidow V4 Mini HyperSpeed", 0x1F, 0.6, 3),
        [0x02BA] = K("Razer BlackWidow V4 Mini HyperSpeed", 0x9F, 4.9, 3),
        [0x02D7] = K("Razer BlackWidow V4 TKL HyperSpeed", 0x1F, 0.6, 3),
        [0x02D5] = K("Razer BlackWidow V4 TKL HyperSpeed", 0x9F, 4.9, 2),
        [0x02CD] = K("Razer Joro", 0x1F, 0.6, 3),
    };

    public bool Supports(HidDeviceGroup device) =>
        device.VendorId == VendorId
        && device.IsUsb
        && Catalog.ContainsKey(device.ProductId)
        && device.Collections.Any(c => c.FeatureReportLength == ReportLength);

    public IReadOnlyList<HidBatteryReading> Read(HidDeviceGroup device, CancellationToken cancellationToken)
    {
        Model model = Catalog[device.ProductId];
        foreach (HidCollection collection in Candidates(device, model))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Las colecciones de ratón y teclado las tiene Windows en exclusiva: se abren sin
            // permisos, que basta para los informes de característica.
            using IHidChannel? channel = _opener.OpenForQuery(collection);
            if (channel is null)
            {
                continue;
            }

            Result battery = Query(channel, model, 0x07, 0x80);
            if (battery.Status is ReplyStatus.TransportError or ReplyStatus.NotSupported or ReplyStatus.NoAnswer)
            {
                continue; // otra colección del mismo aparato
            }

            Remember(_workingPath, device.Key, collection.Path);
            string identity = Identity(device, model, channel);
            if (battery.Status != ReplyStatus.Ok)
            {
                // Dormido, apagado o conectado por cable a otro puerto: se mantiene el último nivel.
                return [new HidBatteryReading(identity, model.Name, model.Kind, null, Charging: false)];
            }

            bool charging = model.ReportsCharging && Query(channel, model, 0x07, 0x84) is { Status: ReplyStatus.Ok, Value: > 0 };
            return [new HidBatteryReading(identity, model.Name, model.Kind, ToPercent(battery.Value), charging)];
        }

        return [];
    }

    /// <summary>Colecciones de 91 bytes, primero la que funcionó, luego la interfaz del catálogo y la de ratón.</summary>
    private IEnumerable<HidCollection> Candidates(HidDeviceGroup device, Model model)
    {
        string? known;
        lock (_gate)
        {
            _workingPath.TryGetValue(device.Key, out known);
        }

        return device.Collections
            .Where(c => c.FeatureReportLength == ReportLength)
            .OrderByDescending(c => c.Path == known)
            .ThenByDescending(c => c.InterfaceNumber == model.Interface)
            .ThenByDescending(c => c.UsagePage == 0x01 && c.Usage == 0x02);
    }

    /// <summary>
    /// El número de serie identifica el ratón aunque cambie de receptor a cable; si no se
    /// puede leer, se usa el modelo (así el mismo ratón por cable y por receptor es uno solo).
    /// </summary>
    private string Identity(HidDeviceGroup device, Model model, IHidChannel channel)
    {
        string? serial;
        lock (_gate)
        {
            _serials.TryGetValue(device.Key, out serial);
        }

        if (serial is null)
        {
            Result r = Query(channel, model, 0x00, 0x82, dataSize: 0x16, keepArguments: true);
            serial = r.Status == ReplyStatus.Ok ? ParseSerial(r.Arguments) : null;
            Remember(_serials, device.Key, serial ?? "");
        }

        return string.IsNullOrEmpty(serial) ? $"{model.Name}" : $"{model.Name}:{serial}";
    }

    private void Remember(Dictionary<string, string> map, string key, string value)
    {
        lock (_gate)
        {
            map[key] = value;
        }
    }

    /// <summary>0-255 → 0-100 redondeando.</summary>
    internal static int ToPercent(byte raw) => (raw * 100 + 127) / 255;

    internal static string? ParseSerial(ReadOnlySpan<byte> arguments)
    {
        int length = Math.Min(arguments.Length, 0x16);
        Span<char> chars = stackalloc char[length];
        int n = 0;
        foreach (byte b in arguments[..length])
        {
            if (b == 0)
            {
                break;
            }

            if (b is < 0x20 or > 0x7E)
            {
                return null; // no es un número de serie de texto
            }

            chars[n++] = (char)b;
        }

        return n > 0 ? new string(chars[..n]) : null;
    }

    internal enum ReplyStatus
    {
        Ok,
        Asleep,
        Failure,
        NotSupported,
        NoAnswer,
        TransportError,
    }

    internal readonly record struct Result(ReplyStatus Status, byte Value, byte[] Arguments);

    /// <summary>Construye la consulta: id de informe 0 y los 90 bytes del protocolo.</summary>
    internal static byte[] BuildRequest(byte transactionId, byte commandClass, byte commandId, byte dataSize)
    {
        byte[] r = new byte[ReportLength];
        r[2] = transactionId;
        r[6] = dataSize;
        r[7] = commandClass;
        r[8] = commandId;
        r[89] = Crc(r);
        return r;
    }

    /// <summary>XOR de los bytes 2 a 87 del informe de 90 (3 a 88 del búfer de 91).</summary>
    internal static byte Crc(ReadOnlySpan<byte> buffer)
    {
        byte crc = 0;
        for (int i = 3; i <= 88; i++)
        {
            crc ^= buffer[i];
        }

        return crc;
    }

    internal Result Query(IHidChannel channel, Model model, byte commandClass, byte commandId, byte dataSize = 0x02, bool keepArguments = false)
    {
        // Solo consultas: el bit 7 del id de comando indica "leer".
        if (commandId < 0x80)
        {
            throw new InvalidOperationException("Solo se permiten comandos de lectura.");
        }

        byte[] request = BuildRequest(model.TransactionId, commandClass, commandId, dataSize);
        using SharedLock _ = SharedLock.Acquire(SharedMutexName);
        for (int send = 0; send < SendAttempts; send++)
        {
            if (!channel.SetFeature(request))
            {
                return new Result(ReplyStatus.TransportError, 0, []);
            }

            Wait(model.WaitMs);
            DateTime deadline = DateTime.UtcNow + s_replyWindow;
            while (true)
            {
                byte[] reply = new byte[ReportLength];
                if (!channel.GetFeature(reply))
                {
                    return new Result(ReplyStatus.TransportError, 0, []);
                }

                // Solo vale una respuesta íntegra a esta misma consulta (Synapse u otros
                // programas pueden estar usando el aparato a la vez).
                if (Crc(reply) == reply[89] && reply[7] == commandClass && reply[8] == commandId && reply[3] == 0 && reply[4] == 0)
                {
                    switch (reply[1])
                    {
                        case 0x02:
                            return new Result(ReplyStatus.Ok, reply[10], keepArguments ? reply[9..89] : []);
                        case 0x04:
                            return new Result(ReplyStatus.Asleep, 0, []);
                        case 0x05:
                            return new Result(ReplyStatus.NotSupported, 0, []);
                        case 0x03:
                            return new Result(ReplyStatus.Failure, 0, []);
                    }
                }

                // 0x00 (sin procesar), 0x01 (ocupado) u otra respuesta: se vuelve a leer.
                if (DateTime.UtcNow > deadline)
                {
                    break;
                }

                Thread.Sleep(15);
            }
        }

        return new Result(ReplyStatus.NoAnswer, 0, []);
    }

    private static void Wait(double milliseconds) => Thread.Sleep(Math.Max(1, (int)Math.Ceiling(milliseconds)));

    /// <summary>
    /// Mutex que comparten OpenRGB y otras herramientas para no pisarse con los aparatos
    /// Razer. Si existe, se usa con un tiempo máximo; si no, se sigue sin él.
    /// </summary>
    private readonly struct SharedLock : IDisposable
    {
        private readonly Mutex? _mutex;
        private readonly bool _owned;

        private SharedLock(Mutex? mutex, bool owned)
        {
            _mutex = mutex;
            _owned = owned;
        }

        public static SharedLock Acquire(string name)
        {
            try
            {
                if (Mutex.TryOpenExisting(name, out Mutex? mutex))
                {
                    bool owned;
                    try
                    {
                        owned = mutex.WaitOne(500);
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true;
                    }

                    return new SharedLock(mutex, owned);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                // Sin permiso o sin mutex: se sigue sin él.
            }

            return default;
        }

        public void Dispose()
        {
            if (_owned)
            {
                _mutex?.ReleaseMutex();
            }

            _mutex?.Dispose();
        }
    }
}
