namespace BatteryTray.Core.Settings;

/// <summary>Cómo se reparten los iconos en la barra de tareas.</summary>
public enum TrayMode
{
    /// <summary>Un icono por cada dispositivo conectado.</summary>
    PerDevice,

    /// <summary>Un solo icono con el nivel más bajo.</summary>
    Single,
}

/// <summary>Qué se dibuja en cada icono.</summary>
public enum IconStyle
{
    /// <summary>El porcentaje en número.</summary>
    Percentage,

    /// <summary>El icono del tipo de dispositivo con una barra de nivel.</summary>
    DeviceIcon,
}

/// <summary>Ajustes del usuario. Se guardan en JSON en %LOCALAPPDATA%\BatteryTray.</summary>
public sealed class AppSettings
{
    public const int MinThreshold = 5;
    public const int MaxThreshold = 50;
    public const int MaxListEntries = 200;
    public static readonly string[] SupportedLanguages = ["auto", "es", "en"];

    public int SchemaVersion { get; set; } = 1;

    /// <summary>"auto", "es" o "en".</summary>
    public string Language { get; set; } = "auto";

    public TrayMode TrayMode { get; set; } = TrayMode.PerDevice;

    public IconStyle IconStyle { get; set; } = IconStyle.Percentage;

    public bool NotifyLowBattery { get; set; } = true;

    public int LowBatteryThreshold { get; set; } = 20;

    public bool NotifyConnectionChanges { get; set; }

    public bool DemoMode { get; set; }

    /// <summary>True cuando ya se ha mostrado la bienvenida (y la pregunta de iniciar con Windows).</summary>
    public bool WelcomeDismissed { get; set; }

    /// <summary>Dispositivos que el usuario ha ocultado por completo.</summary>
    public List<string> HiddenDevices { get; set; } = [];

    /// <summary>
    /// Preferencia por dispositivo de tener icono propio en la barra (modo "uno por dispositivo").
    /// Si no hay entrada se usa el valor por defecto (sí, salvo la batería del propio equipo,
    /// que Windows ya muestra).
    /// </summary>
    public Dictionary<string, bool> TrayIconOverrides { get; set; } = [];

    /// <summary>Nombres personalizados por dispositivo.</summary>
    public Dictionary<string, string> Aliases { get; set; } = [];

    public bool IsHidden(string deviceId) => HiddenDevices.Contains(deviceId, StringComparer.Ordinal);

    public bool HasTrayIcon(BatteryDevice device) =>
        TrayIconOverrides.TryGetValue(device.Id, out bool value) ? value : device.Kind != DeviceKind.Computer;

    public string DisplayName(BatteryDevice device) =>
        Aliases.TryGetValue(device.Id, out string? alias) && !string.IsNullOrWhiteSpace(alias) ? alias : device.Name;

    /// <summary>
    /// Valida y corrige valores tras leerlos del disco. El archivo de ajustes lo puede
    /// editar cualquiera a mano, así que se trata como entrada no confiable: rangos
    /// acotados, valores por lista permitida, tamaños limitados.
    /// </summary>
    public void Normalize()
    {
        SchemaVersion = 1;

        if (!SupportedLanguages.Contains(Language, StringComparer.Ordinal))
        {
            Language = "auto";
        }

        if (!Enum.IsDefined(TrayMode))
        {
            TrayMode = TrayMode.PerDevice;
        }

        if (!Enum.IsDefined(IconStyle))
        {
            IconStyle = IconStyle.Percentage;
        }

        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, MinThreshold, MaxThreshold);

        HiddenDevices = (HiddenDevices ?? [])
            .Where(IsValidDeviceId)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxListEntries)
            .ToList();

        TrayIconOverrides = (TrayIconOverrides ?? [])
            .Where(kv => IsValidDeviceId(kv.Key))
            .Take(MaxListEntries)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        Aliases = (Aliases ?? [])
            .Where(kv => IsValidDeviceId(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .Take(MaxListEntries)
            .ToDictionary(kv => kv.Key, kv => DeviceText.Sanitize(kv.Value, kv.Value.Trim()), StringComparer.Ordinal);
    }

    /// <summary>Los ids son cortos y solo usan caracteres seguros (lista permitida).</summary>
    public static bool IsValidDeviceId(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= 64
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_' or '.');
}
