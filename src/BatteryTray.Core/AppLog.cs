namespace BatteryTray.Core;

/// <summary>
/// Registro mínimo. La app conecta un destino (archivo); por defecto no hace nada.
/// No registrar nunca datos sensibles: las direcciones Bluetooth se enmascaran con
/// <see cref="DeviceText.MaskAddress(ulong)"/> antes de escribirlas.
/// </summary>
public static class AppLog
{
    private static volatile Action<string, string>? s_sink;

    public static void SetSink(Action<string, string>? sink) => s_sink = sink;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            s_sink?.Invoke(level, message);
        }
        catch
        {
            // El registro nunca debe tumbar la app.
        }
    }
}
