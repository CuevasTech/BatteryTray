using System.Globalization;
using System.Text.RegularExpressions;

namespace BatteryTray.Core;

/// <summary>Utilidades para identificar dispositivos Bluetooth.</summary>
public static partial class BluetoothIds
{
    public const string Prefix = "bt:";

    /// <summary>Id estable de la app para una dirección Bluetooth.</summary>
    public static string ToDeviceId(ulong address) => Prefix + address.ToString("X12", CultureInfo.InvariantCulture);

    /// <summary>
    /// Convierte el texto de DEVPKEY_Bluetooth_DeviceAddress ("0C8A3B123456", hex sin separadores)
    /// o un formato con separadores ("0C:8A:3B:12:34:56").
    /// </summary>
    public static bool TryParseAddress(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Span<char> hex = stackalloc char[12];
        int n = 0;
        foreach (char c in text.Trim())
        {
            if (c is ':' or '-')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(c) || n == 12)
            {
                return false;
            }

            hex[n++] = c;
        }

        return n == 12
            && ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address)
            && address != 0;
    }

    /// <summary>
    /// Intenta sacar la dirección de un id de instancia PnP, por ejemplo:
    /// <c>BTHENUM\{0000111E-...}_VID&amp;0001004C_PID&amp;200E\7&amp;2C2A6C1F&amp;0&amp;A4C6F0123456_C00000000</c>,
    /// <c>BTHENUM\DEV_A4C6F0123456\...</c>, <c>BTHLE\DEV_A4C6F0123456\...</c> o
    /// <c>BTHLEDEVICE\{...}_DEV_VID&amp;...._A4C6F0123456\...</c>.
    /// </summary>
    public static bool TryParseAddressFromInstanceId(string? instanceId, out ulong address)
    {
        address = 0;
        if (string.IsNullOrEmpty(instanceId))
        {
            return false;
        }

        Match m = DevRegex().Match(instanceId);
        if (!m.Success)
        {
            m = ServiceRegex().Match(instanceId);
        }

        if (!m.Success)
        {
            m = LeServiceRegex().Match(instanceId);
        }

        return m.Success && TryParseAddress(m.Groups["addr"].Value, out address);
    }

    [GeneratedRegex(@"\\DEV_(?<addr>[0-9A-Fa-f]{12})(?:\\|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DevRegex();

    [GeneratedRegex(@"&(?<addr>[0-9A-Fa-f]{12})_C[0-9A-Fa-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceRegex();

    [GeneratedRegex(@"_(?<addr>[0-9A-Fa-f]{12})\\", RegexOptions.CultureInvariant)]
    private static partial Regex LeServiceRegex();
}
