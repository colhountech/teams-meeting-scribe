using System.Diagnostics;
using System.Text.RegularExpressions;
using MeetingScribe.Infrastructure;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MeetingScribe.Detection;

/// <summary>
/// Detects an active call by asking the audio engine which processes currently hold an
/// active session on a capture (microphone) endpoint.
/// </summary>
internal sealed class AudioSessionProbe : IDisposable
{
    private readonly Regex _processPattern;
    private readonly Dictionary<int, string> _processNameCache = [];
    private MMDeviceEnumerator? _enumerator;

    public AudioSessionProbe(string processNamePattern)
    {
        _processPattern = new Regex(processNamePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public bool IsCallActive()
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();

            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    if (HasActiveMatchingSession(device)) return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Audio session probe failed: {ex.Message}");
            // Force a fresh enumerator next poll: the default device may have changed.
            _enumerator?.Dispose();
            _enumerator = null;
        }

        return false;
    }

    private bool HasActiveMatchingSession(MMDevice device)
    {
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        if (sessions is null) return false;

        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session.State != AudioSessionState.AudioSessionStateActive) continue;

            var name = ResolveProcessName((int)session.GetProcessID);
            if (name is not null && _processPattern.IsMatch(name)) return true;
        }

        return false;
    }

    private string? ResolveProcessName(int processId)
    {
        if (processId <= 0) return null;
        if (_processNameCache.TryGetValue(processId, out var cached)) return cached;

        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            _processNameCache[processId] = name;
            return name;
        }
        catch
        {
            // The process exited between enumeration and lookup.
            return null;
        }
    }

    /// <summary>Drops cached PID→name mappings so recycled PIDs cannot cause false positives.</summary>
    public void ResetCache() => _processNameCache.Clear();

    public void Dispose()
    {
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
