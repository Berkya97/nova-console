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

/// <summary>TV ve Video sekmesine kullanıcının eklediği site.</summary>
public sealed class TvService
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>Bir Nova kullanıcısı. Her kullanıcının ayarları ayrı dosyada tutulur.</summary>
public sealed class Profile
{
    public string Id { get; set; } = "main";
    public string Name { get; set; } = "";
    public int Color { get; set; }
}

public sealed class ProfileStore
{
    public List<Profile> Profiles { get; set; } = new();
    public string LastId { get; set; } = "main";

    static string FilePath => System.IO.Path.Combine(App.DataDir, "profiles.json");

    public static ProfileStore Load()
    {
        ProfileStore? s = null;
        try
        {
            if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<ProfileStore>(File.ReadAllText(FilePath));
        }
        catch { }
        s ??= new ProfileStore();
        if (s.Profiles.Count == 0) s.Profiles.Add(new Profile { Id = "main", Name = Environment.UserName });
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>%AppData%\Nova\settings*.json içinde saklanan kullanıcı ayarları.</summary>
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

    // 0.3
    public bool BackgroundMode { get; set; } = true;
    /// <summary>Nova kapatılınca küçük bir dinleyici kalır; kısayol ya da kolla yeniden açılır.</summary>
    public bool WakeWhenClosed { get; set; } = true;
    public string Language { get; set; } = "tr";
    /// <summary>null: konuma göre otomatik, "off": kapalı, diğer: şehir adı.</summary>
    public string? WeatherCity { get; set; }
    public int PlayLimitMinutes { get; set; }
    /// <summary>Gün (yyyy-MM-dd) → o gün oynanan dakika (tüm oyunlar).</summary>
    public Dictionary<string, double> DailyPlay { get; set; } = new();
    public bool RemoteEnabled { get; set; }
    public string RemotePin { get; set; } = Random.Shared.Next(1000, 10000).ToString();
    /// <summary>Kupa kimliği → kazanılma zamanı (Unix).</summary>
    public Dictionary<string, long> Trophies { get; set; } = new();
    public Dictionary<string, int> Counters { get; set; } = new();
    public List<string> DaysUsed { get; set; } = new();
    public string? SteamApiKey { get; set; }
    public List<TvService> TvServices { get; set; } = new();
    public string? UpdateCheckedDay { get; set; }

    public static string ProfileId { get; private set; } = "main";

    static string FileFor(string id) =>
        System.IO.Path.Combine(App.DataDir, id == "main" ? "settings.json" : $"settings_{id}.json");

    public static AppSettings Current { get; private set; } = Load("main");

    /// <summary>Başka bir kullanıcının ayarlarına geç.</summary>
    public static void Use(string profileId)
    {
        ProfileId = profileId;
        Current = Load(profileId);
    }

    static AppSettings Load(string id)
    {
        try
        {
            var f = FileFor(id);
            if (File.Exists(f)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(f)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void DeleteProfileData(string id)
    {
        if (id == "main") return;
        try { File.Delete(FileFor(id)); } catch { }
    }

    readonly object _gate = new();

    public void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(App.DataDir);
            File.WriteAllText(FileFor(ProfileId), json);
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

    public static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    public double PlayedToday()
    {
        lock (_gate) return DailyPlay.TryGetValue(Today, out var v) ? v : 0;
    }

    public void AddDailyPlay(double minutes)
    {
        lock (_gate)
        {
            DailyPlay[Today] = PlayedToday() + minutes;
            // Yalnızca son 30 günü tut.
            foreach (var old in DailyPlay.Keys.Where(k => string.CompareOrdinal(k, DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd")) < 0).ToList())
                DailyPlay.Remove(old);
        }
    }

    public int Count(string counter, int add = 0)
    {
        lock (_gate)
        {
            Counters.TryGetValue(counter, out var v);
            if (add != 0) Counters[counter] = v += add;
            return v;
        }
    }

    public bool IsPinned(string key)
    {
        lock (_gate) return Pinned.Any(p => p.Key == key);
    }
}
