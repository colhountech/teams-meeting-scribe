namespace MeetingScribe.Configuration;

/// <summary>User-editable settings, persisted as JSON in %APPDATA%\MeetingScribe\config.json.</summary>
public sealed class AppConfig
{
    public VaultConfig Vault { get; set; } = new();
    public DetectionConfig Detection { get; set; } = new();
    public RecordingConfig Recording { get; set; } = new();
    public TranscriptionConfig Transcription { get; set; } = new();
    public NotesConfig Notes { get; set; } = new();

    /// <summary>Show a tray balloon when recording starts/stops and when a note is written.</summary>
    public bool ShowNotifications { get; set; } = true;
}

public sealed class VaultConfig
{
    /// <summary>Absolute path to the Obsidian vault root.</summary>
    public string Root { get; set; } = "";

    /// <summary>
    /// Path of the note relative to <see cref="Root"/>. Supported tokens:
    /// {date:FORMAT}, {time:FORMAT}, {title}.
    /// </summary>
    public string NotePathTemplate { get; set; } = @"Meetings\{date:yyyy-MM}\{date:yyyy-MM-dd} {title}.md";

    /// <summary>Append a new section when the target note already exists (otherwise a suffix is added).</summary>
    public bool AppendToExistingNote { get; set; } = true;
}

public sealed class DetectionConfig
{
    /// <summary>Regex matched against process names that count as "a Teams call".</summary>
    public string ProcessNamePattern { get; set; } = "^(ms-teams|Teams|Teams1)$";

    /// <summary>Seconds between detection polls.</summary>
    public int PollSeconds { get; set; } = 2;

    /// <summary>Consecutive positive polls required before recording starts (debounce).</summary>
    public int StartAfterConsecutiveHits { get; set; } = 2;

    /// <summary>Consecutive negative polls required before recording stops (debounce).</summary>
    public int StopAfterConsecutiveMisses { get; set; } = 6;

    /// <summary>Detect via the WASAPI capture-endpoint audio session list.</summary>
    public bool UseAudioSessions { get; set; } = true;

    /// <summary>Detect via the Windows microphone privacy "in use" registry markers.</summary>
    public bool UseMicrophoneConsentRegistry { get; set; } = true;
}

public sealed class RecordingConfig
{
    /// <summary>Where WAV files are written. Empty = %APPDATA%\MeetingScribe\recordings.</summary>
    public string OutputDirectory { get; set; } = "";

    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordMicrophone { get; set; } = true;

    /// <summary>Keep the WAV files after a note has been written.</summary>
    public bool KeepAudioFiles { get; set; } = false;

    /// <summary>Recordings shorter than this are discarded (filters out accidental blips).</summary>
    public int MinimumMeetingSeconds { get; set; } = 60;

    /// <summary>Safety valve so a stuck detector cannot fill the disk.</summary>
    public double MaxMeetingHours { get; set; } = 6;

    /// <summary>Substring of the playback device name to capture. Empty = default device.</summary>
    public string SystemAudioDeviceName { get; set; } = "";

    /// <summary>Substring of the microphone device name to capture. Empty = default comms device.</summary>
    public string MicrophoneDeviceName { get; set; } = "";
}

public sealed class TranscriptionConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Whisper ggml model: Tiny, TinyEn, Base, BaseEn, Small, SmallEn, Medium, MediumEn, LargeV3, LargeV3Turbo.</summary>
    public string Model { get; set; } = "SmallEn";

    /// <summary>Language code, or "auto" to auto-detect.</summary>
    public string Language { get; set; } = "en";

    /// <summary>0 = use half the logical processors.</summary>
    public int Threads { get; set; } = 0;

    public string SelfSpeakerLabel { get; set; } = "Me";
    public string OthersSpeakerLabel { get; set; } = "Participants";

    /// <summary>Consecutive segments from the same speaker closer than this are merged into one paragraph.</summary>
    public double MergeGapSeconds { get; set; } = 4;

    /// <summary>Segments whose loudest 100 ms window is quieter than this RMS are dropped as silence hallucinations.</summary>
    public double SilenceRmsThreshold { get; set; } = 0.004;

    /// <summary>Segments whose average token probability is below this are dropped.</summary>
    public double MinimumProbability { get; set; } = 0.25;
}

public sealed class NotesConfig
{
    public bool IncludeFrontmatter { get; set; } = true;
    public bool IncludeTimestamps { get; set; } = true;
    public string[] Tags { get; set; } = ["meeting", "teams", "auto-transcribed"];

    /// <summary>Extra text inserted directly under the heading (e.g. a Dataview or callout snippet).</summary>
    public string HeaderSnippet { get; set; } = "";
}
