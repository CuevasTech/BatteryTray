using System.Reflection;
using BatteryTray.Interop;

namespace BatteryTray.Services;

/// <summary>Datos básicos de la app: rutas, versión y si va empaquetada (Store) o es portable.</summary>
internal static unsafe class AppInfo
{
    public const string RepositoryUrl = "https://github.com/CuevasTech/BatteryTray";
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    private static readonly Lazy<bool> s_isPackaged = new(() =>
    {
        uint length = 0;
        return NativeMethods.GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
    });

    public static bool IsPackaged => s_isPackaged.Value;

    /// <summary>%LOCALAPPDATA%\BatteryTray (en la versión de la Store, Windows lo redirige al almacenamiento del paquete).</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryTray");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string CachePath => Path.Combine(DataDirectory, "cache.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
}
