using System.Net.Http.Headers;

namespace OpusConverter;

/// <summary>An audio source on disk; a downloaded URL lives in a temp file that is deleted on dispose.</summary>
public sealed class ResolvedInput : IDisposable
{
    private readonly string? _tempFile;

    public string Path { get; }

    /// <summary>File name without extension, used to name the output.</summary>
    public string BaseName { get; }

    public ResolvedInput(string path, string baseName, string? tempFile = null)
    {
        Path = path;
        BaseName = baseName;
        _tempFile = tempFile;
    }

    public void Dispose()
    {
        if (_tempFile != null && File.Exists(_tempFile))
        {
            try
            {
                File.Delete(_tempFile);
            }
            catch (IOException)
            {
                // Temp files are best effort; the OS cleans the temp folder eventually.
            }
        }
    }
}

public static class InputResolver
{
    private static readonly HttpClient Http = CreateClient();

    public static bool IsUrl(string input) =>
        Uri.TryCreate(input, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Expands directories into the files they contain; files and URLs are returned unchanged.</summary>
    public static IEnumerable<string> Expand(string input)
    {
        if (!IsUrl(input) && Directory.Exists(input))
        {
            return Directory.EnumerateFiles(input).Order(StringComparer.OrdinalIgnoreCase);
        }

        return new[] { input };
    }

    /// <summary>Default name (without extension) for the .rvoice made from this file, folder entry or link.</summary>
    public static string SuggestName(string input)
    {
        string name = IsUrl(input)
            ? System.IO.Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(new Uri(input).AbsolutePath))
            : System.IO.Path.GetFileNameWithoutExtension(input);
        return Sanitize(name);
    }

    public static async Task<ResolvedInput> ResolveAsync(string input, int maxDownloadMb, Action<string> log, CancellationToken cancellation, IProgress<ConversionProgress>? progress = null)
    {
        if (!IsUrl(input))
        {
            if (!File.Exists(input))
            {
                throw new FileNotFoundException($"File not found: {input}");
            }

            return new ResolvedInput(input, System.IO.Path.GetFileNameWithoutExtension(input));
        }

        var uri = new Uri(input);
        log($"Downloading {uri} ...");

        using HttpResponseMessage response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase} for {uri}.", null, response.StatusCode);
        }

        long limit = maxDownloadMb * 1024L * 1024L;
        if (response.Content.Headers.ContentLength is long declared && declared > limit)
        {
            throw new DownloadTooLargeException(maxDownloadMb, $"The file is {declared / 1024 / 1024} MB, over the {maxDownloadMb} MB limit (--max-download-mb).");
        }

        if (response.Content.Headers.ContentType?.MediaType is string type && type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotDirectLinkException($"The link returned a web page ({type}), not an audio file. Use a direct link to the file.");
        }

        string baseName = BaseNameFromUri(uri, response.Content.Headers.ContentDisposition);
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opusconv_" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellation);
            await using FileStream target = File.Create(temp);

            long? declaredLength = response.Content.Headers.ContentLength;
            var buffer = new byte[81920];
            long total = 0;
            long lastReported = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    throw new DownloadTooLargeException(maxDownloadMb, $"The download is over the {maxDownloadMb} MB limit (--max-download-mb). Is this a live stream?");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellation);

                if (progress != null && total - lastReported >= 128 * 1024)
                {
                    lastReported = total;
                    progress.Report(new ConversionProgress(ConversionStage.Downloading, declaredLength is > 0 ? (double)total / declaredLength.Value : null));
                }
            }

            log($"Downloaded {total / 1024.0 / 1024.0:F1} MB.");
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            throw;
        }

        return new ResolvedInput(temp, baseName, temp);
    }

    private static string BaseNameFromUri(Uri uri, ContentDispositionHeaderValue? disposition)
    {
        string? name = disposition?.FileNameStar ?? disposition?.FileName;
        name = name?.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
        {
            name = System.IO.Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        }

        name = System.IO.Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(name) ? "audio" : Sanitize(name);
    }

    public static string Sanitize(string name)
    {
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray();
        string result = new string(chars).Trim('_', '.');
        return result.Length == 0 ? "audio" : result;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OpusConverter/1.0)");
        return client;
    }
}
