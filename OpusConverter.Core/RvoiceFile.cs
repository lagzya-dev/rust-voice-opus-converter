namespace OpusConverter;

/// <summary>
/// The .rvoice container read by the OpusVoice plugin (little endian):
/// "RVOC", u16 version(1), u32 sampleRate, u16 frameSamples, u32 frameCount,
/// then frameCount times { u16 length, byte[length] opusPacket }.
/// </summary>
public sealed class RvoiceFile
{
    public const string Extension = ".rvoice";
    public const int Version = 1;
    public const int MaxOpusPacket = 1275;

    private const uint Magic = 0x434F5652; // "RVOC" when written little endian

    public int SampleRate { get; }
    public int FrameSamples { get; }
    public IReadOnlyList<byte[]> Frames { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames.Count * FrameSamples / SampleRate);

    public RvoiceFile(int sampleRate, int frameSamples, IReadOnlyList<byte[]> frames)
    {
        SampleRate = sampleRate;
        FrameSamples = frameSamples;
        Frames = frames;
    }

    public void Write(Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write((ushort)Version);
        writer.Write((uint)SampleRate);
        writer.Write((ushort)FrameSamples);
        writer.Write((uint)Frames.Count);
        foreach (byte[] frame in Frames)
        {
            if (frame.Length is 0 or > MaxOpusPacket)
            {
                throw new InvalidDataException($"Opus packet of {frame.Length} bytes cannot be stored.");
            }

            writer.Write((ushort)frame.Length);
            writer.Write(frame);
        }
    }

    /// <summary>Writes through a temporary file so a failed conversion never leaves a half-written .rvoice behind.</summary>
    public void Save(string path)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        string temp = path + ".tmp";
        try
        {
            using (FileStream stream = File.Create(temp))
            {
                Write(stream);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static RvoiceFile Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Not an .rvoice file.");
        }

        int version = reader.ReadUInt16();
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported .rvoice version {version}.");
        }

        int sampleRate = checked((int)reader.ReadUInt32());
        int frameSamples = reader.ReadUInt16();
        uint frameCount = reader.ReadUInt32();

        var frames = new List<byte[]>((int)Math.Min(frameCount, 100_000u));
        for (uint i = 0; i < frameCount; i++)
        {
            int length = reader.ReadUInt16();
            byte[] frame = reader.ReadBytes(length);
            if (length == 0 || length > MaxOpusPacket || frame.Length != length)
            {
                throw new InvalidDataException("The .rvoice file is corrupted.");
            }

            frames.Add(frame);
        }

        return new RvoiceFile(sampleRate, frameSamples, frames);
    }

    public static RvoiceFile Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }
}

public static class RvoicePreview
{
    /// <summary>
    /// Decodes an .rvoice back into a mono 16-bit WAV in memory, i.e. exactly what a player's client will hear after Opus,
    /// so a conversion can be auditioned without starting the game.
    /// </summary>
    public static byte[] ToWav(RvoiceFile file)
    {
        var decoder = Concentus.OpusCodecFactory.CreateDecoder(file.SampleRate, 1);
        var pcm = new short[file.FrameSamples];

        using var data = new MemoryStream(file.Frames.Count * file.FrameSamples * 2);
        var bytes = new byte[file.FrameSamples * 2];
        foreach (byte[] frame in file.Frames)
        {
            int decoded = decoder.Decode(frame, pcm, file.FrameSamples, false);
            Buffer.BlockCopy(pcm, 0, bytes, 0, decoded * 2);
            data.Write(bytes, 0, decoded * 2);
        }

        using var wav = new MemoryStream();
        using var writer = new BinaryWriter(wav);
        writer.Write("RIFF"u8.ToArray());
        writer.Write((int)(36 + data.Length));
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(file.SampleRate);
        writer.Write(file.SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write((int)data.Length);
        writer.Write(data.GetBuffer(), 0, (int)data.Length);
        writer.Flush();
        return wav.ToArray();
    }
}
