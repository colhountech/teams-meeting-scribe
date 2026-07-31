namespace MeetingScribe.Recording;

/// <summary>The audio captured for one meeting.</summary>
internal sealed record RecordingResult(
    string Title,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string? SystemAudioPath,
    string? MicrophonePath)
{
    /// <summary>Set for files the user imported by hand: never delete them, never length-filter them.</summary>
    public bool IsManualImport { get; init; }

    public TimeSpan Duration => EndedAt - StartedAt;

    public bool HasAudio => SystemAudioPath is not null || MicrophonePath is not null;

    public IEnumerable<string> AudioFiles
    {
        get
        {
            if (SystemAudioPath is not null) yield return SystemAudioPath;
            if (MicrophonePath is not null) yield return MicrophonePath;
        }
    }
}
