using System.Runtime.Versioning;
using System.Text;
using MeetingScribe.Configuration;
using MeetingScribe.Detection;
using MeetingScribe.Infrastructure;
using MeetingScribe.Recording;
using MeetingScribe.Transcription;
using NAudio.CoreAudioApi;

namespace MeetingScribe.Diagnostics;

/// <summary>
/// Command-line diagnostics. These exist so the audio and Whisper plumbing can be verified
/// without sitting through a real meeting.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SelfTest
{
    private static readonly StringBuilder Report = new();

    public static string ReportPath { get; } =
        Path.Combine(Path.GetTempPath(), "meetingscribe-selftest.txt");

    public static async Task<int> RunAsync(string[] args)
    {
        var exitCode = 0;

        try
        {
            if (args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
            {
                WriteVersion();
                return 0;
            }

            if (args[0] is "--help" or "--?")
            {
                WriteUsage();
                return 0;
            }

            var config = ConfigStore.Load();

            if (args[0].Equals("--transcribe", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    Write("Usage: MeetingScribe.exe --transcribe <audio-file>");
                    exitCode = 2;
                }
                else
                {
                    await TranscribeAsync(config, args[1]).ConfigureAwait(false);
                }
            }
            else
            {
                var seconds = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 6;
                await RunSelfTestAsync(config, seconds).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Write($"FAILED: {ex}");
            exitCode = 1;
        }

        File.WriteAllText(ReportPath, Report.ToString(), Encoding.UTF8);
        Write($"(report written to {ReportPath})");
        return exitCode;
    }

    private static async Task RunSelfTestAsync(AppConfig config, int seconds)
    {
        Section("Configuration");
        Write($"Config file      : {Paths.ConfigFile}");
        Write($"Vault root       : '{config.Vault.Root}' (exists: {Directory.Exists(config.Vault.Root)})");
        Write($"Note template    : {config.Vault.NotePathTemplate}");
        Write($"Whisper model    : {config.Transcription.Model}");

        Section("Audio devices");
        Write("Playback:");
        foreach (var name in DeviceLocator.ListDevices(DataFlow.Render)) Write($"  - {name}");
        Write("Capture:");
        foreach (var name in DeviceLocator.ListDevices(DataFlow.Capture)) Write($"  - {name}");

        Write($"Selected playback: {Describe(DeviceLocator.FindRender(config.Recording.SystemAudioDeviceName))}");
        Write($"Selected mic     : {Describe(DeviceLocator.FindCapture(config.Recording.MicrophoneDeviceName))}");

        Section("Call detection");
        using var audioProbe = new AudioSessionProbe(config.Detection.ProcessNamePattern);
        var registryProbe = new MicrophoneConsentProbe(config.Detection.ProcessNamePattern);
        Write($"Pattern              : {config.Detection.ProcessNamePattern}");
        Write($"Audio-session probe  : {audioProbe.IsCallActive()}");
        Write($"Mic-consent probe    : {registryProbe.IsCallActive()}");
        Write($"Resolved title       : {MeetingTitleResolver.Resolve(config.Detection.ProcessNamePattern, config.Detection.IgnoredWindowTitlePattern)}");

        Section($"Recording {seconds}s");
        var recordingConfig = new RecordingConfig
        {
            OutputDirectory = Path.Combine(Path.GetTempPath(), "meetingscribe-selftest"),
            KeepAudioFiles = true,
            MinimumMeetingSeconds = 0,
        };

        using var recorder = new MeetingRecorder(recordingConfig);
        recorder.Start("SelfTest");

        // While we hold the microphone open, both probes should see *this* process.
        // That proves the detection plumbing works without needing a live Teams call.
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        using (var selfAudioProbe = new AudioSessionProbe("MeetingScribe"))
        {
            Write($"Probe self-check (audio session): {selfAudioProbe.IsCallActive()}");
        }

        Write($"Probe self-check (mic consent)  : {new MicrophoneConsentProbe("MeetingScribe").IsCallActive()}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds - 2))).ConfigureAwait(false);
        var result = recorder.Stop();

        if (result is null)
        {
            Write("No recording produced.");
            return;
        }

        Write($"Duration: {result.Duration.TotalSeconds:0.0}s");
        ReportTrack("System", result.SystemAudioPath);
        ReportTrack("Microphone", result.MicrophonePath);

        Section("Conversion");
        foreach (var file in result.AudioFiles)
        {
            var target = Path.ChangeExtension(file, ".16k.wav");
            var converted = AudioConverter.Convert(file, target);
            Write(converted is null
                ? $"{Path.GetFileName(file)} -> conversion FAILED"
                : $"{Path.GetFileName(file)} -> {converted.Duration.TotalSeconds:0.0}s, " +
                  $"{converted.Envelope.Length} windows, peak RMS {converted.Envelope.DefaultIfEmpty(0).Max():0.0000}");
        }

        Write("");
        Write("Self-test complete. Speak during the recording window to verify the mic track has signal.");
    }

    private static async Task TranscribeAsync(AppConfig config, string path)
    {
        Section($"Transcribing {path}");
        if (!File.Exists(path))
        {
            Write("File not found.");
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), $"ms-selftest-{Guid.NewGuid():N}.wav");
        var converted = AudioConverter.Convert(path, temp);
        if (converted is null)
        {
            Write("Conversion failed.");
            return;
        }

        Write($"Converted: {converted.Duration.TotalSeconds:0.0}s @16kHz mono, " +
              $"peak RMS {converted.Envelope.DefaultIfEmpty(0).Max():0.0000}");

        using var transcriber = new WhisperTranscriber(config.Transcription);
        var progress = new Progress<string>(Write);
        var started = DateTime.UtcNow;

        var segments = await transcriber
            .TranscribeAsync(converted, "Speaker", progress, CancellationToken.None)
            .ConfigureAwait(false);

        Write($"Model '{config.Transcription.Model}' produced {segments.Count} segment(s) " +
              $"in {(DateTime.UtcNow - started).TotalSeconds:0.0}s.");

        foreach (var segment in segments)
        {
            Write($"  [{segment.Start:hh\\:mm\\:ss}-{segment.End:hh\\:mm\\:ss}] {segment.Text}");
        }

        WriteSampleNote(config, path, converted.Duration, segments);

        try
        {
            File.Delete(temp);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }

    /// <summary>Renders the note into a throwaway vault so formatting can be checked safely.</summary>
    private static void WriteSampleNote(
        AppConfig config,
        string sourcePath,
        TimeSpan duration,
        IReadOnlyList<TranscriptSegment> segments)
    {
        Section("Note preview");

        var sandbox = Path.Combine(Path.GetTempPath(), "meetingscribe-selftest", "vault");
        Directory.CreateDirectory(sandbox);

        var preview = new AppConfig
        {
            Vault = new VaultConfig { Root = sandbox, NotePathTemplate = config.Vault.NotePathTemplate },
            Notes = config.Notes,
            Recording = config.Recording,
            Transcription = config.Transcription,
        };

        var startedAt = DateTimeOffset.Now - duration;
        var recording = new RecordingResult(
            Path.GetFileNameWithoutExtension(sourcePath),
            startedAt,
            startedAt + duration,
            sourcePath,
            null) { IsManualImport = true };

        var notePath = new Notes.NoteWriter(preview).Write(recording, segments);
        Write($"Written to {notePath}");
        Write("");
        Write(File.ReadAllText(notePath));
    }

    private static void ReportTrack(string label, string? path)
    {
        if (path is null)
        {
            Write($"{label}: not captured");
            return;
        }

        Write($"{label}: {new FileInfo(path).Length / 1024.0:0} KB — {path}");
    }

    private static string Describe(MMDevice? device)
    {
        if (device is null) return "<none>";

        using (device)
        {
            return device.FriendlyName;
        }
    }

    private static void Section(string title)
    {
        Write("");
        Write($"=== {title} ===");
    }

    /// <summary>
    /// Prints build identity without touching audio devices or config, so it doubles as a
    /// cheap "does this executable actually run here?" check for the build script.
    /// </summary>
    private static void WriteVersion()
    {
        var assembly = typeof(SelfTest).Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "unknown";
        var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;

        Console.WriteLine($"MeetingScribe {version} ({runtime}, {arch})");
    }

    private static void WriteUsage()
    {
        Console.WriteLine("MeetingScribe — records Teams calls and files transcripts in Obsidian.");
        Console.WriteLine();
        Console.WriteLine("  MeetingScribe.exe                    Start in the system tray (normal use).");
        Console.WriteLine("  MeetingScribe.exe --version          Print version and exit.");
        Console.WriteLine("  MeetingScribe.exe --selftest [secs]  Record, transcribe and write a sample note.");
        Console.WriteLine("  MeetingScribe.exe --transcribe <f>   Transcribe an existing audio file.");
        Console.WriteLine();
        Console.WriteLine(@"Config and logs live in %APPDATA%\MeetingScribe.");
    }

    private static void Write(string line)
    {
        Report.AppendLine(line);
        Console.WriteLine(line);
    }
}
