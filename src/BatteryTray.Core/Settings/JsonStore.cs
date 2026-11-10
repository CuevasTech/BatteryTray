using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace BatteryTray.Core.Settings;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    MaxDepth = 8)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, CachedReading>))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;

/// <summary>
/// Lectura y escritura de JSON en disco.
/// - Serialización generada en compilación (sin reflexión ni tipos polimórficos), así el
///   archivo no puede pedir instanciar tipos arbitrarios.
/// - Tamaño máximo de lectura para no cargar archivos enormes.
/// - Escritura atómica (archivo temporal + reemplazo) para no dejar un archivo a medias.
/// - Si el archivo está dañado se guarda una copia .bak y se usan valores por defecto.
/// </summary>
public static class JsonStore
{
    public const long MaxFileBytes = 1024 * 1024;

    public static AppSettings LoadSettings(string path)
    {
        AppSettings settings = Load(path, StoreJsonContext.Default.AppSettings) ?? new AppSettings();
        settings.Normalize();
        return settings;
    }

    public static void SaveSettings(string path, AppSettings settings) =>
        Save(path, settings, StoreJsonContext.Default.AppSettings);

    public static Dictionary<string, CachedReading> LoadCache(string path)
    {
        Dictionary<string, CachedReading>? raw = Load(path, StoreJsonContext.Default.DictionaryStringCachedReading);
        var result = new Dictionary<string, CachedReading>(StringComparer.Ordinal);
        if (raw is null)
        {
            return result;
        }

        foreach ((string id, CachedReading reading) in raw.Take(AppSettings.MaxListEntries))
        {
            if (AppSettings.IsValidDeviceId(id) && reading is not null && reading.Level is >= 0 and <= 100)
            {
                result[id] = reading with { Name = DeviceText.Sanitize(reading.Name, id) };
            }
        }

        return result;
    }

    public static void SaveCache(string path, IReadOnlyDictionary<string, CachedReading> cache) =>
        Save(path, cache.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            StoreJsonContext.Default.DictionaryStringCachedReading);

    private static T? Load<T>(string path, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > MaxFileBytes)
            {
                AppLog.Warn($"Archivo demasiado grande, se ignora: {info.Name} ({info.Length} bytes)");
                Backup(path);
                return null;
            }

            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Error($"No se pudo leer {Path.GetFileName(path)}; se usan valores por defecto", ex);
            Backup(path);
            return null;
        }
    }

    private static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string temp = path + ".tmp";
            using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, typeInfo);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"No se pudo guardar {Path.GetFileName(path)}", ex);
        }
    }

    private static void Backup(string path)
    {
        try
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin copia de seguridad; no es grave.
        }
    }
}
