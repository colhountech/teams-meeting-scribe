using System.Text.RegularExpressions;
using MeetingScribe.Configuration;
using MeetingScribe.Infrastructure;

namespace MeetingScribe.Transcription;

/// <summary>
/// Removes acoustic echo from the microphone transcript.
/// </summary>
/// <remarks>
/// When the meeting is played through speakers rather than a headset, the microphone re-records
/// everyone else, so the same sentence is transcribed once on the loopback track and again on the
/// microphone track. Cancelling that in the signal domain is unreliable here — the two WASAPI
/// endpoints have independent clocks, so the echo delay drifts over a long meeting — but the
/// duplicate is trivially obvious in the text: a microphone line whose words are already present
/// in the participants audio at the same moment is echo. The loopback track is the clean copy, so
/// the microphone side is what gets dropped.
/// </remarks>
internal static partial class EchoFilter
{
    public static List<TranscriptSegment> Apply(
        IReadOnlyList<TranscriptSegment> segments,
        TranscriptionConfig config)
    {
        if (!config.SuppressMicrophoneEcho) return [.. segments];

        var reference = segments
            .Where(segment => segment.Speaker == config.OthersSpeakerLabel)
            .OrderBy(segment => segment.Start)
            .ToList();

        if (reference.Count == 0) return [.. segments];

        var tolerance = TimeSpan.FromSeconds(Math.Max(0, config.EchoToleranceSeconds));
        var threshold = Math.Clamp(config.EchoSimilarityThreshold, 0, 1);

        var kept = new List<TranscriptSegment>(segments.Count);
        var dropped = 0;

        foreach (var segment in segments)
        {
            if (segment.Speaker != config.SelfSpeakerLabel || !IsEcho(segment, reference, tolerance, threshold))
            {
                kept.Add(segment);
                continue;
            }

            dropped++;
        }

        if (dropped > 0)
        {
            Log.Info($"Echo suppression removed {dropped} duplicated '{config.SelfSpeakerLabel}' segment(s).");
        }

        return kept;
    }

    private static bool IsEcho(
        TranscriptSegment segment,
        List<TranscriptSegment> reference,
        TimeSpan tolerance,
        double threshold)
    {
        var words = Tokenize(segment.Text);
        if (words.Count == 0) return false;

        // A word bag rather than a set: three "yeah"s only cancel three "yeah"s.
        var available = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var other in reference)
        {
            if (other.End + tolerance < segment.Start) continue;
            if (other.Start - tolerance > segment.End) break;

            foreach (var word in Tokenize(other.Text))
            {
                available[word] = available.GetValueOrDefault(word) + 1;
            }
        }

        if (available.Count == 0) return false;

        var matched = 0;
        foreach (var word in words)
        {
            if (!available.TryGetValue(word, out var remaining) || remaining == 0) continue;

            available[word] = remaining - 1;
            matched++;
        }

        return (double)matched / words.Count >= threshold;
    }

    private static List<string> Tokenize(string text) =>
        [.. NonWord().Split(text.ToLowerInvariant()).Where(word => word.Length > 0)];

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();
}
