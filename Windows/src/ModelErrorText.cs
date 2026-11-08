namespace GigaTTS;

public static class ModelErrorText
{
    public const string ClockHint =
        "Проверьте дату и время Windows и нажмите «Повторить загрузку» после обновления приложения.";

    public static string MergeFailure(string logTail, string exceptionMessage)
    {
        var shown = logTail.Length > 0 ? logTail : exceptionMessage;
        if (exceptionMessage.Contains("CERTIFICATE_VERIFY_FAILED", StringComparison.Ordinal)
            && !shown.Contains("CERTIFICATE_VERIFY_FAILED", StringComparison.Ordinal))
            shown = shown.TrimEnd() + "\n" + exceptionMessage;
        return shown;
    }

    public static string WithClockHint(string text, bool onWindows)
    {
        if (!onWindows || text.Length == 0)
            return text;
        if (!text.Contains("CERTIFICATE_VERIFY_FAILED", StringComparison.Ordinal))
            return text;
        if (text.Contains("дату и время Windows", StringComparison.Ordinal))
            return text;
        return text.TrimEnd() + "\n" + ClockHint;
    }

    // The settings «Скопировать» button copies this whole string.
    public static string CopyPayload(string statusDetail) => statusDetail;
}
