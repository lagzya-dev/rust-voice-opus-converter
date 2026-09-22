using Concentus;
using Concentus.Enums;

namespace OpusConverter;

public sealed record ConversionResult(RvoiceFile File, string OutputPath, long OutputBytes);

/// <summary>Any audio/video file or direct link -> ffmpeg PCM -> 20 ms Opus frames -> .rvoice.</summary>
public sealed class AudioConverter
{
    private readonly ConvertOptions _options;
    private readonly string _ffmpeg;
    private readonly Action<string> _log;

    public AudioConverter(ConvertOptions options, Action<string> log)
    {
        _options = options;
        _log = log;
        _ffmpeg = Ffmpeg.Locate(options.FfmpegPath);
    }

    public async Task<ConversionResult> ConvertAsync(string input, string outputPath, CancellationToken cancellation, IProgress<ConversionProgress>? progress = null)
    {
        using ResolvedInput source = await InputResolver.ResolveAsync(input, _options.MaxDownloadMb, _log, cancellation, progress);
        RvoiceFile file = await EncodeAsync(source.Path, cancellation, progress);

        file.Save(outputPath);
        return new ConversionResult(file, outputPath, new FileInfo(outputPath).Length);
    }

    public async Task<RvoiceFile> EncodeAsync(string localPath, CancellationToken cancellation, IProgress<ConversionProgress>? progress = null)
    {
        int frameSamples = _options.FrameSamples;
        IOpusEncoder encoder = CreateEncoder();

        progress?.Report(new ConversionProgress(ConversionStage.Analyzing, null));
        double? expectedSeconds = await ExpectedSecondsAsync(localPath, cancellation);

        var frames = new List<byte[]>();
        var pcmBytes = new byte[frameSamples * sizeof(short)];
        var pcm = new short[frameSamples];
        var packet = new byte[RvoiceFile.MaxOpusPacket];

        using FfmpegProcess ffmpeg = Ffmpeg.Start(_ffmpeg, localPath, _options);
        Stream stream = ffmpeg.Pcm;

        int lastReported = 0;
        while (true)
        {
            int filled = await ReadFullAsync(stream, pcmBytes, cancellation);
            if (filled == 0)
            {
                break;
            }

            // Pad a short final frame with silence so the encoder always gets a whole 20 ms.
            Array.Clear(pcmBytes, filled, pcmBytes.Length - filled);
            Buffer.BlockCopy(pcmBytes, 0, pcm, 0, pcmBytes.Length);

            int length = encoder.Encode(pcm.AsSpan(), frameSamples, packet.AsSpan(), packet.Length);
            if (length > 0)
            {
                frames.Add(packet.AsSpan(0, length).ToArray());
            }

            int seconds = frames.Count * frameSamples / _options.SampleRate;
            if (seconds >= lastReported + 10)
            {
                lastReported = seconds;
                _log($"  encoded {seconds / 60}:{seconds % 60:00} ...");
            }

            if (progress != null && frames.Count % 25 == 0)
            {
                double encoded = (double)frames.Count * frameSamples / _options.SampleRate;
                progress.Report(new ConversionProgress(ConversionStage.Encoding, expectedSeconds is double total ? Math.Min(0.99, encoded / total) : null));
            }

            if (filled < pcmBytes.Length)
            {
                break;
            }
        }

        int exitCode = await ffmpeg.WaitAsync(cancellation);
        if (frames.Count == 0)
        {
            string details = ffmpeg.ErrorText;
            throw new NoAudioException("ffmpeg produced no audio" + (details.Length > 0 ? ":\n" + details : ". Is the input an audio or video file?"));
        }

        if (exitCode != 0)
        {
            _log($"  warning: ffmpeg exited with code {exitCode}; the audio may be cut short.\n{ffmpeg.ErrorText}");
        }

        progress?.Report(new ConversionProgress(ConversionStage.Encoding, 1.0));
        return new RvoiceFile(_options.SampleRate, frameSamples, frames);
    }

    /// <summary>Length of the audio that will actually be converted (after --start / --duration), or null if unknown.</summary>
    private async Task<double?> ExpectedSecondsAsync(string path, CancellationToken cancellation)
    {
        TimeSpan? full = await FfmpegProbe.ProbeDurationAsync(_ffmpeg, path, cancellation);
        if (full is null)
        {
            return null;
        }

        double seconds = full.Value.TotalSeconds;
        if (TimeSpec.TryParse(_options.Start, out TimeSpan start))
        {
            seconds -= start.TotalSeconds;
        }

        if (TimeSpec.TryParse(_options.Duration, out TimeSpan duration))
        {
            seconds = Math.Min(seconds, duration.TotalSeconds);
        }

        return seconds > 0 ? seconds : null;
    }

    private IOpusEncoder CreateEncoder()
    {
        IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(
            _options.SampleRate,
            1,
            _options.Mode == EncodeMode.Voice ? OpusApplication.OPUS_APPLICATION_VOIP : OpusApplication.OPUS_APPLICATION_AUDIO);

        encoder.Bitrate = _options.BitrateKbps * 1000;
        encoder.UseVBR = true;
        encoder.Complexity = 10;
        encoder.SignalType = _options.Mode == EncodeMode.Voice ? OpusSignal.OPUS_SIGNAL_VOICE : OpusSignal.OPUS_SIGNAL_MUSIC;
        return encoder;
    }

    /// <summary>Reads until the buffer is full or the stream ends; returns how many bytes were read.</summary>
    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken cancellation)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellation);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
