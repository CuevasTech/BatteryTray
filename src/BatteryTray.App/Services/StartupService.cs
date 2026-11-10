using BatteryTray.Core;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace BatteryTray.Services;

internal enum StartupState
{
    Disabled,
    Enabled,

    /// <summary>El usuario lo desactivó en el Administrador de tareas o en Configuración.</summary>
    DisabledByUser,

    /// <summary>Lo controla una directiva de la organización.</summary>
    ControlledByPolicy,
}

/// <summary>
/// Iniciar con Windows.
/// - Versión Store: tarea de inicio del paquete MSIX (Windows la gestiona y el usuario la ve en Configuración).
/// - Versión portable: valor en HKCU\...\Run (solo el usuario actual, sin permisos de administrador),
///   con la ruta del ejecutable entre comillas para evitar ambigüedades con espacios.
/// </summary>
internal sealed class StartupService
{
    public const string StartupArgument = "--startup";
    private const string TaskId = "BatteryTrayStartup";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "BatteryTray";

    public async Task<StartupState> GetStateAsync()
    {
        try
        {
            if (AppInfo.IsPackaged)
            {
                StartupTask task = await StartupTask.GetAsync(TaskId);
                return Map(task.State);
            }

            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (run?.GetValue(ValueName) is not string)
            {
                return StartupState.Disabled;
            }

            return IsDisabledInTaskManager() ? StartupState.DisabledByUser : StartupState.Enabled;
        }
        catch (Exception ex)
        {
            AppLog.Error("No se pudo leer el inicio con Windows", ex);
            return StartupState.Disabled;
        }
    }

    public async Task<StartupState> SetEnabledAsync(bool enabled)
    {
        try
        {
            if (AppInfo.IsPackaged)
            {
                StartupTask task = await StartupTask.GetAsync(TaskId);
                if (enabled)
                {
                    return Map(await task.RequestEnableAsync());
                }

                task.Disable();
                return Map(task.State);
            }

            using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !Path.IsPathFullyQualified(exe) || !File.Exists(exe))
                {
                    AppLog.Warn("Ruta del ejecutable no válida; no se activa el inicio con Windows");
                    return StartupState.Disabled;
                }

                run.SetValue(ValueName, $"\"{exe}\" {StartupArgument}", RegistryValueKind.String);

                // Si antes se desactivó desde el Administrador de tareas, se quita esa marca
                // (es una entrada de esta misma app).
                using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
                return StartupState.Enabled;
            }

            run.DeleteValue(ValueName, throwOnMissingValue: false);
            return StartupState.Disabled;
        }
        catch (Exception ex)
        {
            AppLog.Error("No se pudo cambiar el inicio con Windows", ex);
            return await GetStateAsync();
        }
    }

    private static bool IsDisabledInTaskManager()
    {
        using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
        // El primer byte par (02) = activado; impar (03) = desactivado por el usuario.
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
    }

    private static StartupState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => StartupState.Enabled,
        StartupTaskState.DisabledByUser => StartupState.DisabledByUser,
        StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => StartupState.ControlledByPolicy,
        _ => StartupState.Disabled,
    };
}
