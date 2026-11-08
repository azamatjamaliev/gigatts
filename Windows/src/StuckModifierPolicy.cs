namespace GigaTTS;

public readonly record struct StuckModifierDecision(bool AppRelease, IReadOnlyList<ushort> InjectKeyUps);

// Pure stuck-modifier decisions. Win32 (GetAsyncKeyState / SendInput) stays in HotkeyMonitor.
public static class StuckModifierPolicy
{
    public static readonly TimeSpan WatchdogInterval = TimeSpan.FromMilliseconds(300);

    static readonly ushort[] TrackedModifiers =
    {
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0x10, 0x11, 0x12
    };

    public static bool ShouldSynthesizeAppRelease(bool held, bool operatingSystemDown) =>
        held && !operatingSystemDown;

    // Watchdog order: if the tracker still holds a key the OS already released, synthesize
    // that release and do not inject a keyup for it. Then inject keyups only for swallowed
    // modifiers that are logically up while the OS still reports them down.
    public static StuckModifierDecision Decide(
        bool held,
        bool capturing,
        ushort hotkey,
        IReadOnlySet<ushort> swallowed,
        IReadOnlySet<ushort> logicallyDown,
        IReadOnlySet<ushort> genuine,
        Func<ushort, bool> operatingSystemDown)
    {
        var appRelease = ShouldSynthesizeAppRelease(held, operatingSystemDown(hotkey));
        var down = logicallyDown;
        if (appRelease)
        {
            held = false;
            var updated = new HashSet<ushort>(logicallyDown);
            updated.Remove(hotkey);
            var generic = GenericOf(hotkey);
            if (generic != 0)
                updated.Remove(generic);
            down = updated;
        }

        var inject = new List<ushort>();
        foreach (var vk in KeysToRelease(false, held, capturing, swallowed, down, genuine, operatingSystemDown))
        {
            if (!TrackerAllowsInject(vk, false, held, capturing, down, swallowed, genuine))
                continue;
            if (!operatingSystemDown(vk))
                continue;
            inject.Add(vk);
        }
        return new StuckModifierDecision(appRelease, inject);
    }

    public static bool TrackerAllowsInject(
        ushort virtualKey,
        bool force,
        bool held,
        bool capturing,
        IReadOnlySet<ushort> logicallyDown,
        IReadOnlySet<ushort> swallowed,
        IReadOnlySet<ushort> genuine)
    {
        if (!force && (held || capturing || logicallyDown.Contains(virtualKey)))
            return false;
        if (!swallowed.Contains(virtualKey) && genuine.Contains(virtualKey))
            return false;
        return true;
    }

    internal static List<ushort> KeysToRelease(
        bool force,
        bool held,
        bool capturing,
        IReadOnlySet<ushort> swallowed,
        IReadOnlySet<ushort> logicallyDown,
        IReadOnlySet<ushort> genuine,
        Func<ushort, bool> operatingSystemDown)
    {
        if (!force && (held || capturing))
            return new List<ushort>();

        bool LogicallyUp(ushort vk) => force || !logicallyDown.Contains(vk);

        bool IsSwallowedStuck(ushort vk) =>
            swallowed.Contains(vk) && LogicallyUp(vk) && operatingSystemDown(vk);

        bool GenuineDown(ushort vk) =>
            vk != 0 && genuine.Contains(vk) && operatingSystemDown(vk);

        bool UnconsumedDown(ushort vk) =>
            vk != 0 && !swallowed.Contains(vk) && !logicallyDown.Contains(vk) && operatingSystemDown(vk);

        bool ExplainedByUnconsumedSide(ushort generic)
        {
            var (left, right) = Sides(generic);
            if (left == 0)
                return false;
            if (GenuineDown(left) || GenuineDown(right))
                return true;
            if (IsSwallowedStuck(left) || IsSwallowedStuck(right))
                return false;
            return UnconsumedDown(left) || UnconsumedDown(right);
        }

        bool AltFamilyStuck() =>
            IsSwallowedStuck(0xA4) || IsSwallowedStuck(0xA5)
            || (IsSwallowedStuck(0x12) && !ExplainedByUnconsumedSide(0x12));

        bool SiblingAltStuck(ushort vk) =>
            vk == 0xA4 ? IsSwallowedStuck(0xA5) : vk == 0xA5 && IsSwallowedStuck(0xA4);

        var result = new List<ushort>();
        foreach (var vk in TrackedModifiers)
        {
            if (!IsSwallowedStuck(vk))
                continue;
            if (IsGeneric(vk) && ExplainedByUnconsumedSide(vk))
                continue;
            result.Add(vk);
        }

        if (!AltFamilyStuck())
            return result;

        foreach (var vk in new ushort[] { 0xA4, 0xA5, 0x12 })
        {
            if (result.Contains(vk) || !LogicallyUp(vk) || !operatingSystemDown(vk) || genuine.Contains(vk))
                continue;
            if (vk == 0x12)
            {
                if (!ExplainedByUnconsumedSide(0x12))
                    result.Add(vk);
                continue;
            }
            if (swallowed.Contains(vk) || SiblingAltStuck(vk))
                result.Add(vk);
        }
        return result;
    }

    internal static ushort GenericOf(ushort virtualKey) => virtualKey switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => 0
    };

    internal static (ushort Left, ushort Right) Sides(ushort generic) => generic switch
    {
        0x10 => ((ushort)0xA0, (ushort)0xA1),
        0x11 => ((ushort)0xA2, (ushort)0xA3),
        0x12 => ((ushort)0xA4, (ushort)0xA5),
        _ => ((ushort)0, (ushort)0)
    };

    internal static bool IsGeneric(ushort virtualKey) => virtualKey is 0x10 or 0x11 or 0x12;
}
