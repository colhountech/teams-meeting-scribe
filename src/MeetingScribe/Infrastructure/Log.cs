using System.Text;

namespace MeetingScribe.Infrastructure;

/// <summary>Minimal thread-safe rolling file logger.</summary>
internal static class Log
{
    private static readonly Lock Gate = new();
    private const int RetentionDays = 14;

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    public static string CurrentLogFile =>
        Path.Combine(Paths.LogDir, $"meetingscribe-{DateTime.Now:yyyy-MM-dd}.log");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.LogDir);
                File.AppendAllText(CurrentLogFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
        System.Diagnostics.Debug.Write(line);
    }

    public static void PurgeOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(Paths.LogDir, "meetingscribe-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Write("WRN", $"Could not purge old logs: {ex.Message}");
        }
    }
}
