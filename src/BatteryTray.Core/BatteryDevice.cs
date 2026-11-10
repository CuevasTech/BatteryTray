namespace BatteryTray.Core;

/// <summary>Tipo de dispositivo, usado para elegir el icono.</summary>
public enum DeviceKind
{
    Unknown,
    Headphones,
    Speaker,
    Mouse,
    Keyboard,
    Gamepad,
    Pen,
    Phone,
    Computer,
    Watch,
}

/// <summary>Franja de nivel, usada para el color (normal, aviso, crítico).</summary>
public enum LevelBand
{
    Unknown,
    Normal,
    Warning,
    Critical,
}

/// <summary>Instantánea inmutable de un dispositivo con batería.</summary>
public sealed record BatteryDevice
{
    /// <summary>Identificador estable, p. ej. "bt:0C8A3B123456" o "system:battery".</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public DeviceKind Kind { get; init; }

    /// <summary>Nivel 0-100, o null si no se conoce.</summary>
    public int? Level { get; init; }

    public bool IsConnected { get; init; }

    public bool IsCharging { get; init; }

    /// <summary>True si <see cref="Level"/> es el último valor conocido y no una lectura actual.</summary>
    public bool IsStale { get; init; }

    /// <summary>Proveedor que lo informó (para diagnóstico).</summary>
    public required string Source { get; init; }

    public DateTimeOffset? LastUpdated { get; init; }

    public LevelBand GetBand(int lowThreshold)
    {
        if (Level is not int level)
        {
            return LevelBand.Unknown;
        }

        if (level <= lowThreshold)
        {
            return LevelBand.Critical;
        }

        return level <= lowThreshold + 15 ? LevelBand.Warning : LevelBand.Normal;
    }
}
