using System.Text;
using System.Text.RegularExpressions;
using MeetingScribe.Configuration;
using MeetingScribe.Infrastructure;
using MeetingScribe.Recording;
using MeetingScribe.Transcription;

namespace MeetingScribe.Notes;

/// <summary>Renders a transcript into an Obsidian-friendly markdown note.</summary>
internal sealed partial class NoteWriter(AppConfig config)
{
    public string Write(RecordingResult recording, IReadOnlyList<TranscriptSegment> segments)
    {
        var path = ResolvePath(recording);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var exists = File.Exists(path);
        if (exists && !config.Vault.AppendToExistingNote)
        {
            path = MakeUnique(path);
            exists = false;
        }

        var body = exists
            ? BuildAppendSection(recording, segments)
            : BuildNewNote(recording, segments);

        if (exists)
        {
            File.AppendAllText(path, body, Encoding.UTF8);
        }
        else
        {
            File.WriteAllText(path, body, Encoding.UTF8);
        }

        Log.Info($"Note {(exists ? "appended to" : "written to")} {path}");
        return path;
    }

    private string ResolvePath(RecordingResult recording)
    {
        var root = config.Vault.Root;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Vault root '{root}' does not exist. Set Vault.Root in {Paths.ConfigFile}.");
        }

        var relative = SubstituteTokens(config.Vault.NotePathTemplate, recording);

        var segments = relative
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => FileNames.Sanitize(segment, 120))
            .ToArray();

        if (segments.Length == 0) segments = ["Inbox", $"{recording.StartedAt:yyyy-MM-dd} Meeting.md"];
        if (!segments[^1].EndsWith(".md", StringComparison.OrdinalIgnoreCase)) segments[^1] += ".md";

        var combined = Path.GetFullPath(Path.Combine([root, .. segments]));

        // Never let a template escape the vault.
        var rootFull = Path.GetFullPath(root);
        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Resolved note path '{combined}' is outside the vault.");
        }

        return combined;
    }

    private static string SubstituteTokens(string template, RecordingResult recording)
    {
        var start = recording.StartedAt;

        return Token().Replace(template, match =>
        {
            var name = match.Groups["name"].Value.ToLowerInvariant();
            var format = match.Groups["fmt"].Success ? match.Groups["fmt"].Value : null;

            return name switch
            {
                "date" => start.ToString(format ?? "yyyy-MM-dd"),
                "time" => start.ToString(format ?? "HH-mm"),
                "title" => FileNames.Sanitize(recording.Title),
                _ => match.Value,
            };
        });
    }

    private static string MakeUnique(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private string BuildNewNote(RecordingResult recording, IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();

        if (config.Notes.IncludeFrontmatter)
        {
            builder.AppendLine("---");
            builder.AppendLine("type: meeting");
            builder.AppendLine($"title: {YamlString(recording.Title)}");
            builder.AppendLine($"date: {recording.StartedAt:yyyy-MM-dd}");
            builder.AppendLine($"start: {recording.StartedAt:HH:mm}");
            builder.AppendLine($"end: {recording.EndedAt:HH:mm}");
            builder.AppendLine($"duration: {FormatDuration(recording.Duration)}");
            builder.AppendLine("source: meeting-scribe");

            if ((config.Recording.KeepAudioFiles || recording.IsManualImport) && recording.HasAudio)
            {
                builder.AppendLine("audio:");
                foreach (var file in recording.AudioFiles) builder.AppendLine($"  - {YamlString(file)}");
            }

            if (config.Notes.Tags.Length > 0)
            {
                builder.AppendLine("tags:");
                foreach (var tag in config.Notes.Tags) builder.AppendLine($"  - {tag}");
            }

            builder.AppendLine("---");
            builder.AppendLine();
        }

        builder.AppendLine($"# {recording.Title}");
        builder.AppendLine();
        builder.AppendLine(
            $"> [!info] Recorded {recording.StartedAt:yyyy-MM-dd HH:mm}–{recording.EndedAt:HH:mm} " +
            $"({FormatDuration(recording.Duration)}) · transcribed locally with `{config.Transcription.Model}`");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(config.Notes.HeaderSnippet))
        {
            builder.AppendLine(config.Notes.HeaderSnippet);
            builder.AppendLine();
        }

        builder.AppendLine("## Transcript");
        builder.AppendLine();
        AppendTranscript(builder, segments);

        return builder.ToString();
    }

    private string BuildAppendSection(RecordingResult recording, IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine($"## {recording.StartedAt:HH:mm} — {recording.Title}");
        builder.AppendLine();
        builder.AppendLine(
            $"> [!info] Recorded {recording.StartedAt:yyyy-MM-dd HH:mm}–{recording.EndedAt:HH:mm} " +
            $"({FormatDuration(recording.Duration)})");
        builder.AppendLine();
        AppendTranscript(builder, segments);

        return builder.ToString();
    }

    private void AppendTranscript(StringBuilder builder, IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count == 0)
        {
            builder.AppendLine("_No speech was detected in this recording._");
            builder.AppendLine();
            return;
        }

        foreach (var block in Merge(segments))
        {
            var prefix = config.Notes.IncludeTimestamps
                ? $"**[{block.Start:hh\\:mm\\:ss}] {block.Speaker}:** "
                : $"**{block.Speaker}:** ";

            builder.AppendLine(prefix + block.Text);
            builder.AppendLine();
        }
    }

    /// <summary>Interleaves both tracks by time and joins consecutive lines from one speaker.</summary>
    private IEnumerable<TranscriptSegment> Merge(IReadOnlyList<TranscriptSegment> segments)
    {
        var ordered = segments.OrderBy(s => s.Start).ThenBy(s => s.End).ToList();
        var gap = TimeSpan.FromSeconds(Math.Max(0, config.Transcription.MergeGapSeconds));

        TranscriptSegment? pending = null;

        foreach (var segment in ordered)
        {
            if (pending is null)
            {
                pending = segment;
                continue;
            }

            var sameSpeaker = pending.Speaker == segment.Speaker;
            var closeEnough = segment.Start - pending.End <= gap;

            if (sameSpeaker && closeEnough)
            {
                pending = pending with
                {
                    End = segment.End,
                    Text = $"{pending.Text} {segment.Text}".Trim(),
                };
                continue;
            }

            yield return pending;
            pending = segment;
        }

        if (pending is not null) yield return pending;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{Math.Max(1, (int)duration.TotalMinutes)}m";

    private static string YamlString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    [GeneratedRegex(@"\{(?<name>\w+)(?::(?<fmt>[^}]*))?\}")]
    private static partial Regex Token();
}
