using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingScribe.Infrastructure;

namespace MeetingScribe.Configuration;

/// <summary>Loads and saves <see cref="AppConfig"/>, creating a sensible default on first run.</summary>
internal static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load()
    {
        Paths.EnsureCreated();

        if (!File.Exists(Paths.ConfigFile))
        {
            var created = CreateDefault();
            Save(created);
            Log.Info($"Created default config at {Paths.ConfigFile}");
            return created;
        }

        try
        {
            var json = File.ReadAllText(Paths.ConfigFile);
            var config = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? CreateDefault();
            // Re-save so newly added settings appear in the file for discoverability.
            Save(config);
            return config;
        }
        catch (Exception ex)
        {
            Log.Error("Config could not be read; falling back to defaults", ex);
            return CreateDefault();
        }
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Paths.EnsureCreated();
            File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(config, Options));
        }
        catch (Exception ex)
        {
            Log.Error("Config could not be saved", ex);
        }
    }

    private static AppConfig CreateDefault()
    {
        var config = new AppConfig();
        config.Vault.Root = GuessVaultRoot() ?? "";
        return config;
    }

    /// <summary>Best-effort discovery of an Obsidian vault (a folder containing a .obsidian directory).</summary>
    private static string? GuessVaultRoot()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string> { userProfile };

        try
        {
            roots.AddRange(Directory.EnumerateDirectories(userProfile, "OneDrive*"));
        }
        catch
        {
            // Ignore an unreadable profile folder.
        }

        foreach (var root in roots)
        {
            var hit = FindVault(root, depth: 3);
            if (hit is not null) return hit;
        }

        return null;
    }

    private static string? FindVault(string directory, int depth)
    {
        if (depth < 0) return null;

        try
        {
            if (Directory.Exists(Path.Combine(directory, ".obsidian"))) return directory;

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name is "AppData" or "node_modules") continue;

                var hit = FindVault(child, depth - 1);
                if (hit is not null) return hit;
            }
        }
        catch
        {
            // Unreadable directories are simply skipped.
        }

        return null;
    }
}
