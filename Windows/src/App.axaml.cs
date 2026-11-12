using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace GigaTTS;

public partial class App : Application
{
    AppState? _state;
    TrayIcon? _tray;
    MainWindow? _main;
    SettingsWindow? _settings;
    OnboardingWindow? _onboarding;

    public static WindowIcon? SharedIcon { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        SharedIcon = LoadIcon();
        _state = new AppState();
        _state.MenuChanged += (_, _) => RebuildMenu();
        _state.OnNeedOnboarding = ShowOnboarding;
        _state.OnOpenCalls = () => ShowMain();
        _state.OnOpenSettings = ShowSettings;
        _state.PickMeetingFileAsync = PickMeetingFileAsync;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => _state.Shutdown();
        }
        SetupTray();
        base.OnFrameworkInitializationCompleted();
        _state.Bootstrap();
        RebuildMenu();
    }

    void SetupTray()
    {
        _tray = new TrayIcon
        {
            Icon = SharedIcon,
            ToolTipText = "GigaПисец",
            IsVisible = true
        };
        _tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    void RebuildMenu()
    {
        if (_state == null || _tray == null)
            return;
        var state = _state;
        var menu = new NativeMenu();
        menu.Add(Item(state.ModelTitle, enabled: false));
        if (!state.ModelReady && state.StatusDetail.Length > 0)
            menu.Add(Item(state.StatusDetail, enabled: false));
        menu.Add(new NativeMenuItemSeparator());
        if (!state.ModelReady)
        {
            var title = state.IsInstalling ? "Установка распознавания…" : "Установить распознавание";
            menu.Add(Item(title, () =>
            {
                state.OpenSettings();
                state.InstallWorker();
            }, state.CanInstall));
        }
        menu.Add(Item(state.DictationButtonText, state.ToggleHandsFree, state.CanDictate));
        menu.Add(Item(state.CallButtonText, state.ToggleCall, state.CanToggleCall));
        menu.Add(Item("Открыть запись встречи…", state.ImportMeetingRecording, state.CanImportMeeting));
        if (state.LastTranscript.Length > 0)
        {
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(Item("Скопировать последнюю фразу", state.CopyLastTranscript));
        }
        if (state.History.Count > 0)
        {
            var recent = new NativeMenuItem { Header = "Недавние" };
            var submenu = new NativeMenu();
            foreach (var entry in state.History.Take(8))
            {
                var captured = entry;
                submenu.Add(Item(captured.Preview, () => state.InsertHistory(captured)));
            }
            recent.Menu = submenu;
            menu.Add(recent);
        }
        if (!string.IsNullOrEmpty(state.LastError))
        {
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(Item(state.LastError, enabled: false));
        }
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("GigaПисец…", () =>
        {
            state.ShowSection("calls");
        }));
        menu.Add(Item("Настройки…", state.OpenSettings));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Выйти", Quit));
        _tray.Menu = menu;
        _tray.ToolTipText = state.Phase switch
        {
            Phase.Dictating => "GigaПисец — диктовка",
            Phase.Transcribing => "GigaПисец — распознавание",
            Phase.RecordingCall => "GigaПисец — запись звонка",
            Phase.TranscribingCall => "GigaПисец — расшифровка звонка",
            _ => "GigaПисец"
        };
    }

    void ShowMain()
    {
        if (_state == null)
            return;
        if (_main == null)
        {
            _main = new MainWindow(_state);
            _main.Closed += (_, _) => _main = null;
        }
        _main.Show();
        _main.Activate();
        _main.ApplySection();
    }

    async Task<string?> PickMeetingFileAsync()
    {
        ShowMain();
        if (_main == null)
            return null;
        return await _main.PickMeetingFileAsync();
    }

    void ShowSettings()
    {
        if (_state == null)
            return;
        if (_settings == null)
        {
            _settings = new SettingsWindow(_state);
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Show();
        _settings.Activate();
    }

    void ShowOnboarding()
    {
        if (_state == null)
            return;
        if (_onboarding == null)
        {
            _onboarding = new OnboardingWindow(_state);
            _onboarding.Closed += (_, _) => _onboarding = null;
        }
        _onboarding.Show();
        _onboarding.Activate();
    }

    void Quit()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    static NativeMenuItem Item(string header, Action? action = null, bool enabled = true)
    {
        var item = new NativeMenuItem
        {
            Header = header.Replace("_", "__").Replace("&", "&&"),
            IsEnabled = enabled && action != null
        };
        if (action != null && enabled)
            item.Click += (_, _) => action();
        return item;
    }

    static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://GigaTTS/Assets/icon.png"));
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }
}
