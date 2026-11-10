using System.Globalization;
using BatteryTray.Core;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BatteryTray.Services;

/// <summary>
/// Textos traducidos (Strings/&lt;idioma&gt;/Resources.resw).
/// El idioma se fija al arrancar: "auto" sigue al de Windows (español si Windows está
/// en español, inglés en cualquier otro caso).
/// </summary>
internal static class Loc
{
    private static ResourceManager? s_manager;
    private static ResourceMap? s_map;
    private static ResourceContext? s_context;

    public static string LanguageTag { get; private set; } = "en-US";

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    public static void Initialize(string setting)
    {
        LanguageTag = Resolve(setting);
        Culture = CultureInfo.GetCultureInfo(LanguageTag);
        try
        {
            s_manager = new ResourceManager();
            s_context = s_manager.CreateResourceContext();
            s_context.QualifierValues["Language"] = LanguageTag;
            s_map = s_manager.MainResourceMap.GetSubtree("Resources");
        }
        catch (Exception ex)
        {
            AppLog.Error("No se pudieron cargar los textos", ex);
        }
    }

    public static string Resolve(string setting) => setting switch
    {
        "es" => "es-ES",
        "en" => "en-US",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es-ES" : "en-US",
    };

    /// <summary>Texto por clave; si falta, devuelve la clave (así se ve el fallo sin romper nada).</summary>
    public static string Get(string key)
    {
        try
        {
            return s_map?.GetValue(key, s_context)?.ValueAsString ?? key;
        }
        catch (Exception)
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(Culture, Get(key), args);

    public static string Kind(DeviceKind kind) => Get("Kind" + kind);

    /// <summary>"hace 5 min", "hace 2 h"...</summary>
    public static string Relative(DateTimeOffset? time)
    {
        if (time is not DateTimeOffset t)
        {
            return "";
        }

        TimeSpan ago = DateTimeOffset.UtcNow - t;
        if (ago < TimeSpan.FromMinutes(1))
        {
            return Get("TimeJustNow");
        }

        if (ago < TimeSpan.FromHours(1))
        {
            return Format("TimeMinutes", (int)ago.TotalMinutes);
        }

        return ago < TimeSpan.FromDays(1)
            ? Format("TimeHours", (int)ago.TotalHours)
            : Format("TimeDays", (int)ago.TotalDays);
    }
}
