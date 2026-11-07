using System.Globalization;
using System.Text;
using Avalonia.Threading;

namespace GigaTTS;

public sealed class AppLog : Observable
{
    public static AppLog Shared { get; } = new();

    readonly object _gate = new();
    string _text = "";

    public string FilePath { get; }

    public string Text
    {
        get => _text;
        private set => Set(ref _text, value);
    }

    AppLog()
    {
        var directory = Path.Combine(AppPaths.Support, "logs");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "gigatts.log");
        if (File.Exists(FilePath))
        {
            var existing = File.ReadAllText(FilePath);
            _text = existing.Length > 60_000 ? existing[^60_000..] : existing;
        }
    }

    public void Write(string level, string message)
    {
        var clean = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0)
            return;
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture)} {level} {clean}\n";
        lock (_gate)
            Append(line);
        RunOnUi(() =>
        {
            var next = Text + line;
            Text = next.Length > 80_000 ? next[^80_000..] : next;
        });
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { File.WriteAllText(FilePath, ""); }
            catch { /* The next line still records that the log was cleared. */ }
        }
        RunOnUi(() => Text = "");
        Write("INFO", "Журнал очищен.");
    }

    void Append(string line)
    {
        try
        {
            File.AppendAllText(FilePath, line, Encoding.UTF8);
            var info = new FileInfo(FilePath);
            if (info.Length <= 4_000_000)
                return;
            var bytes = File.ReadAllBytes(FilePath);
            var kept = bytes.Length > 1_000_000 ? bytes[^1_000_000..] : bytes;
            File.WriteAllBytes(FilePath, kept);
        }
        catch
        {
            // Logging must not break dictation.
        }
    }

    static void RunOnUi(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                action();
            else
                Dispatcher.UIThread.Post(action);
        }
        catch
        {
            action();
        }
    }
}
