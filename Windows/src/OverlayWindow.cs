using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GigaTTS;

sealed class OverlayController
{
    OverlayWindow? _window;

    OverlayWindow Window
    {
        get
        {
            if (_window != null)
                return _window;
            _window = new OverlayWindow();
            _window.Closed += (_, _) => _window = null;
            return _window;
        }
    }

    public void ShowDictation() => Window.ShowDictation();
    public void ShowTranscribing() => Window.ShowTranscribing();
    public void ShowCall(string elapsed) => Window.ShowCall(elapsed);
    public void ShowMessage(string text) => Window.ShowMessage(text);
    public void Hide() => _window?.HideOverlay();
}

sealed class OverlayWindow : Window
{
    readonly Ellipse _dot = new()
    {
        Width = 12,
        Height = 12,
        Fill = Brushes.White,
        VerticalAlignment = VerticalAlignment.Center
    };
    readonly TextBlock _label = new()
    {
        Foreground = Brushes.White,
        FontSize = 15,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 360
    };
    readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer _hide = new();
    bool _blinkOn = true;
    bool _message;

    public OverlayWindow()
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        CanResize = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 20, 20, 20)),
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(18, 12),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { _dot, _label }
            }
        };
        _blink.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            _dot.Opacity = _blinkOn ? 1 : 0.25;
        };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            if (_message)
                HideOverlay();
        };
        Opened += (_, _) => Native.MakeClickThrough(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }

    public void ShowDictation()
    {
        _message = false;
        _hide.Stop();
        _dot.IsVisible = true;
        _dot.Fill = Brushes.White;
        _label.Text = "Диктовка";
        _blinkOn = true;
        _dot.Opacity = 1;
        _blink.Start();
        Present();
    }

    public void ShowTranscribing()
    {
        _message = false;
        _hide.Stop();
        _blink.Stop();
        _dot.IsVisible = false;
        _label.Text = "Распознаю…";
        Present();
    }

    public void ShowCall(string elapsed)
    {
        _message = false;
        _hide.Stop();
        _blink.Stop();
        _dot.IsVisible = true;
        _dot.Fill = Brushes.Red;
        _dot.Opacity = 1;
        _label.Text = elapsed + "  Звонок";
        Present();
    }

    public void ShowMessage(string text)
    {
        _message = true;
        _blink.Stop();
        _dot.IsVisible = false;
        _label.Text = text;
        Present();
        _hide.Interval = TimeSpan.FromSeconds(1.6);
        _hide.Stop();
        _hide.Start();
    }

    public void HideOverlay()
    {
        _hide.Stop();
        _blink.Stop();
        _message = false;
        if (IsVisible)
            Hide();
    }

    void Present()
    {
        if (!IsVisible)
            Show();
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Background);
    }

    void Place()
    {
        var screen = Screens?.ScreenFromPoint(CurrentCursor()) ?? Screens?.Primary;
        if (screen == null)
            return;
        var scale = screen.Scaling;
        var width = Bounds.Width > 1 ? Bounds.Width : 240;
        var height = Bounds.Height > 1 ? Bounds.Height : 56;
        var area = screen.WorkingArea;
        var x = area.X + (area.Width - (int)Math.Ceiling(width * scale)) / 2;
        var y = area.Y + area.Height - (int)Math.Ceiling(height * scale) - (int)(28 * scale);
        Position = new PixelPoint(x, y);
    }

    static PixelPoint CurrentCursor() =>
        OperatingSystem.IsWindows() ? Native.Cursor() : LinuxText.Cursor();
}
