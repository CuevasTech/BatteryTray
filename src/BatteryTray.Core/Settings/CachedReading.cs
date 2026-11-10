namespace BatteryTray.Core.Settings;

/// <summary>Último nivel conocido de un dispositivo, para mostrarlo cuando se desconecta.</summary>
public sealed record CachedReading
{
    public required string Name { get; init; }

    public DeviceKind Kind { get; init; }

    public int Level { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}
