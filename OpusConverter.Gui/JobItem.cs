namespace OpusConverter.Gui;

public enum JobState
{
    Waiting,
    Working,
    Done,
    Failed,
    Cancelled,
}

/// <summary>One line of the queue: a file, a folder entry or a link, and everything the list shows about it.</summary>
public sealed class JobItem : ObservableObject
{
    private string _name;
    private JobState _state = JobState.Waiting;
    private string _statusText = "В очереди";
    private double _progress;
    private bool _indeterminate;
    private string _info = "";
    private string? _error;
    private bool _isPlaying;

    public JobItem(string source)
    {
        Source = source;
        IsUrl = InputResolver.IsUrl(source);
        _name = InputResolver.SuggestName(source);
    }

    public string Source { get; }

    public bool IsUrl { get; }

    /// <summary>Segoe MDL2 glyph: a globe for links, a music note for files.</summary>
    public string Icon => IsUrl ? "" : "";

    /// <summary>Output file name without extension; the user can edit it, it is what /vplay takes in game.</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public JobState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(IsEditable));
                Raise(nameof(CanPreview));
                Raise(nameof(ShowProgress));
                Raise(nameof(CanRetry));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>0..1 across the whole job (download + conversion).</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public bool IsIndeterminate
    {
        get => _indeterminate;
        private set => Set(ref _indeterminate, value);
    }

    /// <summary>Result summary such as "3:21 · 1 240 КБ".</summary>
    public string Info
    {
        get => _info;
        private set => Set(ref _info, value);
    }

    public string? ErrorDetail
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    public ConversionResult? Result { get; private set; }

    public bool IsEditable => State != JobState.Working;

    public bool ShowProgress => State == JobState.Working;

    public bool CanPreview => State == JobState.Done && Result != null;

    public bool CanRetry => State is JobState.Done or JobState.Failed or JobState.Cancelled;

    public bool NeedsWork => State is JobState.Waiting or JobState.Failed or JobState.Cancelled;

    public void Begin()
    {
        Result = null;
        ErrorDetail = null;
        Info = "";
        Progress = 0;
        IsIndeterminate = true;
        StatusText = "Подготовка…";
        State = JobState.Working;
    }

    public void ApplyProgress(ConversionProgress p)
    {
        // A link spends the first 30% of the bar on the download, a local file goes straight to conversion.
        double encodingStart = IsUrl ? 0.3 : 0.0;
        switch (p.Stage)
        {
            case ConversionStage.Downloading:
                StatusText = p.Fraction is double d ? $"Загрузка… {(int)(d * 100)}%" : "Загрузка…";
                Progress = (p.Fraction ?? 0) * encodingStart;
                IsIndeterminate = p.Fraction is null;
                break;
            case ConversionStage.Analyzing:
                StatusText = "Анализ файла…";
                Progress = encodingStart;
                IsIndeterminate = true;
                break;
            case ConversionStage.Encoding:
                double f = p.Fraction ?? 0;
                StatusText = p.Fraction is null ? "Кодирование…" : $"Кодирование… {(int)(f * 100)}%";
                Progress = encodingStart + f * (1 - encodingStart);
                IsIndeterminate = p.Fraction is null;
                break;
        }
    }

    public void Complete(ConversionResult result)
    {
        Result = result;
        Progress = 1;
        IsIndeterminate = false;
        Info = $"{FormatDuration(result.File.Duration)} · {Math.Max(1, result.OutputBytes / 1024)} КБ";
        StatusText = "Готово";
        State = JobState.Done;
    }

    public void Fail(string message, string? detail)
    {
        Result = null;
        IsIndeterminate = false;
        Progress = 0;
        StatusText = message;
        ErrorDetail = detail ?? message;
        State = JobState.Failed;
    }

    public void MarkCancelled()
    {
        IsIndeterminate = false;
        Progress = 0;
        StatusText = "Отменено";
        State = JobState.Cancelled;
    }

    public void Reset()
    {
        Result = null;
        ErrorDetail = null;
        Info = "";
        Progress = 0;
        IsIndeterminate = false;
        StatusText = "В очереди";
        State = JobState.Waiting;
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
