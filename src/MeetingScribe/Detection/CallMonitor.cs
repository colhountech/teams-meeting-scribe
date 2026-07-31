using System.Runtime.Versioning;
using MeetingScribe.Configuration;
using MeetingScribe.Infrastructure;

namespace MeetingScribe.Detection;

/// <summary>
/// Polls the detection probes on a background loop and raises debounced call start/stop events.
/// Events are marshalled back to the thread that constructed the monitor.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CallMonitor : IDisposable
{
    private readonly DetectionConfig _config;
    private readonly AudioSessionProbe? _audioProbe;
    private readonly MicrophoneConsentProbe? _registryProbe;
    private readonly SynchronizationContext _syncContext;
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private int _hits;
    private int _misses;

    public event Action? CallStarted;
    public event Action? CallEnded;

    public bool IsInCall { get; private set; }
    public bool IsPaused { get; set; }

    public CallMonitor(DetectionConfig config)
    {
        _config = config;
        _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();

        if (config.UseAudioSessions) _audioProbe = new AudioSessionProbe(config.ProcessNamePattern);
        if (config.UseMicrophoneConsentRegistry) _registryProbe = new MicrophoneConsentProbe(config.ProcessNamePattern);

        if (_audioProbe is null && _registryProbe is null)
        {
            Log.Warn("All detection probes are disabled; automatic recording will never trigger.");
        }
    }

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_config.PollSeconds, 1, 60));
        using var timer = new PeriodicTimer(interval);
        Log.Info($"Call monitor started (poll every {interval.TotalSeconds:0}s).");

        var pollCount = 0;

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (IsPaused) continue;

            try
            {
                // Recycled PIDs are rare, but clearing periodically keeps the cache honest.
                if (++pollCount % 150 == 0) _audioProbe?.ResetCache();

                var detected = Probe();
                Evaluate(detected);
            }
            catch (Exception ex)
            {
                Log.Error("Detection poll failed", ex);
            }
        }
    }

    private bool Probe()
    {
        if (_audioProbe is not null && _audioProbe.IsCallActive()) return true;
        if (_registryProbe is not null && _registryProbe.IsCallActive()) return true;
        return false;
    }

    private void Evaluate(bool detected)
    {
        if (detected)
        {
            _misses = 0;
            _hits++;

            if (!IsInCall && _hits >= Math.Max(1, _config.StartAfterConsecutiveHits))
            {
                IsInCall = true;
                Log.Info("Call detected.");
                Post(CallStarted);
            }
        }
        else
        {
            _hits = 0;
            _misses++;

            if (IsInCall && _misses >= Math.Max(1, _config.StopAfterConsecutiveMisses))
            {
                IsInCall = false;
                Log.Info("Call ended.");
                Post(CallEnded);
            }
        }
    }

    /// <summary>Lets the pipeline keep the monitor's view of the world consistent after a manual start/stop.</summary>
    public void OverrideState(bool inCall)
    {
        IsInCall = inCall;
        _hits = 0;
        _misses = 0;
    }

    private void Post(Action? handler)
    {
        if (handler is null) return;
        _syncContext.Post(_ => handler(), null);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Cancellation during shutdown is expected.
        }

        _cts.Dispose();
        _audioProbe?.Dispose();
    }
}
