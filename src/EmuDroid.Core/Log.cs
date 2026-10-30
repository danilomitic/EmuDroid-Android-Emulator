namespace EmuDroid.Core;

/// <summary>Minimal file logger: one file per day in %LocalAppData%\EmuDroid\logs.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static void Init(AppPaths paths)
    {
        _dir = paths.Logs;
        Directory.CreateDirectory(_dir);
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? e = null) => Write("ERROR", e is null ? message : $"{message}\n{e}");

    private static void Write(string level, string message)
    {
        if (_dir is null) return;
        try
        {
            lock (Gate)
                File.AppendAllText(Path.Combine(_dir, $"emudroid-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss} {level} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }
}
