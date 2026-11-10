namespace BatteryTray.Core;

/// <summary>
/// Una fuente de lecturas de batería (Bluetooth, batería del equipo, simulador...).
/// Cada proveedor es independiente: si uno falla, los demás siguen funcionando.
/// </summary>
public interface IBatteryProvider : IAsyncDisposable
{
    /// <summary>Nombre corto para diagnóstico.</summary>
    string Name { get; }

    /// <summary>Dispositivos actuales. Debe ser seguro leerlo desde cualquier hilo.</summary>
    IReadOnlyList<BatteryDevice> Devices { get; }

    /// <summary>Se lanza (desde cualquier hilo) cuando cambia <see cref="Devices"/>.</summary>
    event EventHandler? Changed;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
}
