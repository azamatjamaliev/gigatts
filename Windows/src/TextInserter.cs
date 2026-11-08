using Avalonia.Threading;

namespace GigaTTS;

static class TextInserter
{
    public static string? LastProblem { get; private set; }

    public static InsertOutcome Insert(string text)
    {
        LastProblem = null;
        if (OperatingSystem.IsLinux())
            return InsertLinux(text);
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return InsertOutcome.Failed;
        if (Native.IsPasswordFocused())
        {
            AppLog.Shared.Write("WARN", "Вставка пропущена: защищённое поле.");
            return InsertOutcome.Secure;
        }
        HotkeyMonitor.ReleaseStuckModifiers();
        if (Native.SendText(trimmed))
        {
            AppLog.Shared.Write("INFO", "Вставка через ввод символов.");
            return InsertOutcome.Inserted;
        }
        return Paste(trimmed) ? InsertOutcome.Inserted : InsertOutcome.Failed;
    }

    static InsertOutcome InsertLinux(string text)
    {
        var outcome = LinuxText.Insert(text, out var problem);
        LastProblem = problem;
        return outcome;
    }

    static bool Paste(string text)
    {
        var knewPrevious = Native.TryGetText(out var previous);
        if (!Native.TrySetText(text))
            return false;
        var marker = Native.GetClipboardSequenceNumber();
        AppLog.Shared.Write("INFO", "Вставка через Ctrl+V.");
        HotkeyMonitor.ReleaseStuckModifiers();
        Native.SendCtrlV();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (Native.GetClipboardSequenceNumber() != marker || !knewPrevious)
                return;
            if (previous.Length == 0)
                Native.TryEmpty();
            else
                Native.TrySetText(previous);
        };
        timer.Start();
        return true;
    }
}
