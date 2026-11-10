using System.Text;
using BatteryTray.Core.Settings;

namespace BatteryTray.Core;

/// <summary>
/// Junta las lecturas de todos los proveedores en una sola lista y recuerda el último
/// nivel conocido de cada dispositivo (para mostrarlo cuando se desconecta).
/// </summary>
public sealed class BatteryMonitor : IAsyncDisposable
{
    private readonly IReadOnlyList<IBatteryProvider> _providers;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Lock _recomputeGate = new();
    private readonly Dictionary<string, CachedReading> _cache;
    private IReadOnlyList<BatteryDevice> _devices = [];
    private bool _cacheDirty;

    public BatteryMonitor(
        IEnumerable<IBatteryProvider> providers,
        IDictionary<string, CachedReading>? initialCache = null,
        TimeProvider? time = null)
    {
        _providers = providers.ToList();
        _time = time ?? TimeProvider.System;
        _cache = initialCache is null
            ? new Dictionary<string, CachedReading>(StringComparer.Ordinal)
            : new Dictionary<string, CachedReading>(initialCache, StringComparer.Ordinal);

        foreach (IBatteryProvider provider in _providers)
        {
            provider.Changed += OnProviderChanged;
        }
    }

    /// <summary>Se lanza (desde cualquier hilo) cuando cambia <see cref="Devices"/>.</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>Lista ordenada: conectados primero, después por nombre.</summary>
    public IReadOnlyList<BatteryDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices;
            }
        }
    }

    public IReadOnlyList<IBatteryProvider> Providers => _providers;

    /// <summary>Copia de la caché de últimos niveles conocidos.</summary>
    public IReadOnlyDictionary<string, CachedReading> GetCacheSnapshot(out bool wasDirty)
    {
        lock (_gate)
        {
            wasDirty = _cacheDirty;
            _cacheDirty = false;
            return new Dictionary<string, CachedReading>(_cache, StringComparer.Ordinal);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (IBatteryProvider provider in _providers)
        {
            try
            {
                await provider.StartAsync(cancellationToken).ConfigureAwait(false);
                AppLog.Info($"Proveedor iniciado: {provider.Name}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un proveedor roto no debe impedir que funcionen los demás.
                AppLog.Error($"No se pudo iniciar el proveedor {provider.Name}", ex);
            }
        }

        Recompute();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        foreach (IBatteryProvider provider in _providers)
        {
            try
            {
                await provider.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Error($"Fallo al actualizar {provider.Name}", ex);
            }
        }

        Recompute();
    }

    /// <summary>Olvida el último nivel guardado de un dispositivo.</summary>
    public void Forget(string deviceId)
    {
        lock (_gate)
        {
            _cacheDirty |= _cache.Remove(deviceId);
        }

        Recompute();
    }

    public async Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        foreach (IBatteryProvider provider in _providers)
        {
            sb.AppendLine($"## {provider.Name}");
            try
            {
                sb.AppendLine(await provider.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sb.AppendLine($"(error: {ex.GetType().Name}: {ex.Message})");
            }
        }

        sb.AppendLine("## Lista combinada");
        foreach (BatteryDevice d in Devices)
        {
            sb.AppendLine(
                $"- {d.Id[..Math.Min(d.Id.Length, 3)]}…{d.Id[^4..]} | {d.Name} | {d.Kind} | " +
                $"{(d.Level is int l ? l + "%" : "?")}{(d.IsStale ? " (último conocido)" : "")} | " +
                $"{(d.IsConnected ? "conectado" : "desconectado")}{(d.IsCharging ? " | cargando" : "")} | {d.Source}");
        }

        return sb.ToString();
    }

    private void OnProviderChanged(object? sender, EventArgs e) => Recompute();

    internal void Recompute()
    {
        // Serializa los recálculos: los proveedores avisan desde hilos distintos.
        lock (_recomputeGate)
        {
            RecomputeCore();
        }
    }

    private void RecomputeCore()
    {
        DateTimeOffset now = _time.GetUtcNow();
        var merged = new Dictionary<string, BatteryDevice>(StringComparer.Ordinal);

        foreach (IBatteryProvider provider in _providers)
        {
            IReadOnlyList<BatteryDevice> devices;
            try
            {
                devices = provider.Devices;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Lista no disponible en {provider.Name}", ex);
                continue;
            }

            foreach (BatteryDevice device in devices)
            {
                merged[device.Id] = merged.TryGetValue(device.Id, out BatteryDevice? existing)
                    ? Prefer(existing, device)
                    : device;
            }
        }

        bool changed;
        lock (_gate)
        {
            var result = new List<BatteryDevice>(merged.Count);
            foreach (BatteryDevice device in merged.Values)
            {
                if (device.Level is int && device.IsStale)
                {
                    // El propio proveedor da un último valor conocido.
                    result.Add(device);
                }
                else if (device.Level is int level)
                {
                    // Lectura actual: se guarda como último valor conocido.
                    if (!_cache.TryGetValue(device.Id, out CachedReading? old)
                        || old.Level != level || old.Name != device.Name || old.Kind != device.Kind
                        || now - old.Timestamp > TimeSpan.FromMinutes(5))
                    {
                        _cache[device.Id] = new CachedReading { Name = device.Name, Kind = device.Kind, Level = level, Timestamp = device.LastUpdated ?? now };
                        _cacheDirty = true;
                    }

                    result.Add(device);
                }
                else if (_cache.TryGetValue(device.Id, out CachedReading? cached))
                {
                    // Sin lectura ahora (p. ej. desconectado): se muestra el último valor conocido.
                    result.Add(device with { Level = cached.Level, IsStale = true, LastUpdated = cached.Timestamp, IsCharging = false });
                }

                // Dispositivos que nunca han informado de batería no se muestran
                // (p. ej. un móvil emparejado); sí aparecen en el diagnóstico.
            }

            result.Sort(CompareForDisplay);
            changed = result.Count != _devices.Count || !result.Zip(_devices).All(p => SameForDisplay(p.First, p.Second));
            _devices = result;
        }

        if (changed)
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Si dos proveedores informan del mismo dispositivo, gana la lectura más útil.</summary>
    private static BatteryDevice Prefer(BatteryDevice a, BatteryDevice b)
    {
        if (a.Level.HasValue != b.Level.HasValue)
        {
            return a.Level.HasValue ? a : b;
        }

        if (a.IsConnected != b.IsConnected)
        {
            return a.IsConnected ? a : b;
        }

        return (b.LastUpdated ?? DateTimeOffset.MinValue) > (a.LastUpdated ?? DateTimeOffset.MinValue) ? b : a;
    }

    /// <summary>Igualdad sin contar la hora de la lectura (que cambia en cada sondeo).</summary>
    private static bool SameForDisplay(BatteryDevice a, BatteryDevice b) =>
        a with { LastUpdated = null } == b with { LastUpdated = null };

    internal static int CompareForDisplay(BatteryDevice x, BatteryDevice y)
    {
        int c = y.IsConnected.CompareTo(x.IsConnected);
        if (c != 0)
        {
            return c;
        }

        // La batería del propio equipo, al final.
        c = (x.Kind == DeviceKind.Computer).CompareTo(y.Kind == DeviceKind.Computer);
        if (c != 0)
        {
            return c;
        }

        c = string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
        return c != 0 ? c : string.CompareOrdinal(x.Id, y.Id);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IBatteryProvider provider in _providers)
        {
            provider.Changed -= OnProviderChanged;
            try
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Error al cerrar {provider.Name}", ex);
            }
        }
    }
}
