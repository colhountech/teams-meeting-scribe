using System.Text.RegularExpressions;
using MeetingScribe.Configuration;
using MeetingScribe.Infrastructure;
using Whisper.net;
using Whisper.net.Ggml;

namespace MeetingScribe.Transcription;

/// <summary>Runs Whisper locally (via whisper.cpp) over a converted WAV track.</summary>
internal sealed partial class WhisperTranscriber(TranscriptionConfig config) : IDisposable
{
    private WhisperFactory? _factory;
    private string? _loadedModelPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Whisper emits these on silence; they are always noise, never speech.</summary>
    private static readonly string[] Hallucinations =
    [
        "[blank_audio]", "[silence]", "(silence)", "[music]", "(music)", "[ silence ]",
        "[inaudible]", "you", "thank you.", "thanks for watching!", ".", "...",
    ];

    public string ModelName => config.Model;

    public async Task<string> EnsureModelAsync(IProgress<string>? progress, CancellationToken token)
    {
        var type = ParseModel(config.Model);
        var path = Path.Combine(Paths.ModelDir, $"ggml-{type}.bin");

        if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) return path;

        Directory.CreateDirectory(Paths.ModelDir);
        progress?.Report($"Downloading Whisper model '{type}'…");
        Log.Info($"Downloading Whisper model '{type}' to {path}");

        var temp = path + ".part";
        await using (var source = await WhisperGgmlDownloader.Default
            .GetGgmlModelAsync(type, QuantizationType.NoQuantization, token).ConfigureAwait(false))
        await using (var destination = File.Create(temp))
        {
            await source.CopyToAsync(destination, token).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
        Log.Info($"Model ready: {path}");
        return path;
    }

    public async Task<List<TranscriptSegment>> TranscribeAsync(
        ConvertedAudio audio,
        string speaker,
        IProgress<string>? progress,
        CancellationToken token)
    {
        var segments = new List<TranscriptSegment>();
        var modelPath = await EnsureModelAsync(progress, token).ConfigureAwait(false);

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_factory is null || _loadedModelPath != modelPath)
            {
                _factory?.Dispose();
                _factory = WhisperFactory.FromPath(modelPath);
                _loadedModelPath = modelPath;
            }

            var builder = _factory.CreateBuilder()
                .WithThreads(ResolveThreadCount())
                .WithNoSpeechThreshold(0.6f)
                .WithProbabilities();

            builder = string.Equals(config.Language, "auto", StringComparison.OrdinalIgnoreCase)
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(config.Language);

            await using var processor = builder.Build();
            await using var stream = File.OpenRead(audio.Path);

            await foreach (var segment in processor.ProcessAsync(stream, token).ConfigureAwait(false))
            {
                var text = Clean(segment.Text);
                if (text is null) continue;
                if (segment.Probability > 0 && segment.Probability < config.MinimumProbability) continue;
                if (audio.PeakRms(segment.Start, segment.End) < config.SilenceRmsThreshold) continue;

                segments.Add(new TranscriptSegment(speaker, segment.Start, segment.End, text));
            }
        }
        finally
        {
            _gate.Release();
        }

        Log.Info($"Transcribed '{speaker}' track: {segments.Count} segments from {audio.Duration:hh\\:mm\\:ss}.");
        return segments;
    }

    private int ResolveThreadCount() =>
        config.Threads > 0 ? config.Threads : Math.Max(1, Environment.ProcessorCount / 2);

    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = BracketedNoise().Replace(raw, " ");
        text = Whitespace().Replace(text, " ").Trim();

        if (text.Length == 0) return null;
        if (Hallucinations.Contains(text, StringComparer.OrdinalIgnoreCase)) return null;

        return text;
    }

    [GeneratedRegex(@"[\[\(](?:BLANK_AUDIO|SILENCE|MUSIC|INAUDIBLE|APPLAUSE|LAUGHTER|NOISE)[\]\)]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedNoise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static GgmlType ParseModel(string name) =>
        Enum.TryParse<GgmlType>(name, ignoreCase: true, out var parsed) ? parsed : GgmlType.SmallEn;

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        _gate.Dispose();
    }
}
