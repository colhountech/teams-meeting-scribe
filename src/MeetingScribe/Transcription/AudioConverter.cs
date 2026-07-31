using MeetingScribe.Infrastructure;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingScribe.Transcription;

/// <summary>A 16 kHz mono WAV plus a coarse loudness envelope used to reject silence.</summary>
internal sealed record ConvertedAudio(string Path, TimeSpan Duration, float[] Envelope, double WindowSeconds)
{
    /// <summary>Loudest window (RMS, 0..1) inside the given time range.</summary>
    public float PeakRms(TimeSpan start, TimeSpan end)
    {
        if (Envelope.Length == 0) return 1f;

        var from = Math.Clamp((int)(start.TotalSeconds / WindowSeconds), 0, Envelope.Length - 1);
        var to = Math.Clamp((int)Math.Ceiling(end.TotalSeconds / WindowSeconds), from + 1, Envelope.Length);

        var peak = 0f;
        for (var i = from; i < to; i++) peak = Math.Max(peak, Envelope[i]);
        return peak;
    }
}

/// <summary>Converts arbitrary recordings into the 16 kHz mono PCM that Whisper expects.</summary>
internal static class AudioConverter
{
    private const int TargetSampleRate = 16000;
    private const double WindowSeconds = 0.1;

    public static ConvertedAudio? Convert(string inputPath, string outputPath)
    {
        try
        {
            using var reader = new AudioFileReader(inputPath);

            ISampleProvider provider = reader;
            if (provider.WaveFormat.Channels > 1) provider = new MonoDownmixSampleProvider(provider);
            if (provider.WaveFormat.SampleRate != TargetSampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);
            }

            var tap = new EnvelopeTapSampleProvider(provider, WindowSeconds);
            WaveFileWriter.CreateWaveFile16(outputPath, tap);

            var duration = TimeSpan.FromSeconds((double)tap.SamplesRead / TargetSampleRate);
            return new ConvertedAudio(outputPath, duration, tap.BuildEnvelope(), WindowSeconds);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not convert '{Path.GetFileName(inputPath)}' for transcription", ex);
            return null;
        }
    }
}

/// <summary>Averages all channels down to one.</summary>
internal sealed class MonoDownmixSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly int _channels = source.WaveFormat.Channels;
    private float[] _buffer = [];

    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        var needed = count * _channels;
        if (_buffer.Length < needed) _buffer = new float[needed];

        var read = source.Read(_buffer, 0, needed);
        var frames = read / _channels;

        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var ch = 0; ch < _channels; ch++) sum += _buffer[(frame * _channels) + ch];
            buffer[offset + frame] = sum / _channels;
        }

        return frames;
    }
}

/// <summary>Pass-through provider that records an RMS value per fixed-length window.</summary>
internal sealed class EnvelopeTapSampleProvider(ISampleProvider source, double windowSeconds) : ISampleProvider
{
    private readonly int _windowSamples = Math.Max(1, (int)(source.WaveFormat.SampleRate * windowSeconds));
    private readonly List<float> _windows = [];

    private double _sumOfSquares;
    private int _samplesInWindow;

    public WaveFormat WaveFormat => source.WaveFormat;
    public long SamplesRead { get; private set; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        SamplesRead += read;

        for (var i = 0; i < read; i++)
        {
            var sample = buffer[offset + i];
            _sumOfSquares += (double)sample * sample;

            if (++_samplesInWindow < _windowSamples) continue;

            _windows.Add((float)Math.Sqrt(_sumOfSquares / _samplesInWindow));
            _sumOfSquares = 0;
            _samplesInWindow = 0;
        }

        return read;
    }

    public float[] BuildEnvelope()
    {
        if (_samplesInWindow > 0) _windows.Add((float)Math.Sqrt(_sumOfSquares / _samplesInWindow));
        return [.. _windows];
    }
}
