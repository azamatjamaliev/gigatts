namespace GigaTTS.Tests;

public class ModelErrorTextTests
{
    const string Traceback = """
        Traceback (most recent call last):
          File "worker.py", line 70, in load_model
            model = gigaam.load_model(model_name)
        ssl.SSLCertVerificationError: [SSL: CERTIFICATE_VERIFY_FAILED] certificate verify failed
        """;

    [Fact]
    public void Copy_payload_keeps_the_traceback_and_adds_the_clock_hint()
    {
        var shown = ModelErrorText.WithClockHint(ModelErrorText.MergeFailure(Traceback, Traceback), onWindows: true);
        var payload = ModelErrorText.CopyPayload(shown);

        Assert.Equal(shown, payload);
        Assert.Contains("Traceback (most recent call last):", payload);
        Assert.Contains("gigaam.load_model(model_name)", payload);
        Assert.Contains("CERTIFICATE_VERIFY_FAILED", payload);
        Assert.EndsWith(ModelErrorText.ClockHint, payload);
        Assert.Equal(1, Count(payload, ModelErrorText.ClockHint));
    }

    [Fact]
    public void Clock_hint_is_added_when_only_the_exception_mentions_the_certificate()
    {
        const string tail = "Downloading model…";
        const string exception = "ssl.SSLCertVerificationError: CERTIFICATE_VERIFY_FAILED";
        var payload = ModelErrorText.CopyPayload(
            ModelErrorText.WithClockHint(ModelErrorText.MergeFailure(tail, exception), onWindows: true));

        Assert.Contains(tail, payload);
        Assert.Contains(exception, payload);
        Assert.Contains(ModelErrorText.ClockHint, payload);
    }

    [Fact]
    public void Ordinary_errors_are_copied_unchanged()
    {
        const string text = "Traceback (most recent call last):\nRuntimeError: out of memory";
        var shown = ModelErrorText.WithClockHint(text, onWindows: true);

        Assert.Equal(text, shown);
        Assert.Equal(text, ModelErrorText.CopyPayload(shown));
        Assert.DoesNotContain("дату и время Windows", shown);
    }

    [Fact]
    public void Clock_hint_stays_off_when_not_on_windows()
    {
        Assert.Equal(Traceback, ModelErrorText.WithClockHint(Traceback, onWindows: false));
    }

    static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
