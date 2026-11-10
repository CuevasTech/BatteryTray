namespace BatteryTray.Core;

/// <summary>
/// Colores para distinguir los dispositivos (el relleno de su batería en la bandeja).
/// Se evitan verde, amarillo y rojo porque Windows ya los usa para cargando, ahorro y batería baja.
/// Cada color tiene una versión para fondo oscuro y otra para fondo claro.
/// </summary>
public static class DeviceColors
{
    /// <summary>Sin color: relleno igual que el de Windows (blanco, verde al cargar, rojo si está baja).</summary>
    public const string None = "none";

    public static IReadOnlyList<string> Palette { get; } = ["blue", "purple", "orange", "pink", "teal", "indigo"];

    private static readonly Dictionary<string, (uint Dark, uint Light)> s_rgb = new(StringComparer.Ordinal)
    {
        ["blue"] = (0x60CDFF, 0x005FB8),
        ["purple"] = (0xC5A3FF, 0x6B3FC9),
        ["orange"] = (0xFFAE57, 0xB04E00),
        ["pink"] = (0xFF8AD0, 0xB0127F),
        ["teal"] = (0x45E0CC, 0x00756E),
        ["indigo"] = (0x9AA6FF, 0x3E4FD1),
    };

    public static bool IsValid(string? key) => key is not null && (key == None || s_rgb.ContainsKey(key));

    /// <summary>RGB (0xRRGGBB) del color, o null si es "none" o no existe.</summary>
    public static uint? Rgb(string? key, bool darkBackground) =>
        key is not null && s_rgb.TryGetValue(key, out (uint Dark, uint Light) c) ? (darkBackground ? c.Dark : c.Light) : null;

    /// <summary>Elige el primer color de la paleta que no use ya otro dispositivo.</summary>
    public static string NextFree(IEnumerable<string> used)
    {
        var taken = new HashSet<string>(used, StringComparer.Ordinal);
        foreach (string key in Palette)
        {
            if (!taken.Contains(key))
            {
                return key;
            }
        }

        // Más dispositivos que colores: se repite la paleta en orden.
        return Palette[taken.Count % Palette.Count];
    }
}
