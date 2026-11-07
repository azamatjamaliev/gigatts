using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace GigaTTS;

public partial class PermissionView : UserControl
{
    AppState? _state;

    public PermissionView()
    {
        InitializeComponent();
        if (!OperatingSystem.IsWindows())
        {
            MicButton.Content = "Открыть";
            AudioButton.Content = "Открыть";
        }
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
        Mark(MicMark, MicButton, _state.MicrophoneAllowed);
        Mark(HookMark, HookButton, _state.HotkeyAllowed);
        Mark(AudioMark, AudioButton, _state.SystemAudioAllowed);
        var failure = _state.HotkeyFailure;
        Hint.IsVisible = !_state.HotkeyAllowed && !string.IsNullOrEmpty(failure);
        Hint.Text = failure ?? "";
    }

    static void Mark(TextBlock mark, Button button, bool granted)
    {
        mark.Text = granted ? "✓" : "○";
        mark.Foreground = granted ? Brushes.Green : Brushes.Gray;
        button.IsVisible = !granted;
    }

    void OnMic(object? sender, RoutedEventArgs e) => _state?.RequestMicrophone();
    void OnHook(object? sender, RoutedEventArgs e) => _state?.RetryHotkey();
    void OnAudio(object? sender, RoutedEventArgs e) => _state?.RequestSystemAudio();

    void OnRefresh(object? sender, RoutedEventArgs e)
    {
        _state?.RefreshPermissions();
        _state?.RefreshDevices();
    }
}
