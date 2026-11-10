namespace BatteryTray.Core;

/// <summary>
/// Decide cuándo avisar de batería baja: una sola vez por cada descarga.
/// Se vuelve a armar cuando el dispositivo se carga o sube claramente del umbral
/// (margen de 5 puntos para no avisar repetidamente si oscila alrededor del umbral).
/// </summary>
public sealed class LowBatteryAlertTracker
{
    public const int RearmMargin = 5;
    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);

    public IReadOnlyList<BatteryDevice> Evaluate(IEnumerable<BatteryDevice> devices, int threshold)
    {
        var toNotify = new List<BatteryDevice>();
        foreach (BatteryDevice d in devices)
        {
            if (d.Level is not int level || d.IsStale || !d.IsConnected)
            {
                continue;
            }

            if (d.IsCharging || level > threshold + RearmMargin)
            {
                _alerted.Remove(d.Id);
                continue;
            }

            if (level <= threshold && _alerted.Add(d.Id))
            {
                toNotify.Add(d);
            }
        }

        return toNotify;
    }
}

/// <summary>Detecta conexiones y desconexiones entre dos instantáneas.</summary>
public sealed class ConnectionChangeTracker
{
    private Dictionary<string, bool>? _previous;

    public IReadOnlyList<(BatteryDevice Device, bool Connected)> Evaluate(IReadOnlyList<BatteryDevice> devices)
    {
        var current = devices.ToDictionary(d => d.Id, d => d.IsConnected, StringComparer.Ordinal);
        var changes = new List<(BatteryDevice, bool)>();

        // La primera instantánea solo sirve de referencia: al arrancar no se avisa de nada.
        if (_previous is not null)
        {
            foreach (BatteryDevice d in devices)
            {
                bool wasConnected = _previous.TryGetValue(d.Id, out bool before) && before;
                if (wasConnected != d.IsConnected)
                {
                    changes.Add((d, d.IsConnected));
                }
            }
        }

        _previous = current;
        return changes;
    }
}
