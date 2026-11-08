namespace GigaTTS.Tests;

public class StuckModifierTests
{
    const ushort LeftAlt = 0xA4;
    const ushort Alt = 0x12;
    const ushort LeftCtrl = 0xA2;

    [Fact]
    public void Logically_up_and_os_down_injects_keyup()
    {
        var decision = Decide(
            held: false,
            capturing: false,
            hotkey: LeftAlt,
            swallowed: [LeftAlt, Alt],
            logicallyDown: [],
            genuine: [],
            osDown: [LeftAlt, Alt]);

        Assert.False(decision.AppRelease);
        Assert.Contains(LeftAlt, decision.InjectKeyUps);
    }

    [Fact]
    public void Logically_held_and_os_up_releases_in_the_app_only()
    {
        var decision = Decide(
            held: true,
            capturing: false,
            hotkey: LeftAlt,
            swallowed: [LeftAlt, Alt],
            logicallyDown: [LeftAlt, Alt],
            genuine: [],
            osDown: []);

        Assert.True(decision.AppRelease);
        Assert.DoesNotContain(LeftAlt, decision.InjectKeyUps);
        Assert.Empty(decision.InjectKeyUps);
    }

    [Fact]
    public void Hold_to_talk_blocks_injection_while_the_key_is_still_down()
    {
        var decision = Decide(
            held: true,
            capturing: false,
            hotkey: LeftAlt,
            swallowed: [LeftAlt, Alt, LeftCtrl],
            logicallyDown: [LeftAlt],
            genuine: [],
            osDown: [LeftAlt, LeftCtrl]);

        Assert.False(decision.AppRelease);
        Assert.Empty(decision.InjectKeyUps);
    }

    [Fact]
    public void Hotkey_capture_blocks_injection()
    {
        var decision = Decide(
            held: false,
            capturing: true,
            hotkey: LeftAlt,
            swallowed: [LeftAlt, Alt],
            logicallyDown: [],
            genuine: [],
            osDown: [LeftAlt, Alt]);

        Assert.False(decision.AppRelease);
        Assert.Empty(decision.InjectKeyUps);
    }

    [Fact]
    public void Does_not_release_a_modifier_this_app_did_not_swallow()
    {
        var decision = Decide(
            held: false,
            capturing: false,
            hotkey: LeftAlt,
            swallowed: [LeftAlt, Alt],
            logicallyDown: [],
            genuine: [],
            osDown: [LeftAlt, Alt, LeftCtrl]);

        Assert.Contains(LeftAlt, decision.InjectKeyUps);
        Assert.DoesNotContain(LeftCtrl, decision.InjectKeyUps);
    }

    [Fact]
    public void Watchdog_interval_is_300_milliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300), StuckModifierPolicy.WatchdogInterval);
    }

    [Fact]
    public void Low_level_hook_does_not_send_input()
    {
        var source = File.ReadAllText(RepoFiles.Find("Windows/src/HotkeyMonitor.cs"));
        var hook = source.IndexOf("IntPtr Hook(", StringComparison.Ordinal);
        var pass = source.IndexOf("IntPtr Pass(", StringComparison.Ordinal);
        Assert.True(hook >= 0 && pass > hook);
        var body = source[hook..pass];
        Assert.DoesNotContain("ReleaseVirtualKey", body);
        Assert.DoesNotContain("SendInput", body);
    }

    static StuckModifierDecision Decide(
        bool held,
        bool capturing,
        ushort hotkey,
        ushort[] swallowed,
        ushort[] logicallyDown,
        ushort[] genuine,
        ushort[] osDown)
    {
        var down = new HashSet<ushort>(osDown);
        return StuckModifierPolicy.Decide(
            held,
            capturing,
            hotkey,
            swallowed.ToHashSet(),
            logicallyDown.ToHashSet(),
            genuine.ToHashSet(),
            down.Contains);
    }
}
