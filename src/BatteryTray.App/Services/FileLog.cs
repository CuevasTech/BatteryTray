using System.Globalization;
using System.Text;

namespace BatteryTray.Services;

/// <summary>
/// Registro en archivo de texto, con rotación al llegar a 1 MB (se guarda una copia .old).
/// Solo se escribe en la carpeta de datos de la app.
/// </summary>
internal sealed class FileLog
{
    private const long MaxBytes = 1024 * 1024;
    private readonly string _path;
    private readonly Lock _gate = new();

    public FileLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "batterytray.log");
    }

    public string FilePath => _path;

    public void Write(string level, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(_path, _path + ".old", overwrite: true);
                }

                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sin registro; no es motivo para fallar.
            }
        }
    }
}
