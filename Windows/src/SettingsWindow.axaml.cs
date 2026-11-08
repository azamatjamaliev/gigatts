using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace GigaTTS;

public partial class SettingsWindow : Window
{
    readonly AppState _state;
    bool _applying;
    DispatcherTimer? _copyTimer;

    public SettingsWindow(AppState state)
    {
        _state = state;
        DataContext = state;
        InitializeComponent();
        Icon = App.SharedIcon;
        if (!OperatingSystem.IsWindows())
            LoginBox.Content = "Запускать при входе в систему";
        ModelDeviceBox.Items.Add(new ComboBoxItem { Content = "Процессор", Tag = "cpu" });
        ModelDeviceBox.Items.Add(new ComboBoxItem { Content = "Видеокарта NVIDIA", Tag = "cuda" });
        ModelDeviceBox.SelectionChanged += (_, _) =>
        {
            if (_applying || ModelDeviceBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
                return;
            _state.SetModelDevice(tag);
        };
        MicBox.SelectionChanged += (_, _) =>
        {
            if (_applying || MicBox.SelectedItem is not ComboBoxItem item || item.Tag is not string id)
                return;
            _state.SetDeviceId(id);
        };
        LoginBox.PropertyChanged += (_, e) =>
        {
            if (_applying || e.Property != CheckBox.IsCheckedProperty)
                return;
            _state.SetLaunchAtLogin(LoginBox.IsChecked == true);
        };
        _state.PropertyChanged += (_, _) => Apply();
        _state.Devices.CollectionChanged += (_, _) => RebuildMics();
        Opened += (_, _) =>
        {
            _state.RefreshPermissions();
            _state.RefreshDevices();
            _state.RefreshLogin();
            RebuildMics();
            Apply();
        };
        RebuildMics();
        Apply();
    }

    void Apply()
    {
        _applying = true;
        HotkeyLabel.Text = _state.HotkeyText;
        CaptureButton.Content = _state.IsCapturingHotkey ? "Жду клавишу" : "Изменить";
        CaptureButton.IsEnabled = _state.Phase == Phase.Idle;
        ModelDeviceBox.IsEnabled = _state.Phase == Phase.Idle && !_state.IsInstalling;
        Select(ModelDeviceBox, _state.ModelDevice);
        Select(MicBox, _state.SelectedDeviceId);
        ReloadButton.IsVisible = _state.ModelKind == ModelKind.Failed;
        LoginBox.IsChecked = _state.LaunchAtLogin;
        var failed = _state.ModelKind == ModelKind.Failed;
        var hasError = failed && _state.StatusDetail.Length > 0;
        CopyErrorButton.IsVisible = hasError;
        ErrorScroll.IsVisible = hasError;
        StatusLine.IsVisible = !hasError;
        if (failed)
        {
            ModelBadge.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xD2, 0xD2));
            ModelBadge.Padding = new Thickness(8, 2);
            ModelTitleText.Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0x1D, 0x1D));
        }
        else
        {
            ModelBadge.Background = Brushes.Transparent;
            ModelBadge.Padding = new Thickness(0);
            ModelTitleText.ClearValue(TextBlock.ForegroundProperty);
        }
        if (hasError)
        {
            if (ErrorText.Text != _state.StatusDetail)
                ErrorText.Text = _state.StatusDetail;
        }
        else
        {
            _copyTimer?.Stop();
            CopyErrorButton.Content = "Скопировать";
            ErrorText.Text = "";
        }
        _applying = false;
    }

    void RebuildMics()
    {
        _applying = true;
        MicBox.Items.Clear();
        foreach (var device in _state.Devices)
            MicBox.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
        Select(MicBox, _state.SelectedDeviceId);
        _applying = false;
    }

    static void Select(ComboBox box, string tag)
    {
        foreach (var entry in box.Items)
        {
            if (entry is ComboBoxItem item && item.Tag as string == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.ItemCount > 0)
            box.SelectedIndex = 0;
    }

    void OnCapture(object? sender, RoutedEventArgs e) => _state.BeginHotkeyCapture();
    void OnResetHotkey(object? sender, RoutedEventArgs e) => _state.ResetHotkey();
    void OnReload(object? sender, RoutedEventArgs e) => _state.ReloadModel();

    void OnCopyError(object? sender, RoutedEventArgs e)
    {
        var text = ModelErrorText.CopyPayload(_state.StatusDetail);
        if (text.Length == 0 || !PlatformClipboard.TrySetText(text))
            return;
        CopyErrorButton.Content = "Скопировано";
        _copyTimer?.Stop();
        _copyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _copyTimer.Tick += (_, _) =>
        {
            _copyTimer?.Stop();
            CopyErrorButton.Content = "Скопировать";
        };
        _copyTimer.Start();
    }
}
