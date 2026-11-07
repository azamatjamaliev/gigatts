using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace GigaTTS;

public partial class MainWindow : Window
{
    readonly AppState _state;
    readonly CallPlayer _player = new();
    CallRecord? _selected;

    public MainWindow(AppState state)
    {
        _state = state;
        InitializeComponent();
        Icon = App.SharedIcon;
        CallList.ItemsSource = _state.Calls;
        CallList.SelectionChanged += (_, _) =>
        {
            if (!_state.SuppressCallSelection)
                ShowCall(CallList.SelectedItem as CallRecord);
        };
        _state.CallsReloaded += (_, _) => RestoreCall();
        _state.PropertyChanged += (_, _) => Apply();
        _player.PropertyChanged += (_, _) => Apply();
        AppLog.Shared.PropertyChanged += (_, _) => ShowLog();
        Opened += (_, _) =>
        {
            RestoreCall();
            Apply();
            ShowLog();
        };
        Apply();
        ShowLog();
    }

    public void ApplySection()
    {
        var calls = _state.Section != "debug";
        CallsPage.IsVisible = calls;
        DebugPage.IsVisible = !calls;
        CallsTab.FontWeight = calls ? FontWeight.SemiBold : FontWeight.Normal;
        DebugTab.FontWeight = calls ? FontWeight.Normal : FontWeight.SemiBold;
    }

    void Apply()
    {
        ApplySection();
        var call = _selected;
        var hasAudio = call != null && (File.Exists(call.MicPath) || File.Exists(call.SystemPath));
        ListenButton.Content = _player.IsPlaying ? "Стоп" : "Слушать";
        ListenButton.IsEnabled = hasAudio;
        AgainButton.IsEnabled = call != null && _state.Phase == Phase.Idle && _state.ModelReady;
        ExportButton.IsEnabled = call != null && call.Segments.Count > 0;
        var hasCapture = !string.IsNullOrEmpty(_state.LastCapturePath);
        PlayLastButton.IsEnabled = hasCapture;
        RecognizeButton.IsEnabled = hasCapture && _state.ModelReady && _state.Phase == Phase.Idle;
        OpenWavButton.IsEnabled = _state.ModelReady && _state.Phase == Phase.Idle;
        InsertButton.IsEnabled = _state.DebugResult.Trim().Length > 0;
        DebugPhase.Text = _state.PhaseTitle;
        DebugModel.Text = "Модель: " + _state.ModelTitle;
        DebugError.Text = _state.LastError ?? "";
        DebugError.IsVisible = !string.IsNullOrEmpty(_state.LastError);
        DebugCapture.Text = "Последняя запись: " + _state.CaptureSummary;
        DebugResult.Text = _state.DebugResult;
        ResultFrame.IsVisible = _state.DebugResult.Length > 0;
        if (call != null && Detail.IsVisible)
        {
            DetailTitle.Text = call.Title;
            DetailLine.Text = call.CreatedAt.ToString("g") + " · " + Format.Minutes(call.Duration);
            SystemNote.IsVisible = !call.HasSystemAudio;
            var hasText = call.Segments.Count > 0;
            NoTranscript.IsVisible = !hasText;
            TranscriptScroll.IsVisible = hasText;
        }
    }

    bool _updating;

    void RestoreCall()
    {
        var id = _selected?.Id;
        var match = id == null ? null : _state.Calls.FirstOrDefault(call => call.Id == id);
        ShowCall(match ?? _state.Calls.FirstOrDefault());
    }

    void ShowCall(CallRecord? call)
    {
        if (_updating)
            return;
        _updating = true;
        try
        {
            if (!ReferenceEquals(_selected, call))
                _player.Stop();
            _selected = call;
            if (CallList.SelectedItem != call)
                CallList.SelectedItem = call;
            EmptyState.IsVisible = call == null;
            Detail.IsVisible = call != null;
            if (call != null)
                Segments.ItemsSource = call.Segments;
            Apply();
        }
        finally
        {
            _updating = false;
        }
    }

    void OnCallsTab(object? sender, RoutedEventArgs e) => _state.ShowSection("calls");
    void OnDebugTab(object? sender, RoutedEventArgs e) => _state.ShowSection("debug");
    void OnSettings(object? sender, RoutedEventArgs e) => _state.OpenSettings();

    void OnListen(object? sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;
        _player.Toggle(_selected.MicPath, _selected.SystemPath);
    }

    void OnAgain(object? sender, RoutedEventArgs e)
    {
        if (_selected != null)
            _state.Retranscribe(_selected);
    }

    async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт расшифровки",
            SuggestedFileName = "giga-call.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Текст") { Patterns = ["*.txt"] }]
        });
        if (file == null)
            return;
        var path = file.TryGetLocalPath();
        if (path != null)
        {
            await File.WriteAllTextAsync(path, _selected.PlainTranscript);
            return;
        }
        await using var stream = await file.OpenWriteAsync();
        using var writer = new StreamWriter(stream);
        await writer.WriteAsync(_selected.PlainTranscript);
    }

    async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;
        if (!await ConfirmWindow.Ask(this, "Удалить эту запись?", "Запись и расшифровка будут удалены.", "Удалить"))
            return;
        _player.Stop();
        _state.DeleteCall(_selected);
    }

    void OnPlayLast(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_state.LastCapturePath))
            _player.Toggle(_state.LastCapturePath);
    }

    void OnRecognize(object? sender, RoutedEventArgs e) => _state.RetranscribeLastCapture();

    async void OnOpenWav(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Открыть WAV",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Аудио") { Patterns = ["*.wav"] }]
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
            _state.TranscribeFile(path);
    }

    void OnInsert(object? sender, RoutedEventArgs e) => _state.InsertDebugResult();

    void OnCopyLog(object? sender, RoutedEventArgs e) => PlatformClipboard.TrySetText(AppLog.Shared.Text);

    void OnRevealLog(object? sender, RoutedEventArgs e) => PlatformServices.Reveal(AppLog.Shared.FilePath);

    void OnClearLog(object? sender, RoutedEventArgs e) => AppLog.Shared.Clear();

    void ShowLog()
    {
        LogPath.Text = AppLog.Shared.FilePath;
        var text = AppLog.Shared.Text.Length == 0 ? "Журнал пуст." : AppLog.Shared.Text;
        if (LogBox.Text == text)
            return;
        LogBox.Text = text;
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
    }
}
