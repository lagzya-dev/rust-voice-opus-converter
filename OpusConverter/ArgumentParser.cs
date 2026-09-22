using System.Globalization;

namespace OpusConverter;

public sealed class CliArguments
{
    public List<string> Inputs { get; } = new();
    public string? Output { get; set; }
    public bool ShowHelp { get; set; }
    public ConvertOptions Options { get; } = new();
}

public static class ArgumentParser
{
    public static CliArguments Parse(string[] args)
    {
        var result = new CliArguments();
        ConvertOptions o = result.Options;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Option {arg} needs a value.");
                }

                return args[++i];
            }

            switch (arg)
            {
                case "-h":
                case "--help":
                case "/?":
                    result.ShowHelp = true;
                    break;
                case "-o":
                case "--output":
                    result.Output = Next();
                    break;
                case "-b":
                case "--bitrate":
                    o.BitrateKbps = ParseInt(arg, Next(), 8, 256);
                    break;
                case "-v":
                case "--volume":
                    o.Volume = ParseDouble(arg, Next(), 0.01, 10);
                    break;
                case "-n":
                case "--normalize":
                    o.Normalize = true;
                    break;
                case "-m":
                case "--mode":
                    o.Mode = Next().ToLowerInvariant() switch
                    {
                        "music" => EncodeMode.Music,
                        "voice" => EncodeMode.Voice,
                        var other => throw new ArgumentException($"Unknown mode '{other}'. Use 'music' or 'voice'."),
                    };
                    break;
                case "-s":
                case "--start":
                    o.Start = Next();
                    break;
                case "-t":
                case "--duration":
                    o.Duration = Next();
                    break;
                case "--ffmpeg":
                    o.FfmpegPath = Next();
                    break;
                case "--max-download-mb":
                    o.MaxDownloadMb = ParseInt(arg, Next(), 1, 4096);
                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        throw new ArgumentException($"Unknown option '{arg}'. Use --help.");
                    }

                    result.Inputs.Add(arg);
                    break;
            }
        }

        return result;
    }

    private static int ParseInt(string option, string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < min || number > max)
        {
            throw new ArgumentException($"{option} must be a whole number from {min} to {max}, got '{value}'.");
        }

        return number;
    }

    private static double ParseDouble(string option, string value, double min, double max)
    {
        if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number < min || number > max)
        {
            throw new ArgumentException($"{option} must be a number from {min} to {max}, got '{value}'.");
        }

        return number;
    }
}
