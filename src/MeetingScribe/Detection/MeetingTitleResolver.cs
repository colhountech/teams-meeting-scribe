using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MeetingScribe.Infrastructure;

namespace MeetingScribe.Detection;

/// <summary>Best-effort meeting title, read from Teams' visible top-level window captions.</summary>
internal static partial class MeetingTitleResolver
{
    private static readonly string[] ShellTabs =
    [
        "Chat", "Calendar", "Teams", "Activity", "Calls", "Files", "Apps",
        "Microsoft Teams", "Notifications", "Search",
    ];

    public static string Resolve(string processNamePattern, string fallback = "Teams Meeting")
    {
        try
        {
            var pattern = new Regex(processNamePattern, RegexOptions.IgnoreCase);
            var pids = CollectProcessIds(pattern);

            if (pids.Count == 0) return fallback;

            var candidates = EnumerateWindowTitles(pids);
            foreach (var title in candidates)
            {
                var cleaned = Clean(title);
                if (cleaned is not null) return cleaned;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not resolve meeting title: {ex.Message}");
        }

        return fallback;
    }

    private static HashSet<int> CollectProcessIds(Regex pattern)
    {
        var pids = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (pattern.IsMatch(process.ProcessName)) pids.Add(process.Id);
                }
                catch
                {
                    // The process exited while we were enumerating.
                }
            }
        }

        return pids;
    }

    private static string? Clean(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Teams captions look like "Weekly sync | Microsoft Teams".
        var name = title.Split('|')[0].Trim();
        if (name.Length == 0) return null;
        if (ShellTabs.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        if (name.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase)) return null;

        // Strip Teams' unread-count and notification prefixes, e.g. "(3) Weekly sync".
        name = UnreadPrefix().Replace(name, "").Trim();
        return name.Length == 0 ? null : name;
    }

    private static List<string> EnumerateWindowTitles(HashSet<int> processIds)
    {
        var titles = new List<string>();

        EnumWindows((hWnd, _unused) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            if (!processIds.Contains((int)pid)) return true;

            var length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            var buffer = new StringBuilder(length + 1);
            GetWindowText(hWnd, buffer, buffer.Capacity);
            titles.Add(buffer.ToString());
            return true;
        }, IntPtr.Zero);

        return titles;
    }

    [GeneratedRegex(@"^\(\d+\)\s*")]
    private static partial Regex UnreadPrefix();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
}
