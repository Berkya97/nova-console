using System.Text.Json;

namespace Nova.Services;

public sealed class CustomGame
{
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>Ana ekrana sabitlenmiş herhangi bir öğe (oyun, uygulama, klasör, dosya).</summary>
public sealed class PinnedItem
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Path { get; set; }
    public string? LaunchUri { get; set; }
}

/// <summary>%AppData%\Nova\settings.json içinde saklanan kullanıcı ayarları.</summary>
public sealed class AppSettings
{
    public List<CustomGame> CustomGames { get; set; } = new();
    public Dictionary<string, long> LastLaunched { get; set; } = new();
    /// <summary>Nova'nın ölçtüğü oynama süreleri (dakika).</summary>
    public Dictionary<string, double> Playtime { get; set; } = new();
    public List<PinnedItem> Pinned { get; set; } = new();
    public bool Sounds { get; set; } = true;
    public string Theme { get; set; } = "Gece Mavisi";
    public string? WallpaperPath { get; set; }
    public string? MusicPath { get; set; }
    public int MusicVolume { get; set; } = 30;
    public int DefaultTab { get; set; }
    public bool ShowHidden { get; set; }
    public string? SteamGridDbKey { get; set; }
    public string FileSort { get; set; } = "Ad";

    static string FilePath => System.IO.Path.Combine(App.DataDir, "settings.json");

    public static AppSettings Current { get; } = Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    readonly object _gate = new();

    public void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(App.DataDir);
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    public long LastLaunchOf(string key)
    {
        lock (_gate) return LastLaunched.TryGetValue(key, out var v) ? v : 0;
    }

    public void MarkLaunched(string key)
    {
        lock (_gate) LastLaunched[key] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Save();
    }

    public double PlaytimeOf(string key)
    {
        lock (_gate) return Playtime.TryGetValue(key, out var v) ? v : 0;
    }

    public void AddPlaytime(string key, double minutes)
    {
        lock (_gate) Playtime[key] = PlaytimeOf(key) + minutes;
        Save();
    }

    public bool IsPinned(string key)
    {
        lock (_gate) return Pinned.Any(p => p.Key == key);
    }
}
