using System.Text.Encodings.Web;
using System.Text.Json;

namespace GigaTTS;

static class JsonOptions
{
    public static JsonSerializerOptions Indented { get; } = Create(true);
    public static JsonSerializerOptions Compact { get; } = Create(false);

    static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

static class AppPaths
{
    public static string Support
    {
        get
        {
            var path = OperatingSystem.IsLinux() ? LinuxData() : WindowsData();
            Directory.CreateDirectory(path);
            return path;
        }
    }

    static string WindowsData() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GigaTTS");

    static string LinuxData()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Path.Combine(root, "GigaTTS");
    }

    public static string HistoryFile => Path.Combine(Support, "dictation-history.json");
}

static class SettingsStore
{
    static string FilePath => Path.Combine(AppPaths.Support, "settings.json");

    public static SettingsData Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new SettingsData();
            return JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath), JsonOptions.Indented) ?? new SettingsData();
        }
        catch
        {
            return new SettingsData();
        }
    }

    public static void Save(SettingsData data)
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data, JsonOptions.Indented));
    }
}

static class HistoryStore
{
    public static List<DictationEntry> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.HistoryFile))
                return new List<DictationEntry>();
            return JsonSerializer.Deserialize<List<DictationEntry>>(File.ReadAllText(AppPaths.HistoryFile), JsonOptions.Indented)
                ?? new List<DictationEntry>();
        }
        catch
        {
            return new List<DictationEntry>();
        }
    }

    public static void Save(IEnumerable<DictationEntry> entries)
    {
        File.WriteAllText(AppPaths.HistoryFile, JsonSerializer.Serialize(entries, JsonOptions.Indented));
    }
}

static class CallStore
{
    public static string Root
    {
        get
        {
            var path = Path.Combine(AppPaths.Support, "calls");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static List<CallRecord> Load()
    {
        var records = new List<CallRecord>();
        if (!Directory.Exists(Root))
            return records;
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var meta = Path.Combine(directory, "meta.json");
            if (!File.Exists(meta))
                continue;
            try
            {
                var record = JsonSerializer.Deserialize<CallRecord>(File.ReadAllText(meta), JsonOptions.Indented);
                if (record == null)
                    continue;
                foreach (var segment in record.Segments)
                {
                    if (segment.Id == Guid.Empty)
                        segment.Id = Guid.NewGuid();
                }
                records.Add(record);
            }
            catch
            {
                // A damaged meta file should not hide the other calls.
            }
        }
        records.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return records;
    }

    public static void Save(CallRecord record)
    {
        Directory.CreateDirectory(record.Directory);
        File.WriteAllText(Path.Combine(record.Directory, "meta.json"), JsonSerializer.Serialize(record, JsonOptions.Indented));
    }

    public static void Delete(CallRecord record)
    {
        if (Directory.Exists(record.Directory))
            Directory.Delete(record.Directory, true);
    }
}
