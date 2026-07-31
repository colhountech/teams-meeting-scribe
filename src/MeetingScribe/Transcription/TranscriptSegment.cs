namespace MeetingScribe.Transcription;

/// <summary>One utterance from one track.</summary>
internal sealed record TranscriptSegment(string Speaker, TimeSpan Start, TimeSpan End, string Text);
