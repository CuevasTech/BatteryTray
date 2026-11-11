using BatteryTray.Windows.Native;
using static BatteryTray.Windows.Native.HidNative;

namespace BatteryTray.Windows.Providers.Usb.Protocols;

/// <summary>
/// El método estándar de USB HID para informar de la batería, independiente de la marca:
/// el uso "Battery Strength" (página 0x06, uso 0x20), "AbsoluteStateOfCharge"/"RemainingCapacity"
/// (página 0x85) o "Battery Strength" de los lápices (página 0x0D, uso 0x3B).
///
/// Solo lee: pide el informe de característica o el último informe de entrada que contiene
/// el valor (GET_REPORT) y lo escala con el mínimo y máximo que declara el aparato.
/// Nunca escribe nada. Se queda con los aparatos USB: por Bluetooth Windows ya da la
/// batería y los SAI (que usan las mismas páginas) los muestra Windows como batería del sistema.
/// </summary>
internal sealed class StandardHidBattery : IHidBatteryProtocol
{
    public string Name => "hid";

    public bool Supports(HidDeviceGroup device) =>
        device.IsUsb
        && !IsPowerDevice(device)
        && device.Collections.Any(c => c.BatteryValues.Count > 0);

    /// <summary>SAI y fuentes de alimentación: su colección principal es de las páginas 0x84/0x85.</summary>
    private static bool IsPowerDevice(HidDeviceGroup device) =>
        device.Collections.Any(c => c.UsagePage is 0x84 or 0x85);

    public IReadOnlyList<HidBatteryReading> Read(HidDeviceGroup device, CancellationToken cancellationToken)
    {
        foreach (HidCollection collection in device.Collections.Where(c => c.BatteryValues.Count > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryRead(collection) is int level)
            {
                return [new HidBatteryReading(HidIds.Identity(device), device.Product ?? "USB", HidKinds.Guess(device), level, Charging: false)];
            }
        }

        // Declara batería pero ahora no la da (p. ej. el aparato inalámbrico está apagado).
        return [new HidBatteryReading(HidIds.Identity(device), device.Product ?? "USB", HidKinds.Guess(device), null, Charging: false)];
    }

    private static int? TryRead(HidCollection collection)
    {
        // Con acceso 0 se puede consultar incluso un teclado o ratón que Windows tiene abierto en exclusiva.
        using HidDevice? device = HidDevice.OpenForQuery(collection);
        if (device is null)
        {
            return null;
        }

        foreach (HidValueInfo value in collection.BatteryValues)
        {
            int length = value.ReportType == HidpReportType.Feature ? collection.FeatureReportLength : collection.InputReportLength;
            if (length <= 0)
            {
                continue;
            }

            byte[] report = new byte[length];
            report[0] = value.ReportId;
            bool ok = value.ReportType == HidpReportType.Feature ? device.GetFeature(report) : device.GetInputReport(report);
            if (ok && device.TryGetUsageValue(value.ReportType, report, value.UsagePage, value.LinkCollection, value.Usage, out uint raw)
                && Scale(raw, value) is int level)
            {
                return level;
            }
        }

        return null;
    }

    /// <summary>Convierte el valor al 0-100 % con el rango que declara el aparato; null si está fuera de rango.</summary>
    internal static int? Scale(uint raw, HidValueInfo value)
    {
        long min = value.LogicalMin;
        long max = value.LogicalMax;

        // Con un mínimo negativo, el valor llega como entero con signo de BitSize bits.
        long v = raw;
        if (min < 0 && value.BitSize is > 0 and < 32 && (raw & (1u << (value.BitSize - 1))) != 0)
        {
            v = raw - (1L << value.BitSize);
        }

        if (max <= min || v < min || v > max)
        {
            return null;
        }

        return (int)Math.Round((v - min) * 100.0 / (max - min));
    }
}
