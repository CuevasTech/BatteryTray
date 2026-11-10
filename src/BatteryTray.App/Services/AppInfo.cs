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

    /// <summary>
    /// Carpeta de datos (ajustes, caché y registros).
    /// - Versión de la Store: la carpeta local del paquete (…\Packages\&lt;app&gt;\LocalState). Es una ruta
    ///   real (se puede abrir en el Explorador) y Windows la borra al desinstalar la app.
    /// - Sin paquete (desarrollo): %LOCALAPPDATA%\BatteryTray.
    /// </summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        if (IsPackaged)
        {
            try
            {
                return global::Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            }
            catch (Exception)
            {
                // Si no estuviera disponible, se usa la ruta de siempre.
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryTray");
    }

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string CachePath => Path.Combine(DataDirectory, "cache.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
}
