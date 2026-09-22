using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpusConverter;

public static class FfmpegProbe
{
    private static readonly Regex DurationLine = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>Asks ffmpeg for the media duration; null when unknown (live streams, broken headers).</summary>
    public static async Task<TimeSpan?> ProbeDurationAsync(string ffmpegPath, string input, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-nostdin");
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(input);

        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            return ParseDuration(await errors);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    public static TimeSpan? ParseDuration(string ffmpegOutput)
    {
        Match match = DurationLine.Match(ffmpegOutput);
        if (!match.Success)
        {
            return null;
        }

        double seconds = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
            + double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
            + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }
}
