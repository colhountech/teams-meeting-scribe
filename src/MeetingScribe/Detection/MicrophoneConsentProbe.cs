using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using MeetingScribe.Infrastructure;
using Microsoft.Win32;

namespace MeetingScribe.Detection;

/// <summary>
/// Detects an active call using the Windows privacy "microphone in use" markers.
/// Windows writes LastUsedTimeStart/LastUsedTimeStop per app; a Stop value of 0 means
/// the microphone is in use right now. This works for both packaged (new Teams) and
/// classic desktop apps, and needs no elevation.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MicrophoneConsentProbe
{
    private const string ConsentStorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private readonly Regex _appPattern;

    public MicrophoneConsentProbe(string processNamePattern)
    {
        // The consent store keys are package family names or encoded exe paths rather than
        // bare process names, so match loosely on the same identifier.
        var loose = processNamePattern.Replace("^", "").Replace("$", "");
        _appPattern = new Regex(loose, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public bool IsCallActive()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentStorePath);
            if (root is null) return false;

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                if (subKey is null) continue;

                if (subKeyName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var exeKeyName in subKey.GetSubKeyNames())
                    {
                        using var exeKey = subKey.OpenSubKey(exeKeyName);
                        // NonPackaged keys encode the full path with '#' instead of '\'.
                        if (exeKey is not null && IsInUse(exeKey, exeKeyName.Replace('#', '\\'))) return true;
                    }
                }
                else if (IsInUse(subKey, subKeyName))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Microphone consent probe failed: {ex.Message}");
        }

        return false;
    }

    private bool IsInUse(RegistryKey key, string identifier)
    {
        if (!_appPattern.IsMatch(identifier)) return false;

        // A zero stop time means "still in use"; a missing value means it was never used.
        if (key.GetValue("LastUsedTimeStop") is not long stop) return false;
        if (stop != 0) return false;

        return key.GetValue("LastUsedTimeStart") is long start && start > 0;
    }
}
