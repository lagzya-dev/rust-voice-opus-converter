
namespace OpusConverter;

public enum EncodeMode
{
    Music,
    Voice,
}

public sealed class ConvertOptions
{
    /// <summary>
    /// 24000 Hz - Rust's own voice chat rate - is the only value the GUI/CLI ever set. Opus itself, and this
    /// property, accept other rates too (kept settable for the encoder's own tests), but confirmed in-game testing
    /// showed 48000 Hz produces a structurally valid .rvoice file that Rust's client plays back silently.
    /// </summary>
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
