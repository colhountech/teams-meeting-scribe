namespace MeetingScribe.Infrastructure;

/// <summary>Well-known filesystem locations used by the app.</summary>
internal static class Paths
{
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetingScribe");

    public static string ConfigFile => Path.Combine(AppDataDir, "config.json");
    public static string LogDir => Path.Combine(AppDataDir, "logs");
    public static string ModelDir => Path.Combine(AppDataDir, "models");
    public static string DefaultRecordingsDir => Path.Combine(AppDataDir, "recordings");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(ModelDir);
    }
}
