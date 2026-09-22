
namespace OpusConverter;

public enum EncodeMode
{
    Music,
    Voice,
}

public sealed class ConvertOptions
{
    /// <summary>
    /// Sample rates Opus can encode that Steam's voice decoder (11025-48000 Hz) also accepts as a packet header value.
    /// 48000 is listed for completeness and produces a structurally valid .rvoice file, but confirmed in-game testing
    /// shows Rust's client plays it back silently at that rate — 24000 (Rust's own voice chat rate) is what actually works.
    /// </summary>
    public static readonly int[] SupportedSampleRates = { 12000, 16000, 24000, 48000 };

    public int SampleRate { get; set; } = 24000;
    public int BitrateKbps { get; set; } = 48;
    public double Volume { get; set; } = 1.0;
    public bool Normalize { get; set; }
    public EncodeMode Mode { get; set; } = EncodeMode.Music;
    public string? Start { get; set; }
    public string? Duration { get; set; }
    public string? FfmpegPath { get; set; }
    public int MaxDownloadMb { get; set; } = 512;

    /// <summary>Samples per channel in one 20 ms Opus frame.</summary>
    public int FrameSamples => SampleRate / 50;
}
