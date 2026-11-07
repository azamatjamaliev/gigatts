using Avalonia.Threading;

namespace GigaTTS;

interface IPlayback
{
    bool IsPlaying { get; }
    event Action? Ended;
    void Start(IReadOnlyList<string> paths);
    void Stop();
}

public sealed class CallPlayer : Observable
{
    readonly IPlayback _playback;
    bool _playing;

    public CallPlayer()
    {
        _playback = OperatingSystem.IsWindows() ? CreateWindows() : CreateLinux();
        _playback.Ended += () => Dispatcher.UIThread.Post(() =>
        {
            if (!IsPlaying)
                return;
            Stop();
        });
    }

    public bool IsPlaying
    {
        get => _playing;
        private set => Set(ref _playing, value);
    }

    public void Toggle(params string[] paths)
    {
        if (IsPlaying)
            Stop();
        else
            Start(paths);
    }

    public void Start(IEnumerable<string> paths)
    {
        Stop();
        _playback.Start(paths.Where(File.Exists).ToArray());
        IsPlaying = _playback.IsPlaying;
    }

    public void Stop()
    {
        _playback.Stop();
        IsPlaying = false;
    }

    static IPlayback CreateWindows() => new WindowsPlayback();

    static IPlayback CreateLinux() => new LinuxPlayback();
}
