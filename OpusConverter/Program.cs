using OpusConverter;

const string Help = """
OpusConverter - converts any audio/video file or direct link into an .rvoice file
for the OpusVoice Carbon plugin (Rust voice chat).

Usage:
  OpusConverter <file | folder | http(s) link> [more inputs...] [options]

Options:
  -o, --output <path>     Output .rvoice file (one input) or folder (several). Default: next to the current folder, named after the input.
  -b, --bitrate <kbps>    Opus bitrate, 8-256. Default 48.
  -r, --sample-rate <hz>  12000, 16000, 24000 or 48000. Default 24000 (what Steam voice uses).
  -m, --mode <music|voice> Encoder tuning. Default music.
  -v, --volume <x>        Volume multiplier, e.g. 0.5 or 1.5. Default 1.
  -n, --normalize         Even out loudness (EBU R128).
  -s, --start <time>      Start offset, seconds or hh:mm:ss.
  -t, --duration <time>   Convert only this much audio.
      --ffmpeg <path>     ffmpeg executable or its folder (otherwise: next to this exe, FFMPEG_PATH, PATH).
      --max-download-mb   Refuse links larger than this. Default 512.
  -h, --help              Show this help.

Examples:
  OpusConverter song.mp3
  OpusConverter https://example.com/track.mp3 -o siren.rvoice -b 64
  OpusConverter clip.mp4 -s 00:00:30 -t 20 -n
  OpusConverter C:\music -o C:\server\carbon\data\OpusVoice

Put the resulting .rvoice files into <server>/carbon/data/OpusVoice/ and play them in game with /vplay <name>.
""";

CliArguments cli;
try
{
    cli = ArgumentParser.Parse(args);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine("Error: " + e.Message);
    return 2;
}

if (cli.ShowHelp)
{
    Console.WriteLine(Help);
    return 0;
}

// Started without arguments (double click / drag and drop): ask for the source.
if (cli.Inputs.Count == 0)
{
    Console.WriteLine("OpusConverter - drop a file here or paste a file path / direct link (empty line to quit):");
    Console.Write("> ");
    string? line = Console.ReadLine()?.Trim().Trim('"');
    if (string.IsNullOrEmpty(line))
    {
        return 0;
    }

    cli.Inputs.Add(line);
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var jobs = new List<string>();
foreach (string input in cli.Inputs)
{
    string trimmed = input.Trim('"');
    if (!InputResolver.IsUrl(trimmed) && !File.Exists(trimmed) && !Directory.Exists(trimmed))
    {
        Console.Error.WriteLine($"Error: '{trimmed}' is not an existing file, folder or http(s) link.");
        return 1;
    }

    jobs.AddRange(InputResolver.Expand(trimmed));
}

AudioConverter converter;
try
{
    converter = new AudioConverter(cli.Options, Console.WriteLine);
}
catch (FileNotFoundException e)
{
    Console.Error.WriteLine("Error: " + e.Message);
    return 1;
}

bool outputIsFile = cli.Output != null
    && jobs.Count == 1
    && cli.Output.EndsWith(RvoiceFile.Extension, StringComparison.OrdinalIgnoreCase);

int failed = 0;
foreach (string job in jobs)
{
    string name = InputResolver.SuggestName(job);

    string outputPath = outputIsFile
        ? cli.Output!
        : Path.Combine(cli.Output ?? Environment.CurrentDirectory, name + RvoiceFile.Extension);

    Console.WriteLine($"Converting {job}");
    try
    {
        ConversionResult result = await converter.ConvertAsync(job, outputPath, cancellation.Token);
        Console.WriteLine($"  done: {result.OutputPath}  ({result.File.Duration:m\\:ss}, {result.OutputBytes / 1024} kB, {result.File.Frames.Count} frames)");
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return 130;
    }
    catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or ConversionException or InvalidOperationException or UnauthorizedAccessException)
    {
        failed++;
        Console.Error.WriteLine("  failed: " + e.Message);
    }
}

return failed == 0 ? 0 : 1;
