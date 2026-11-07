using System.Text;

namespace BgCompass.App.Runtime;

/// <summary>Tiny file log (app.log). Silent when not initialised, never throws.</summary>
public static class AppLog
{
    const long MaxBytes = 1_000_000;
    static readonly object Gate = new();
    static string? _path;

    public static void Init(string path)
    {
        lock (Gate)
        {
            _path = path;
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".1", true);
            }
            catch (IOException) { }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? e = null) => Write("ERROR", e is null ? message : $"{message}: {e}");

    static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_path is null) return;
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
