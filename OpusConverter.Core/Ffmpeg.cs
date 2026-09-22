using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace OpusConverter;

/// <summary>Locates ffmpeg and runs it to turn any audio/video file into raw mono 16-bit PCM.</summary>
public static class Ffmpeg
{
    public static string Locate(string? explicitPath)
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (Directory.Exists(explicitPath))
            {
                explicitPath = Path.Combine(explicitPath, exe);
            }

            return File.Exists(explicitPath)
                ? explicitPath
                : throw new FileNotFoundException($"ffmpeg was not found at '{explicitPath}'.");
        }

        var candidates = new List<string>();
        string? fromEnv = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            candidates.Add(Directory.Exists(fromEnv) ? Path.Combine(fromEnv, exe) : fromEnv);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, exe));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, exe));

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path != null)
        {
            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(dir.Trim('"'), exe));
            }
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "ffmpeg was not found. Install it (Windows: winget install Gyan.FFmpeg), put ffmpeg.exe next to OpusConverter.exe, " +
            "or pass --ffmpeg <path>.");
    }

    public static IReadOnlyList<string> BuildArguments(string input, ConvertOptions options)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };

        if (!string.IsNullOrWhiteSpace(options.Start))
        {
            args.Add("-ss");
            args.Add(options.Start);
        }

        args.Add("-i");
        args.Add(input);

        if (!string.IsNullOrWhiteSpace(options.Duration))
        {
            args.Add("-t");
            args.Add(options.Duration);
        }

        args.Add("-vn");
        args.Add("-sn");
        args.Add("-dn");

        var filters = new List<string>();
        if (options.Normalize)
        {
            filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        }

        if (Math.Abs(options.Volume - 1.0) > 0.001)
        {
            filters.Add("volume=" + options.Volume.ToString("0.###", CultureInfo.InvariantCulture));
            if (options.Volume > 1.0)
            {
                filters.Add("alimiter=limit=0.98");
            }
        }

        if (filters.Count > 0)
        {
            args.Add("-af");
            args.Add(string.Join(',', filters));
        }

        args.Add("-ac");
        args.Add("1");
        args.Add("-ar");
        args.Add(options.SampleRate.ToString(CultureInfo.InvariantCulture));
        args.Add("-f");
        args.Add("s16le");
        args.Add("pipe:1");
        return args;
    }

    /// <summary>Starts ffmpeg. The caller reads PCM from StandardOutput and must dispose the returned process.</summary>
    public static FfmpegProcess Start(string ffmpegPath, string input, ConvertOptions options)
    {
        var info = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in BuildArguments(input, options))
        {
            info.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = info };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (errors)
                {
                    errors.AppendLine(e.Data);
                }
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        return new FfmpegProcess(process, errors);
    }
}

public sealed class FfmpegProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _errors;

    public FfmpegProcess(Process process, StringBuilder errors)
    {
        _process = process;
        _errors = errors;
    }

    public Stream Pcm => _process.StandardOutput.BaseStream;

    public async Task<int> WaitAsync(CancellationToken cancellation)
    {
        await _process.WaitForExitAsync(cancellation);
        return _process.ExitCode;
    }

    public string ErrorText
    {
        get
        {
            lock (_errors)
            {
                return _errors.ToString().Trim();
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
    }
}
