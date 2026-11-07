using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GigaTTS;

public partial class InstallView : UserControl
{
    AppState? _state;

    public InstallView()
    {
        InitializeComponent();
        if (OperatingSystem.IsLinux())
            Hint.Text = "Ставит Python, Git, ffmpeg, PyTorch и GigaAM в папку данных (~/.local/share/GigaTTS). На Ubuntu кнопка запускает apt и может запросить пароль администратора. Интернет нужен для загрузки. Видеодрайвер NVIDIA этот шаг не ставит.";
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not AppState state || ReferenceEquals(_state, state))
            return;
        if (_state != null)
            _state.PropertyChanged -= OnState;
        _state = state;
        _state.PropertyChanged += OnState;
        Apply();
    }

    void OnState(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Apply();

    void Apply()
    {
        if (_state == null)
            return;
        InstallButton.Content = _state.IsInstalling ? "Установка…" : "Установить распознавание";
        InstallButton.IsEnabled = _state.CanInstall;
        var hasLog = _state.InstallLog.Length > 0;
        LogFrame.IsVisible = hasLog;
        if (LogBox.Text != _state.InstallLog)
        {
            LogBox.Text = _state.InstallLog;
            LogScroll.ScrollToEnd();
        }
    }

    void OnInstall(object? sender, RoutedEventArgs e) => _state?.InstallWorker();
}
