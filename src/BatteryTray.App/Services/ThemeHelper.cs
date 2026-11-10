using Microsoft.Win32;

namespace BatteryTray.Services;

internal static class ThemeHelper
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// La barra de tareas sigue el modo de Windows ("SystemUsesLightTheme"), que puede ser
    /// distinto del modo de las apps. Por defecto en Windows 11 es oscuro.
    /// </summary>
    public static bool TaskbarUsesLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
