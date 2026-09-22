namespace OpusConverter;

/// <summary>Base for failures the UI can explain in plain words. Callers catch this to report a problem with one input without stopping a batch.</summary>
public class ConversionException : Exception
{
    public ConversionException(string message) : base(message)
    {
    }
}

/// <summary>ffmpeg could not extract any audio: not an audio/video file, or it is damaged.</summary>
public sealed class NoAudioException : ConversionException
{
    public NoAudioException(string message) : base(message)
    {
    }
}

/// <summary>The link returned a web page instead of a media file.</summary>
public sealed class NotDirectLinkException : ConversionException
{
    public NotDirectLinkException(string message) : base(message)
    {
    }
}

/// <summary>The download is larger than the allowed limit.</summary>
public sealed class DownloadTooLargeException : ConversionException
{
    public int LimitMb { get; }

    public DownloadTooLargeException(int limitMb, string message) : base(message)
    {
        LimitMb = limitMb;
    }
}
