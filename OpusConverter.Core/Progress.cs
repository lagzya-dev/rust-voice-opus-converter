using System.Globalization;
using System.Text.RegularExpressions;

namespace OpusConverter;

public enum ConversionStage
{
    Downloading,
    Analyzing,
    Encoding,
}

/// <param name="Fraction">0..1 when the total is known, otherwise null (show an indeterminate bar).</param>
public readonly record struct ConversionProgress(ConversionStage Stage, double? Fraction);

public static class TimeSpec
{
    private static readonly Regex Clock = new(@"^(?:(\d+):)?(\d+):(\d+(?:\.\d+)?)$", RegexOptions.Compiled);

    /// <summary>Accepts seconds ("90", "1.5") and clock times ("1:30", "00:01:30.5").</summary>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim().Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            if (seconds < 0 || seconds > 100 * 3600)
            {
                return false;
            }

            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        Match match = Clock.Match(text);
        if (!match.Success)
        {
            return false;
        }

        double hours = match.Groups[1].Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        double minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        double secs = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (minutes >= 60 && match.Groups[1].Success || secs >= 60)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + secs);
        return true;
    }
}
