using System.Diagnostics;
using BatteryTray.Windows.Native;
using static BatteryTray.Windows.Native.HidNative;

namespace BatteryTray.Windows.Providers.Usb.Protocols;

/// <summary>
/// El método estándar de USB HID para informar de la batería, independiente de la marca:
/// "Battery Strength" (página 0x06, uso 0x20), "Absolute/Relative State Of Charge" (página
/// 0x85, usos 0x65/0x64) o "Battery Strength" de los lápices (página 0x0D, uso 0x3B).
/// Lo usan, por ejemplo, los Apple Magic por cable y los teclados con firmware ZMK.
///
/// Reglas (las mismas que el controlador hid-input de Linux, adaptadas a Windows):
/// - Solo lectura: GET_REPORT de característica o de entrada, o escuchar los informes que el
///   aparato envía solo. Nunca se envía nada.
/// - Windows tiene en exclusiva las colecciones de teclado, ratón, lápiz y pantalla táctil:
///   de ellas solo se pueden leer informes de característica.
/// - En las demás, primero se escucha medio segundo; solo si no llega nada se pide el informe
///   de entrada, y si falla no se vuelve a pedir en la sesión (algunos aparatos se bloquean).
/// - Los SAI (página 0x84, uso 0x04) se excluyen: Windows ya los muestra como batería del sistema.
/// - Por Bluetooth Windows ya da la batería, así que solo se miran los aparatos USB.
/// </summary>
internal sealed class StandardHidBattery : IHidBatteryProtocol
{
    /// <summary>Aparatos que declaran batería pero dan valores falsos o se bloquean (lista de Linux).</summary>
    private static readonly HashSet<(ushort Vid, ushort Pid)> s_ignore =
    [
        (0x05E0, 0x1200), // lector Zebra: siempre 2 %
        (0x17EF, 0x60EE), // receptor del ThinkPad TrackPoint Keyboard II: siempre "no disponible"
        (0x0A5C, 0x4502), // proxy Bluetooth de Broadcom: la consulta se queda colgada
        (0x0A5C, 0x4503),
    ];

    private readonly HashSet<string> _noInputQuery = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string Name => "hid";

    public bool Supports(HidDeviceGroup device) =>
        device.IsUsb
        && !s_ignore.Contains((device.VendorId, device.ProductId))
        && !device.Collections.Any(c => c.UsagePage == 0x84 && c.Usage == 0x04)
        && device.Collections.Any(c => Readable(c).Any());

    /// <summary>Valores que se pueden leer en esa colección según las reglas de acceso de Windows.</summary>
    private static IEnumerable<HidValueInfo> Readable(HidCollection c) =>
        c.BatteryValues.Where(v => v.ReportType == HidpReportType.Feature || !IsExclusive(c));

    /// <summary>Colecciones que Windows abre en exclusiva (teclado, ratón, lápiz, pantalla y panel táctil).</summary>
    internal static bool IsExclusive(HidCollection c) =>
        (c.UsagePage == 0x01 && c.Usage is 0x01 or 0x02 or 0x06 or 0x07)
        || (c.UsagePage == 0x0D && c.Usage is 0x01 or 0x02 or 0x04 or 0x05);

    public IReadOnlyList<HidBatteryReading> Read(HidDeviceGroup device, CancellationToken cancellationToken)
    {
        string name = device.Product ?? "USB";
        foreach (HidCollection collection in device.Collections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryRead(collection) is int level)
            {
                return [new HidBatteryReading(HidIds.Identity(device), name, HidKinds.Guess(device), level, Charging: false)];
            }
        }

        // Declara batería pero ahora no la da (p. ej. el aparato inalámbrico está apagado).
        return [new HidBatteryReading(HidIds.Identity(device), name, HidKinds.Guess(device), null, Charging: false)];
    }

    private int? TryRead(HidCollection collection)
    {
        List<HidValueInfo> values = Readable(collection).ToList();
        if (values.Count == 0)
        {
            return null;
        }

        // Informes de característica: se pueden pedir incluso a un teclado o ratón (con acceso 0).
        using (HidDevice? query = HidDevice.OpenForQuery(collection))
        {
            if (query is not null)
            {
                foreach (HidValueInfo value in values.Where(v => v.ReportType == HidpReportType.Feature && collection.FeatureReportLength > 0))
                {
                    byte[] report = new byte[collection.FeatureReportLength];
                    report[0] = value.ReportId;
                    if (query.GetFeature(report) && Extract(query, value, report) is int level)
                    {
                        return level;
                    }
                }
            }
        }

        // Informes de entrada (solo colecciones compartidas): primero escuchar, luego preguntar.
        List<HidValueInfo> inputs = values.Where(v => v.ReportType == HidpReportType.Input).ToList();
        if (inputs.Count == 0 || collection.InputReportLength <= 0)
        {
            return null;
        }

        using (HidDevice? reader = HidDevice.OpenForRead(collection))
        {
            if (reader is not null)
            {
                byte[] buffer = new byte[collection.InputReportLength];
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 500)
                {
                    int n = reader.Read(buffer, 100);
                    if (n < 0)
                    {
                        break;
                    }

                    foreach (HidValueInfo value in inputs.Where(v => n > 0 && buffer[0] == v.ReportId))
                    {
                        if (Extract(reader, value, buffer) is int level)
                        {
                            return level;
                        }
                    }
                }
            }
        }

        lock (_gate)
        {
            if (_noInputQuery.Contains(collection.Path))
            {
                return null;
            }
        }

        using HidDevice? device = HidDevice.OpenForQuery(collection);
        foreach (HidValueInfo value in inputs)
        {
            byte[] report = new byte[collection.InputReportLength];
            report[0] = value.ReportId;
            if (device is not null && device.GetInputReport(report))
            {
                if (Extract(device, value, report) is int level)
                {
                    return level;
                }
            }
            else
            {
                lock (_gate)
                {
                    _noInputQuery.Add(collection.Path); // no se insiste: algunos aparatos se bloquean
                }

                break;
            }
        }

        return null;
    }

    private static int? Extract(HidDevice device, HidValueInfo value, byte[] report) =>
        device.TryGetUsageValue(value.ReportType, report, value.UsagePage, value.LinkCollection, value.Usage, out uint raw)
            ? Scale(raw, value)
            : null;

    /// <summary>Convierte el valor al 0-100 % con el rango que declara el aparato; null si no es válido.</summary>
    internal static int? Scale(uint raw, HidValueInfo value)
    {
        long min = value.LogicalMin;
        long max = value.LogicalMax;

        // Descriptores que escriben 255 como "25 FF" (queda -1): con mínimo positivo es sin signo.
        if (min >= 0 && max < 0 && value.BitSize is > 0 and < 32)
        {
            max &= (1L << value.BitSize) - 1;
        }

        // Con un mínimo negativo, el valor llega como entero con signo de BitSize bits.
        long v = raw;
        if (min < 0 && value.BitSize is > 0 and < 32 && (raw & (1u << (value.BitSize - 1))) != 0)
        {
            v = raw - (1L << value.BitSize);
        }

        // "State of charge" es siempre un porcentaje (Apple declara 0-255 pero envía 0-100).
        if (value.UsagePage == 0x85)
        {
            if (v > 100 && v <= max)
            {
                v = 100;
            }

            min = 0;
            max = 100;
        }

        if (max <= min || v < min || v > max)
        {
            return null; // fuera de rango = "desconocido"
        }

        // Las pantallas táctiles dan 0 para un lápiz que no existe.
        if (value.UsagePage == 0x0D && v == 0)
        {
            return null;
        }

        return (int)((v - min) * 100 / (max - min));
    }
}
