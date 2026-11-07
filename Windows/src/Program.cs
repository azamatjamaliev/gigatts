using Avalonia;
using Avalonia.Controls;

namespace GigaTTS;

static class Program
{
    static FileStream? _linuxLock;

    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            using var mutex = new Mutex(true, @"Local\GigaTTS.SingleInstance", out var created);
            if (!created)
                return;
            Start(args);
            return;
        }
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("Это приложение для Windows и Linux.");
            return;
        }
        if (!AcquireLinuxLock())
            return;
        Start(args);
    }

    static void Start(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

    static bool AcquireLinuxLock()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "gigatts-single.lock");
            _linuxLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
