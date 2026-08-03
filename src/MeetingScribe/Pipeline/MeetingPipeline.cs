using System.Runtime.Versioning;
using System.Threading.Channels;
using MeetingScribe.Configuration;
using MeetingScribe.Detection;
using MeetingScribe.Infrastructure;
using MeetingScribe.Notes;
using MeetingScribe.Recording;
using MeetingScribe.Transcription;

namespace MeetingScribe.Pipeline;

/// <summary>
/// Owns the record → transcribe → write-note flow. Recording happens inline; transcription
/// runs on a single background worker so that back-to-back meetings queue rather than
/// competing for the CPU.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MeetingPipeline : IDisposable
{
    private readonly AppConfig _config;
    private readonly MeetingRecorder _recorder;
    private readonly WhisperTranscriber _transcriber;
    private readonly NoteWriter _noteWriter;
    private readonly SynchronizationContext _syncContext;
    private readonly Channel<RecordingResult> _queue = Channel.CreateUnbounded<RecordingResult>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    private System.Threading.Timer? _maxDurationTimer;
    private int _pendingJobs;

    public event Action<AppState, string>? StateChanged;
    public event Action<string>? NoteWritten;
    public event Action<string>? Failed;

    /// <summary>Raised when the pipeline stops recording on its own (safety cut-off).</summary>
    public event Action? AutoStopped;

    public bool IsRecording => _recorder.IsRecording;
    public string CurrentTitle { get; private set; } = "";

    public MeetingPipeline(AppConfig config)
    {
        _config = config;
        _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _recorder = new MeetingRecorder(config.Recording);
        _transcriber = new WhisperTranscriber(config.Transcription);
        _noteWriter = new NoteWriter(config);
        _worker = Task.Run(() => ProcessQueueAsync(_cts.Token));
    }

    public string RecordingsDirectory => _recorder.OutputDirectory;

    public void StartRecording(string? title = null)
    {
        if (_recorder.IsRecording) return;

        var resolved = title ?? MeetingTitleResolver.Resolve(
            _config.Detection.ProcessNamePattern,
            _config.Detection.IgnoredWindowTitlePattern);
        CurrentTitle = resolved;

        try
        {
            _recorder.Start(resolved);
            StartMaxDurationTimer();
            Report(AppState.Recording, $"Recording “{resolved}”");
        }
        catch (Exception ex)
        {
            Log.Error("Could not start recording", ex);
            Report(AppState.Error, "Recording failed to start");
            Post(() => Failed?.Invoke($"Recording failed to start: {ex.Message}"));
        }
    }

    public void StopRecording()
    {
        StopMaxDurationTimer();
        if (!_recorder.IsRecording) return;

        RecordingResult? result = null;
        try
        {
            result = _recorder.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("Could not stop recording cleanly", ex);
        }

        CurrentTitle = "";

        if (result is null || !result.HasAudio)
        {
            Log.Warn("Recording produced no usable audio; nothing to transcribe.");
            ReportIdle();
            return;
        }

        if (result.Duration.TotalSeconds < _config.Recording.MinimumMeetingSeconds)        {
            Log.Info($"Discarding {result.Duration.TotalSeconds:0}s recording (below minimum).");
            DeleteFiles(result.AudioFiles);
            ReportIdle();
            return;
        }

        Interlocked.Increment(ref _pendingJobs);
        _queue.Writer.TryWrite(result);
        Report(AppState.Transcribing, $"Queued “{result.Title}” for transcription");
    }

    /// <summary>Queues an existing audio file (any format NAudio can read) for transcription.</summary>
    public void EnqueueExisting(string audioPath, string? title = null)
    {
        if (!File.Exists(audioPath))
        {
            Post(() => Failed?.Invoke($"File not found: {audioPath}"));
            return;
        }

        TimeSpan duration;
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(audioPath);
            duration = reader.TotalTime;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read '{audioPath}'", ex);
            Post(() => Failed?.Invoke($"Unsupported audio file: {ex.Message}"));
            return;
        }

        var startedAt = new DateTimeOffset(File.GetLastWriteTime(audioPath)) - duration;
        var name = title ?? Path.GetFileNameWithoutExtension(audioPath);

        var recording = new RecordingResult(name, startedAt, startedAt + duration, audioPath, null)
        {
            IsManualImport = true,
        };

        Interlocked.Increment(ref _pendingJobs);
        _queue.Writer.TryWrite(recording);
        Report(AppState.Transcribing, $"Queued “{name}” for transcription");
    }

    private async Task ProcessQueueAsync(CancellationToken token)
    {
        try
        {
            await foreach (var recording in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(recording, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to process '{recording.Title}'", ex);
                    Post(() => Failed?.Invoke($"Transcription failed: {ex.Message}"));
                }
                finally
                {
                    if (Interlocked.Decrement(ref _pendingJobs) <= 0 && !_recorder.IsRecording) ReportIdle();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessAsync(RecordingResult recording, CancellationToken token)
    {
        Report(AppState.Transcribing, $"Transcribing “{recording.Title}”");
        var segments = new List<TranscriptSegment>();
        var tempFiles = new List<string>();

        if (_config.Transcription.Enabled)
        {
            var progress = new Progress<string>(message => Report(AppState.Transcribing, message));

            foreach (var (source, speaker) in EnumerateTracks(recording))
            {
                token.ThrowIfCancellationRequested();

                var tempPath = Path.Combine(Path.GetTempPath(), $"ms-{Guid.NewGuid():N}.wav");
                tempFiles.Add(tempPath);

                var converted = AudioConverter.Convert(source, tempPath);
                if (converted is null) continue;

                Report(AppState.Transcribing, $"Transcribing {speaker} track ({converted.Duration:hh\\:mm\\:ss})…");
                segments.AddRange(await _transcriber
                    .TranscribeAsync(converted, speaker, progress, token)
                    .ConfigureAwait(false));
            }
        }

        DeleteFiles(tempFiles);

        var transcript = EchoFilter.Apply(segments, _config.Transcription);
        var path = _noteWriter.Write(recording, transcript);

        if (!_config.Recording.KeepAudioFiles && !recording.IsManualImport) DeleteFiles(recording.AudioFiles);

        Post(() => NoteWritten?.Invoke(path));
    }

    private IEnumerable<(string Path, string Speaker)> EnumerateTracks(RecordingResult recording)
    {
        if (recording.MicrophonePath is not null)
        {
            yield return (recording.MicrophonePath, _config.Transcription.SelfSpeakerLabel);
        }

        if (recording.SystemAudioPath is not null)
        {
            yield return (recording.SystemAudioPath, _config.Transcription.OthersSpeakerLabel);
        }
    }

    private void StartMaxDurationTimer()
    {
        StopMaxDurationTimer();
        var limit = TimeSpan.FromHours(Math.Clamp(_config.Recording.MaxMeetingHours, 0.1, 24));

        _maxDurationTimer = new System.Threading.Timer(_ =>
        {
            Log.Warn($"Maximum recording length ({limit.TotalHours:0.#}h) reached; stopping.");
            Post(() =>
            {
                StopRecording();
                AutoStopped?.Invoke();
            });
        }, null, limit, Timeout.InfiniteTimeSpan);
    }

    private void StopMaxDurationTimer()
    {
        _maxDurationTimer?.Dispose();
        _maxDurationTimer = null;
    }

    private static void DeleteFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not delete '{file}': {ex.Message}");
            }
        }
    }

    private void ReportIdle() => Report(AppState.Idle, "Watching for Teams calls");

    private void Report(AppState state, string status) => Post(() => StateChanged?.Invoke(state, status));

    private void Post(Action action) => _syncContext.Post(_ => action(), null);

    public void Dispose()
    {
        StopMaxDurationTimer();

        // Give an in-flight meeting a chance to be saved rather than losing it.
        if (_recorder.IsRecording) StopRecording();

        _queue.Writer.TryComplete();

        try
        {
            _worker.Wait(TimeSpan.FromMinutes(2));
        }
        catch
        {
            // Shutdown is best-effort.
        }

        _cts.Cancel();
        _cts.Dispose();
        _recorder.Dispose();
        _transcriber.Dispose();
    }
}
