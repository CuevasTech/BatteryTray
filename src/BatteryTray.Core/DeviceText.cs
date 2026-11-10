using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BatteryTray.Core;

/// <summary>Utilidades de texto para datos que vienen de los dispositivos.</summary>
public static partial class DeviceText
{
    public const int MaxNameLength = 64;

    /// <summary>
    /// Limpia un nombre de dispositivo. Los nombres Bluetooth los elige quien fabrica
    /// (o configura) el dispositivo, así que se tratan como entrada no confiable:
    /// se quitan caracteres de control y de reordenación bidireccional (que permiten
    /// disfrazar texto, p. ej. "U+202E") y se limita la longitud.
    /// </summary>
    public static string Sanitize(string? value, string fallback, int maxLength = MaxNameLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var sb = new StringBuilder(Math.Min(value.Length, maxLength + 1));
        bool lastWasSpace = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (lastWasSpace || sb.Length == 0)
                {
                    continue;
                }

                sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            if (char.IsControl(c) || IsBidiControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
        }

        string result = sb.ToString().Trim();
        if (result.Length == 0)
        {
            return fallback;
        }

        if (result.Length > maxLength)
        {
            // Evita cortar un par sustituto por la mitad.
            int cut = maxLength - 1;
            if (char.IsHighSurrogate(result[cut - 1]))
            {
                cut--;
            }

            result = string.Concat(result.AsSpan(0, cut), "…");
        }

        return result;
    }

    /// <summary>Recorta un texto a una longitud máxima añadiendo "…".</summary>
    public static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        int cut = Math.Max(0, maxLength - 1);
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), "…");
    }

    /// <summary>
    /// Enmascara una dirección Bluetooth para diagnóstico y registros:
    /// solo deja ver los dos últimos bytes (suficiente para distinguir dispositivos).
    /// </summary>
    public static string MaskAddress(ulong address) =>
        $"XX:XX:XX:XX:{(address >> 8) & 0xFF:X2}:{address & 0xFF:X2}";

    /// <summary>Enmascara cualquier dirección de 12 dígitos hex que aparezca en un texto (p. ej. un id de instancia PnP).</summary>
    public static string MaskAddressesInText(string text) =>
        HexAddressRegex().Replace(text, m => "XXXXXXXX" + m.Value[^4..]);

    private static bool IsBidiControl(char c) =>
        c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏' or '؜';

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])")]
    private static partial Regex HexAddressRegex();
}
