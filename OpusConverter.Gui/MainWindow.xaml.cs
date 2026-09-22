using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace OpusConverter.Gui;

public partial class MainWindow : Window
{
    private const string FileFilter =
        "Аудио и видео|*.mp3;*.wav;*.ogg;*.oga;*.opus;*.flac;*.m4a;*.aac;*.wma;*.aif;*.aiff;*.mka;*.mp4;*.mkv;*.avi;*.mov;*.webm;*.flv;*.wmv;*.m4v;*.3gp;*.ts;*.mpg;*.mpeg|Все файлы|*.*";

    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SyncChips();

        // The window has no native handle yet at construction time (ThemeManager.Apply, run from the view model's
        // constructor above, could only theme the palette) - color the title bar itself once one exists.
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this, ThemeManager.IsDark);
    }

    // ---- startup / shutdown --------------------------------------------------------------------------------------------

    /// <summary>
    /// Files, folders and links passed on the command line are queued (so "Open with" and drag-onto-exe work).
    /// --convert starts right away, --output &lt;folder&gt; picks the folder, --close-when-done exits afterwards.
    /// </summary>
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var paths = new List<string>();
        bool convert = false;
        bool closeWhenDone = false;

        string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--convert":
                    convert = true;
                    break;
                case "--close-when-done":
                    closeWhenDone = true;
                    break;
                case "--output" when i + 1 < args.Length:
                    _vm.OverrideOutputDirectory(args[++i]);
                    break;
                default:
                    paths.Add(args[i]);
                    break;
            }
        }

        if (paths.Count > 0)
        {
            _vm.AddPaths(paths);
        }

        if (convert)
        {
            await _vm.ConvertAllAsync();
            if (closeWhenDone)
            {
                Close();
            }
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_vm.IsBusy)
        {
            MessageBoxResult answer = MessageBox.Show(this, "Идёт конвертация. Отменить её и закрыть программу?", "Opus Converter",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _vm.Cancel();
        }

        _vm.StopPreview();
        _vm.SaveSettings();
    }

    /// <summary>Radio buttons are plain toggles; this mirrors the saved settings into them once at startup.</summary>
    private void SyncChips()
    {
        ModeMusic.IsChecked = _vm.MusicMode;
        ModeVoice.IsChecked = _vm.VoiceMode;
    }

    // ---- adding sources ------------------------------------------------------------------------------------------------

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = FileFilter, Title = "Выберите аудио или видео" };
        if (dialog.ShowDialog(this) == true)
        {
            _vm.AddPaths(dialog.FileNames);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка с аудио или видео" };
        if (dialog.ShowDialog(this) == true)
        {
            int added = _vm.AddPaths(new[] { dialog.FolderName });
            if (added == 0)
            {
                MessageBox.Show(this, "В этой папке нет аудио или видео файлов.", "Opus Converter", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void AddLink_Click(object sender, RoutedEventArgs e)
    {
        string text = LinkBox.Text;
        if (string.IsNullOrWhiteSpace(text) && Clipboard.ContainsText())
        {
            text = Clipboard.GetText();
        }

        if (_vm.AddLink(text))
        {
            LinkBox.Clear();
        }
    }

    private void LinkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddLink_Click(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+V anywhere outside a text box adds copied files or a copied link.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBox || !_vm.CanEdit)
        {
            return;
        }

        if (Clipboard.ContainsFileDropList())
        {
            _vm.AddPaths(Clipboard.GetFileDropList().Cast<string>());
            e.Handled = true;
        }
        else if (Clipboard.ContainsText())
        {
            _vm.AddPaths(Clipboard.GetText().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(InputResolver.IsUrl));
            e.Handled = true;
        }
    }

    private static bool HasDroppable(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool ok = _vm.CanEdit && HasDroppable(e.Data);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!_vm.CanEdit)
        {
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _vm.AddPaths(files);
        }
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text || e.Data.GetData(DataFormats.Text) is string text2 && (text = text2) != null)
        {
            _vm.AddPaths(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    // ---- list actions --------------------------------------------------------------------------------------------------

    private static JobItem? JobOf(object sender) => (sender as FrameworkElement)?.DataContext as JobItem;

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { } job)
        {
            try
            {
                await _vm.PreviewAsync(job);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
            {
                MessageBox.Show(this, "Не удалось воспроизвести: " + ex.Message, "Opus Converter", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { } job)
        {
            _vm.Retry(job);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { } job)
        {
            _vm.Remove(job);
        }
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e) => _vm.ClearFinished();

    // ---- settings ------------------------------------------------------------------------------------------------------

    private void Mode_Click(object sender, RoutedEventArgs e) => _vm.VoiceMode = ReferenceEquals(sender, ModeVoice);

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Куда сохранять .rvoice", InitialDirectory = Directory.Exists(_vm.OutputDirectory) ? _vm.OutputDirectory : null };
        if (dialog.ShowDialog(this) == true)
        {
            _vm.OutputDirectory = dialog.FolderName;
        }
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_vm.OutputDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_vm.OutputDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            MessageBox.Show(this, "Не удалось открыть папку: " + ex.Message, "Opus Converter", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChooseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ffmpeg|ffmpeg.exe|Программы|*.exe", Title = "Укажите ffmpeg.exe" };
        if (dialog.ShowDialog(this) == true)
        {
            _vm.SetFfmpeg(dialog.FileName);
            if (_vm.FfmpegMissing)
            {
                MessageBox.Show(this, "По этому пути ffmpeg не найден.", "Opus Converter", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                _vm.SaveSettings();
            }
        }
    }

    private void FfmpegHelp_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://www.gyan.dev/ffmpeg/builds/") { UseShellExecute = true });

    // ---- run -----------------------------------------------------------------------------------------------------------

    private async void Convert_Click(object sender, RoutedEventArgs e) => await _vm.ConvertAllAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _vm.Cancel();

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
