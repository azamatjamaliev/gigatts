namespace GigaTTS;

interface ICaptureSession
{
    event Action<string>? Failed;
    bool IsRunning { get; }
    double Duration { get; }
    void StartMicrophone(string path, string deviceId);
    void StartLoopback(string path);
    string? Stop();
}

static class CaptureFactory
{
    public static ICaptureSession Create() =>
        OperatingSystem.IsWindows() ? CreateWindows() : CreateLinux();

    static ICaptureSession CreateWindows() => new CaptureSession();

    static ICaptureSession CreateLinux() => new LinuxCaptureSession();
}
