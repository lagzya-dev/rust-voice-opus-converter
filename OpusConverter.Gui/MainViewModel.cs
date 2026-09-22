using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Media;
using System.Text;
using System.Windows.Threading;

namespace OpusConverter.Gui;

public sealed class MainViewModel : ObservableObject
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".ogg", ".oga", ".opus", ".flac", ".m4a", ".aac", ".wma", ".aif", ".aiff", ".ape", ".mka", ".amr",
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".wmv", ".m4v", ".3gp", ".ts", ".mpg", ".mpeg",
    };

    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private readonly AppSettings _settings;
    private readonly StringBuilder _log = new();

    private CancellationTokenSource? _cts;
    private SoundPlayer? _player;
    private DispatcherTimer? _playTimer;

    private string _outputDirectory;
    private bool _outputIsTemporary;
    private int _bitrateKbps;
    private bool _voiceMode;
    private double _volumePercent;
    private bool _normalize;
    private string _start = "";
    private string _duration = "";
    private bool _isBusy;
    private double _overallProgress;
    private string _statusText = "Добавьте файлы или ссылки и нажмите «Конвертировать»";
    private bool _ffmpegOk;
    private string _ffmpegStatus = "";
    private JobItem? _playing;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _outputDirectory = _settings.OutputDirectory;
        _bitrateKbps = _settings.BitrateKbps;
        _voiceMode = _settings.VoiceMode;
        _volumePercent = _settings.VolumePercent;
        _normalize = _settings.Normalize;

        Jobs.CollectionChanged += OnJobsChanged;
        RefreshFfmpeg();
    }

    public ObservableCollection<JobItem> Jobs { get; } = new();

    // ---- settings ------------------------------------------------------------------------------------------------------

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (Set(ref _outputDirectory, value))
            {
                _outputIsTemporary = false;
            }
        }
    }

    /// <summary>Uses a folder for this run only (command line); the saved folder is left alone.</summary>
    public void OverrideOutputDirectory(string path)
    {
        OutputDirectory = path;
        _outputIsTemporary = true;
    }

    public double BitrateKbps
    {
        get => _bitrateKbps;
        set
        {
            if (Set(ref _bitrateKbps, (int)value))
            {
                Raise(nameof(BitrateText));
            }
        }
    }

    public string BitrateText => $"{_bitrateKbps} кбит/с  ·  около {(int)(_bitrateKbps * 7.5)} КБ на минуту";

    // Sample rate is fixed at 24 kHz (Rust's own voice chat rate) - not user-configurable. 48 kHz produces a
    // structurally valid .rvoice file, but confirmed in-game testing shows the NPC plays it back silently.
    private const int FixedSampleRate = 24000;

    public bool VoiceMode
    {
        get => _voiceMode;
        set
        {
            if (Set(ref _voiceMode, value))
            {
                Raise(nameof(MusicMode));
            }
        }
    }

    public bool MusicMode => !_voiceMode;

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (Set(ref _volumePercent, value))
            {
                Raise(nameof(VolumeText));
            }
        }
    }

    public string VolumeText => $"{(int)_volumePercent}%";

    public bool Normalize
    {
        get => _normalize;
        set => Set(ref _normalize, value);
    }

    public string Start
    {
        get => _start;
        set
        {
            if (Set(ref _start, value))
            {
                RaiseTrim();
            }
        }
    }

    public string Duration
    {
        get => _duration;
        set
        {
            if (Set(ref _duration, value))
            {
                RaiseTrim();
            }
        }
    }

    public string TrimError
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_start) && !TimeSpec.TryParse(_start, out _))
            {
                return "Начало: введите секунды (30) или время (1:30)";
            }

            if (!string.IsNullOrWhiteSpace(_duration) && !TimeSpec.TryParse(_duration, out _))
            {
                return "Длительность: введите секунды (30) или время (1:30)";
            }

            return "";
        }
    }

    public bool HasTrimError => TrimError.Length > 0;

    // ---- state ---------------------------------------------------------------------------------------------------------

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(CanEdit));
                RaiseCanConvert();
            }
        }
    }

    public bool CanEdit => !_isBusy;

    public bool IsEmpty => Jobs.Count == 0;

    public double OverallProgress
    {
        get => _overallProgress;
        private set => Set(ref _overallProgress, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool FfmpegOk
    {
        get => _ffmpegOk;
        private set
        {
            if (Set(ref _ffmpegOk, value))
            {
                Raise(nameof(FfmpegMissing));
                RaiseCanConvert();
            }
        }
    }

    public bool FfmpegMissing => !_ffmpegOk;

    public string FfmpegStatus
    {
        get => _ffmpegStatus;
        private set => Set(ref _ffmpegStatus, value);
    }

    public bool CanConvert => !_isBusy && _ffmpegOk && !HasTrimError && Jobs.Any(j => j.NeedsWork);

    public string LogText => _log.ToString();

    // ---- queue ---------------------------------------------------------------------------------------------------------

    /// <summary>Adds files, folders (their media files) and links. Returns how many new entries were queued.</summary>
    public int AddPaths(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string raw in paths)
        {
            string path = raw.Trim().Trim('"');
            if (path.Length == 0)
            {
                continue;
            }

            if (InputResolver.IsUrl(path))
            {
                added += AddJob(path) ? 1 : 0;
            }
            else if (Directory.Exists(path))
            {
                IEnumerable<string> files = Directory.EnumerateFiles(path)
                    .Where(f => MediaExtensions.Contains(Path.GetExtension(f)))
                    .Order(StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    added += AddJob(file) ? 1 : 0;
                }
            }
            else if (File.Exists(path))
            {
                added += AddJob(path) ? 1 : 0;
            }
            else
            {
                AppendLog($"Не найдено: {path}");
            }
        }

        return added;
    }

    public bool AddLink(string text)
    {
        string link = text.Trim();
        if (!InputResolver.IsUrl(link))
        {
            StatusText = "Это не похоже на ссылку. Она должна начинаться с http:// или https://";
            return false;
        }

        bool added = AddJob(link);
        if (!added)
        {
            StatusText = "Эта ссылка уже в списке";
        }

        return added;
    }

    private bool AddJob(string source)
    {
        if (Jobs.Any(j => string.Equals(j.Source, source, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var job = new JobItem(source);
        job.Name = UniqueName(job.Name);
        Jobs.Add(job);
        return true;
    }

    private string UniqueName(string baseName)
    {
        string name = baseName;
        for (int i = 2; Jobs.Any(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"{baseName}_{i}";
        }

        return name;
    }

    public void Remove(JobItem job)
    {
        if (job.State == JobState.Working)
        {
            return;
        }

        if (ReferenceEquals(_playing, job))
        {
            StopPreview();
        }

        Jobs.Remove(job);
    }

    public void ClearFinished()
    {
        foreach (JobItem job in Jobs.Where(j => j.State == JobState.Done).ToList())
        {
            Remove(job);
        }
    }

    public void Retry(JobItem job)
    {
        if (job.CanRetry)
        {
            job.Reset();
        }
    }

    // ---- conversion ----------------------------------------------------------------------------------------------------

    private ConvertOptions BuildOptions() => new()
    {
        SampleRate = FixedSampleRate,
        BitrateKbps = _bitrateKbps,
        Volume = _volumePercent / 100.0,
        Normalize = _normalize,
        Mode = _voiceMode ? EncodeMode.Voice : EncodeMode.Music,
        Start = string.IsNullOrWhiteSpace(_start) ? null : _start.Trim(),
        Duration = string.IsNullOrWhiteSpace(_duration) ? null : _duration.Trim(),
        FfmpegPath = _settings.FfmpegPath,
    };

    public async Task ConvertAllAsync()
    {
        if (!CanConvert)
        {
            return;
        }

        List<JobItem> pending = Jobs.Where(j => j.NeedsWork).ToList();

        AudioConverter converter;
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            converter = new AudioConverter(BuildOptions(), AppendLog);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FileNotFoundException)
        {
            StatusText = "Не удалось начать: " + ErrorText.Describe(e);
            AppendLog(e.ToString());
            return;
        }

        SaveSettings();
        StopPreview();
        IsBusy = true;
        OverallProgress = 0;
        _cts = new CancellationTokenSource();
        CancellationToken cancellation = _cts.Token;

        int succeeded = 0;
        int failed = 0;
        int finished = 0;
        bool cancelled = false;

        try
        {
            foreach (JobItem job in pending)
            {
                job.Begin();
                StatusText = $"Файл {finished + 1} из {pending.Count}: {job.Name}";

                string safeName = InputResolver.Sanitize(job.Name);
                string outputPath = Path.Combine(_outputDirectory, safeName + RvoiceFile.Extension);

                var progress = new Progress<ConversionProgress>(p =>
                {
                    job.ApplyProgress(p);
                    OverallProgress = (finished + job.Progress) / pending.Count;
                });

                try
                {
                    AppendLog($"→ {job.Source}");
                    ConversionResult result = await Task.Run(() => converter.ConvertAsync(job.Source, outputPath, cancellation, progress), cancellation);
                    job.Complete(result);
                    succeeded++;
                    AppendLog($"  готово: {outputPath} ({job.Info})");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    job.MarkCancelled();
                    cancelled = true;
                    break;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    job.Fail(ErrorText.Describe(e), e.Message);
                    failed++;
                    AppendLog($"  ошибка: {e}");
                }

                finished++;
                OverallProgress = (double)finished / pending.Count;
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }

        if (cancelled)
        {
            StatusText = "Отменено" + (succeeded > 0 ? $". Готово файлов: {succeeded}" : "");
        }
        else
        {
            OverallProgress = 1;
            StatusText = failed == 0
                ? $"Готово: {succeeded} {Plural(succeeded, "файл", "файла", "файлов")}. Скопируйте .rvoice в carbon/data/OpusVoice на сервере"
                : $"Готово: {succeeded}, с ошибками: {failed}";
        }
    }

    public void Cancel() => _cts?.Cancel();

    // ---- preview -------------------------------------------------------------------------------------------------------

    public async Task PreviewAsync(JobItem job)
    {
        if (job.Result is null)
        {
            return;
        }

        bool wasPlaying = ReferenceEquals(_playing, job);
        StopPreview();
        if (wasPlaying)
        {
            return;
        }

        RvoiceFile file = job.Result.File;
        byte[] wav = await Task.Run(() => RvoicePreview.ToWav(file));

        _player = new SoundPlayer(new MemoryStream(wav));
        _player.Play();
        _playing = job;
        job.IsPlaying = true;

        _playTimer = new DispatcherTimer { Interval = file.Duration + TimeSpan.FromMilliseconds(400) };
        _playTimer.Tick += (_, _) => StopPreview();
        _playTimer.Start();
    }

    public void StopPreview()
    {
        _playTimer?.Stop();
        _playTimer = null;
        _player?.Stop();
        _player?.Dispose();
        _player = null;
        if (_playing != null)
        {
            _playing.IsPlaying = false;
            _playing = null;
        }
    }

    // ---- ffmpeg / settings ---------------------------------------------------------------------------------------------

    public void SetFfmpeg(string? path)
    {
        _settings.FfmpegPath = string.IsNullOrWhiteSpace(path) ? null : path;
        RefreshFfmpeg();
    }

    private void RefreshFfmpeg()
    {
        try
        {
            string found = Ffmpeg.Locate(_settings.FfmpegPath);
            FfmpegStatus = "ffmpeg: " + found;
            FfmpegOk = true;
        }
        catch (FileNotFoundException)
        {
            FfmpegStatus = "ffmpeg не найден";
            FfmpegOk = false;
        }
    }

    public void SaveSettings()
    {
        if (!_outputIsTemporary)
        {
            _settings.OutputDirectory = _outputDirectory;
        }

        _settings.BitrateKbps = _bitrateKbps;
        _settings.VoiceMode = _voiceMode;
        _settings.VolumePercent = _volumePercent;
        _settings.Normalize = _normalize;
        _settings.Save();
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    public void AppendLog(string line)
    {
        _ui.Post(_ =>
        {
            _log.AppendLine(line);
            if (_log.Length > 60_000)
            {
                _log.Remove(0, _log.Length - 40_000);
            }

            Raise(nameof(LogText));
        }, null);
    }

    private void RaiseTrim()
    {
        Raise(nameof(TrimError));
        Raise(nameof(HasTrimError));
        RaiseCanConvert();
    }

    private void RaiseCanConvert() => Raise(nameof(CanConvert));

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (JobItem job in e.NewItems)
            {
                job.PropertyChanged += OnJobPropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (JobItem job in e.OldItems)
            {
                job.PropertyChanged -= OnJobPropertyChanged;
            }
        }

        Raise(nameof(IsEmpty));
        RaiseCanConvert();
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobItem.State))
        {
            RaiseCanConvert();
        }
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int mod100 = n % 100;
        int mod10 = n % 10;
        if (mod100 is >= 11 and <= 14)
        {
            return many;
        }

        return mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
    }
}
