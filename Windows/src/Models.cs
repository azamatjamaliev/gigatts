using System.Globalization;
using System.Text.Json.Serialization;

namespace GigaTTS;

[Flags]
public enum KeyMods : uint
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8
}

public enum Phase
{
    Idle,
    Dictating,
    Transcribing,
    RecordingCall,
    TranscribingCall
}

public enum ModelKind
{
    Starting,
    Missing,
    Loading,
    Ready,
    Failed
}

public sealed record AudioDevice(string Id, string Name);

public sealed class Hotkey
{
    public static Hotkey RightAlt { get; } = new(0xA5, true);

    public ushort VirtualKey { get; }
    public bool IsModifier { get; }
    public KeyMods Modifiers { get; }

    public Hotkey(ushort virtualKey, bool isModifier, KeyMods modifiers = KeyMods.None)
    {
        VirtualKey = virtualKey;
        IsModifier = isModifier;
        Modifiers = modifiers;
    }

    public string DisplayName
    {
        get
        {
            if (IsModifier && ModifierName(VirtualKey) is { } modifier)
                return modifier;
            var parts = new List<string>();
            if (Modifiers.HasFlag(KeyMods.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(KeyMods.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(KeyMods.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(KeyMods.Win)) parts.Add("Win");
            parts.Add(KeyName(VirtualKey));
            return string.Join("+", parts);
        }
    }

    public static bool IsModifierKey(ushort virtualKey)
    {
        return virtualKey is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
    }

    public static string? ModifierName(ushort virtualKey) => virtualKey switch
    {
        0xA4 => "Левый Alt",
        0xA5 => "Правый Alt",
        0xA0 => "Левый Shift",
        0xA1 => "Правый Shift",
        0xA2 => "Левый Ctrl",
        0xA3 => "Правый Ctrl",
        0x5B => "Левая Win",
        0x5C => "Правая Win",
        _ => null
    };

    public static string KeyName(ushort virtualKey)
    {
        if (ModifierName(virtualKey) is { } modifier)
            return modifier;
        if (virtualKey is >= 0x41 and <= 0x5A)
            return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x30 and <= 0x39)
            return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87)
            return "F" + (virtualKey - 0x6F);
        return virtualKey switch
        {
            0x20 => "Пробел",
            0x0D => "Enter",
            0x09 => "Tab",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x1B => "Esc",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            _ => "Клавиша " + virtualKey
        };
    }
}

public sealed class DictationEntry
{
    public Guid Id { get; set; }
    public DateTime Date { get; set; }
    public string Text { get; set; } = "";

    [JsonIgnore]
    public string Preview
    {
        get
        {
            var flat = Text.Replace('\n', ' ');
            return flat.Length <= 72 ? flat : flat[..72] + "…";
        }
    }
}

public sealed class TranscriptSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double Start { get; set; }
    public double End { get; set; }
    public string Speaker { get; set; } = "me";
    public string Text { get; set; } = "";

    [JsonIgnore]
    public string SpeakerTitle => Speaker == "them" ? "Собеседник" : "Я";

    [JsonIgnore]
    public string Clock => Format.Clock(Start);
}

public sealed class CallRecord
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public double Duration { get; set; }
    public bool HasSystemAudio { get; set; }
    public List<TranscriptSegment> Segments { get; set; } = new();

    [JsonIgnore]
    public string Title => "Звонок " + CreatedAt.ToString("d MMM, HH:mm", CultureInfo.GetCultureInfo("ru-RU"));

    [JsonIgnore]
    public string Subtitle => Segments.Count == 0
        ? $"{Format.Minutes(Duration)} · без расшифровки"
        : $"{Format.Minutes(Duration)} · {Segments.Count} фраз";

    [JsonIgnore]
    public string Directory => Path.Combine(CallStore.Root, Id.ToString());

    [JsonIgnore]
    public string MicPath => Path.Combine(Directory, "mic.wav");

    [JsonIgnore]
    public string SystemPath => Path.Combine(Directory, "system.wav");

    [JsonIgnore]
    public string PlainTranscript
    {
        get
        {
            if (Segments.Count == 0)
                return "Расшифровки пока нет.";
            return string.Join('\n', Segments.Select(segment =>
                $"[{Format.Clock(segment.Start)}] {segment.SpeakerTitle}: {segment.Text}"));
        }
    }
}

public sealed class SettingsData
{
    public int VirtualKey { get; set; } = 0xA5;
    public uint Modifiers { get; set; }
    public bool IsModifier { get; set; } = true;
    public string DeviceId { get; set; } = "";
    public string ModelDevice { get; set; } = "cpu";
    public bool DidFinishOnboarding { get; set; }
}

public static class Format
{
    public static string Clock(double interval)
    {
        var tenths = (int)Math.Round(Math.Max(0, interval) * 10);
        var minutes = tenths / 600;
        var seconds = (tenths / 10) % 60;
        var fraction = tenths % 10;
        return $"{minutes:00}:{seconds:00}.{fraction}";
    }

    public static string Minutes(double interval)
    {
        var total = Math.Max(0, (int)Math.Floor(interval));
        return $"{total / 60:00}:{total % 60:00}";
    }
}

public enum InsertOutcome
{
    Inserted,
    Secure,
    Failed
}
