using System.Text;

namespace MeetingScribe.Infrastructure;

internal static class FileNames
{
    private static readonly char[] Invalid =
        [.. Path.GetInvalidFileNameChars(), ':', '*', '?', '"', '<', '>', '|', '/', '\\'];

    /// <summary>Makes an arbitrary string safe to use as a single path segment.</summary>
    public static string Sanitize(string value, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Untitled";

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var ch in value)
        {
            if (Invalid.Contains(ch) || char.IsControl(ch))
            {
                if (!lastWasSpace) { builder.Append(' '); lastWasSpace = true; }
                continue;
            }

            lastWasSpace = ch == ' ';
            builder.Append(ch);
        }

        var cleaned = builder.ToString().Trim().TrimEnd('.');
        if (cleaned.Length > maxLength) cleaned = cleaned[..maxLength].Trim();

        return cleaned.Length == 0 ? "Untitled" : cleaned;
    }
}
