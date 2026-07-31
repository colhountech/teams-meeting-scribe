using System.Runtime.Versioning;
using MeetingScribe.Configuration;
using MeetingScribe.Infrastructure;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingScribe.Recording;

/// <summary>
/// Records the meeting as two independent WAV tracks: system output (everyone else) and
/// the microphone (you). Keeping them separate gives speaker attribution without a
/// diarization model.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MeetingRecorder : IDisposable
{
    private readonly RecordingConfig _config;

    private WasapiLoopbackCapture? _systemCapture;
    private WasapiCapture? _micCapture;
    private WaveFileWriter? _systemWriter;
    private WaveFileWriter? _micWriter;

    // Playing silence keeps the render endpoint active so loopback capture keeps
    // delivering buffers even when nobody is speaking; without it the system track
    // drifts out of alignment with the microphone track.
    private WasapiOut? _silenceKeepAlive;

    private string? _systemPath;
    private string? _micPath;
    private DateTimeOffset _startedAt;
    private string _title = "Teams Meeting";

    public bool IsRecording { get; private set; }
    public DateTimeOffset StartedAt => _startedAt;

    public MeetingRecorder(RecordingConfig config) => _config = config;

    public string OutputDirectory =>
        string.IsNullOrWhiteSpace(_config.OutputDirectory)
            ? Paths.DefaultRecordingsDir
            : _config.OutputDirectory;

    public void Start(string title)
    {
        if (IsRecording) return;

        _title = title;
        _startedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(OutputDirectory);

        var stem = $"{_startedAt:yyyy-MM-dd_HHmmss}_{FileNames.Sanitize(title)}";

        try
        {
            if (_config.RecordSystemAudio) StartSystemCapture(stem);
            if (_config.RecordMicrophone) StartMicrophoneCapture(stem);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start recording", ex);
            SafeTeardown();
            throw;
        }

        if (_systemCapture is null && _micCapture is null)
        {
            throw new InvalidOperationException("No audio device could be opened for recording.");
        }

        IsRecording = true;
        Log.Info($"Recording started: '{title}' (system: {_systemCapture is not null}, mic: {_micCapture is not null}).");
    }

    private void StartSystemCapture(string stem)
    {
        var device = DeviceLocator.FindRender(_config.SystemAudioDeviceName);
        if (device is null)
        {
            Log.Warn("No playback device found; system audio will not be recorded.");
            return;
        }

        _systemCapture = new WasapiLoopbackCapture(device);
        _systemPath = Path.Combine(OutputDirectory, stem + "_system.wav");
        _systemWriter = new WaveFileWriter(_systemPath, _systemCapture.WaveFormat);

        _systemCapture.DataAvailable += (_, e) => WriteSafely(_systemWriter, e);
        _systemCapture.RecordingStopped += (_, e) => LogStopped("system", e);

        StartSilenceKeepAlive(device, _systemCapture.WaveFormat);
        _systemCapture.StartRecording();
    }

    private void StartMicrophoneCapture(string stem)
    {
        var device = DeviceLocator.FindCapture(_config.MicrophoneDeviceName);
        if (device is null)
        {
            Log.Warn("No microphone found; only system audio will be recorded.");
            return;
        }

        _micCapture = new WasapiCapture(device);
        _micPath = Path.Combine(OutputDirectory, stem + "_mic.wav");
        _micWriter = new WaveFileWriter(_micPath, _micCapture.WaveFormat);

        _micCapture.DataAvailable += (_, e) => WriteSafely(_micWriter, e);
        _micCapture.RecordingStopped += (_, e) => LogStopped("microphone", e);
        _micCapture.StartRecording();
    }

    private void StartSilenceKeepAlive(MMDevice device, WaveFormat format)
    {
        try
        {
            _silenceKeepAlive = new WasapiOut(device, AudioClientShareMode.Shared, false, 200);
            _silenceKeepAlive.Init(new SilenceProvider(format));
            _silenceKeepAlive.Play();
        }
        catch (Exception ex)
        {
            // Not fatal: the tracks may drift slightly during long silences.
            Log.Warn($"Could not start loopback keep-alive: {ex.Message}");
            _silenceKeepAlive?.Dispose();
            _silenceKeepAlive = null;
        }
    }

    private static void WriteSafely(WaveFileWriter? writer, WaveInEventArgs e)
    {
        if (writer is null) return;

        try
        {
            lock (writer)
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed writing audio buffer", ex);
        }
    }

    private static void LogStopped(string track, StoppedEventArgs e)
    {
        if (e.Exception is not null) Log.Error($"{track} capture stopped unexpectedly", e.Exception);
    }

    public RecordingResult? Stop()
    {
        if (!IsRecording) return null;

        IsRecording = false;
        var endedAt = DateTimeOffset.Now;

        try
        {
            _systemCapture?.StopRecording();
            _micCapture?.StopRecording();
            // WASAPI raises DataAvailable on a pool thread; give in-flight buffers a moment to land.
            Thread.Sleep(300);
        }
        catch (Exception ex)
        {
            Log.Error("Error stopping capture", ex);
        }

        FlushAndClose();
        SafeTeardown();

        var result = new RecordingResult(_title, _startedAt, endedAt, NullIfEmpty(_systemPath), NullIfEmpty(_micPath));
        _systemPath = null;
        _micPath = null;

        Log.Info($"Recording stopped after {result.Duration:hh\\:mm\\:ss}.");
        return result;
    }

    private void FlushAndClose()
    {
        foreach (var writer in new[] { _systemWriter, _micWriter })
        {
            if (writer is null) continue;

            try
            {
                lock (writer)
                {
                    writer.Flush();
                }

                writer.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Error closing WAV writer", ex);
            }
        }

        _systemWriter = null;
        _micWriter = null;
    }

    private void SafeTeardown()
    {
        try { _silenceKeepAlive?.Stop(); } catch { /* best effort */ }

        _silenceKeepAlive?.Dispose();
        _systemCapture?.Dispose();
        _micCapture?.Dispose();

        _silenceKeepAlive = null;
        _systemCapture = null;
        _micCapture = null;
    }

    private static string? NullIfEmpty(string? path) =>
        path is not null && File.Exists(path) && new FileInfo(path).Length > 1024 ? path : null;

    public void Dispose()
    {
        if (IsRecording) Stop();
        FlushAndClose();
        SafeTeardown();
    }
}
