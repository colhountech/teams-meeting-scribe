using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MeetingScribe.Infrastructure;

/// <summary>Adds or removes the per-user "run at sign-in" registration.</summary>
[SupportedOSPlatform("windows")]
internal static class StartupRegistration
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MeetingScribe";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read startup registration: {ex.Message}");
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var path = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (path is null)
                {
                    Log.Warn("Could not determine the executable path for startup registration.");
                    return;
                }

                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            Log.Info($"Start with Windows: {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not update startup registration", ex);
        }
    }
}
