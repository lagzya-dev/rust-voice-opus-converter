using System.Text.Json;

namespace OpusConverter.Gui;

/// <summary>What the window remembers between launches (stored in %AppData%\OpusConverter\settings.json).</summary>
public sealed class AppSettings
{
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OpusConverter");

    public int BitrateKbps { get; set; } = 48;
    public bool VoiceMode { get; set; }
    public double VolumePercent { get; set; } = 100;
    public bool Normalize { get; set; }
    public string? FfmpegPath { get; set; }

    /// <summary>OPUSCONVERTER_SETTINGS_DIR moves the settings elsewhere (portable use, automated runs).</summary>
    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("OPUSCONVERTER_SETTINGS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpusConverter"),
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    return loaded.Sanitized();
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must never stop the app from starting.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember settings is not worth interrupting the user.
        }
    }

    private AppSettings Sanitized()
    {
        BitrateKbps = Math.Clamp(BitrateKbps, 16, 128);
        VolumePercent = Math.Clamp(VolumePercent, 25, 300);
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = new AppSettings().OutputDirectory;
        }

        return this;
    }
}
