namespace MeetingScribe.Pipeline;

internal enum AppState
{
    Idle,
    Recording,
    Transcribing,
    Paused,
    Error,
}
