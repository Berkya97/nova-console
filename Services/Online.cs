using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nova.Core;

namespace Nova.Services;

static class Net
{
    public static readonly HttpClient Http = Create();

    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Nova/0.3 (+https://github.com/Berkya97/nova-console)");
        return c;
    }

    public static async Task<JsonDocument?> Json(string url)
    {
        try { return JsonDocument.Parse(await Http.GetStringAsync(url)); }
        catch { return null; }
    }
}

/// <summary>Open-Meteo ile hava durumu (anahtar gerektirmez). Konum şehir adından ya da IP'den bulunur.</summary>
public static class Weather
{
    public sealed record Info(double Temp, int Code, string City);

    public static async Task<Info?> GetAsync(string? city)
    {
        if (city == "off") return null;
        double lat, lon;
        string name;
        if (string.IsNullOrWhiteSpace(city))
        {
            using var ip = await Net.Json("http://ip-api.com/json/?fields=status,city,lat,lon");
            if (ip == null || ip.RootElement.GetProperty("status").GetString() != "success") return null;
            lat = ip.RootElement.GetProperty("lat").GetDouble();
            lon = ip.RootElement.GetProperty("lon").GetDouble();
            name = ip.RootElement.GetProperty("city").GetString() ?? "";
        }
        else
        {
            using var geo = await Net.Json($"https://geocoding-api.open-meteo.com/v1/search?count=1&language={(L.En ? "en" : "tr")}&name={Uri.EscapeDataString(city)}");
            if (geo == null || !geo.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
            var r = res[0];
            lat = r.GetProperty("latitude").GetDouble();
            lon = r.GetProperty("longitude").GetDouble();
            name = r.GetProperty("name").GetString() ?? city;
        }
        var inv = CultureInfo.InvariantCulture;
        using var w = await Net.Json($"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(inv)}&longitude={lon.ToString(inv)}&current=temperature_2m,weather_code");
        if (w == null) return null;
        var cur = w.RootElement.GetProperty("current");
        return new Info(cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("weather_code").GetInt32(), name);
    }

    /// <summary>WMO hava kodu → simge (Segoe UI Symbol) ve açıklama.</summary>
    public static (string Symbol, string Text) Describe(int code) => code switch
    {
        0 => ("☀", "Açık hava"),
        1 or 2 => ("⛅", "Parçalı bulutlu"),
        3 => ("☁", "Bulutlu"),
        45 or 48 => ("☁", "Sisli"),
        >= 51 and <= 57 => ("☂", "Çisenti"),
        >= 61 and <= 67 or >= 80 and <= 82 => ("☂", "Yağmurlu"),
        >= 71 and <= 77 or 85 or 86 => ("❄", "Karlı"),
        >= 95 => ("⚡", "Fırtına"),
        _ => ("☁", ""),
    };
}

/// <summary>Steam'den haberler, başarımlar ve indirme durumu.</summary>
public static class SteamOnline
{
    public sealed record News(string Title, string Url, DateTime Date, string Feed);
    public sealed record Achievement(string Name, string Description, string? Icon, bool Achieved, DateTime? Unlocked);

    static readonly Dictionary<string, Task<List<News>>> NewsCache = new();

    /// <summary>Oyunun son haberleri (anahtar gerektirmez). UI iş parçacığından çağrılır.</summary>
    public static Task<List<News>> NewsAsync(string appId)
    {
        if (NewsCache.TryGetValue(appId, out var t)) return t;
        return NewsCache[appId] = Task.Run(async () =>
        {
            var list = new List<News>();
            using var doc = await Net.Json($"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count=6&maxlength=1&format=json");
            if (doc == null || !doc.RootElement.TryGetProperty("appnews", out var an)) return list;
            foreach (var n in an.GetProperty("newsitems").EnumerateArray())
            {
                var title = n.GetProperty("title").GetString() ?? "";
                if (title.Length == 0) continue;
                list.Add(new News(
                    System.Net.WebUtility.HtmlDecode(title),
                    n.GetProperty("url").GetString() ?? "",
                    DateTimeOffset.FromUnixTimeSeconds(n.GetProperty("date").GetInt64()).LocalDateTime,
                    n.TryGetProperty("feedlabel", out var f) ? f.GetString() ?? "" : ""));
                if (list.Count == 3) break;
            }
            return list;
        });
    }

    /// <summary>Steam'de en son giriş yapan hesabın SteamID64'ü (loginusers.vdf).</summary>
    public static string? SteamId()
    {
        var steam = GameLibrary.SteamPath();
        if (steam == null) return null;
        var file = Path.Combine(steam, "config", "loginusers.vdf");
        if (!File.Exists(file)) return null;
        try
        {
            var users = Vdf.Path(Vdf.Parse(File.ReadAllText(file)), "users");
            if (users == null) return null;
            string? first = null;
            foreach (var (id, v) in users)
            {
                first ??= id;
                if (v is Dictionary<string, object> u && u.GetValueOrDefault("MostRecent") as string == "1") return id;
            }
            return first;
        }
        catch { return null; }
    }

    /// <summary>Oyunun başarımları (Steam Web API anahtarı ve herkese açık oyun ayrıntıları gerekir).</summary>
    public static async Task<(List<Achievement> List, string? Error)> AchievementsAsync(string appId, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return (new(), "Steam Web API anahtarı ayarlanmamış");
        var steamId = SteamId();
        if (steamId == null) return (new(), "Steam hesabı bulunamadı");
        var lang = L.En ? "english" : "turkish";
        using var player = await Net.Json($"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?appid={appId}&key={key}&steamid={steamId}&l={lang}");
        using var schema = await Net.Json($"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?appid={appId}&key={key}&l={lang}");
        if (player == null) return (new(), "Steam'e ulaşılamadı");
        var ps = player.RootElement.GetProperty("playerstats");
        if (!ps.TryGetProperty("achievements", out var achs))
            return (new(), ps.TryGetProperty("error", out var err) ? err.GetString() : "Bu oyunun başarımı yok");

        var icons = new Dictionary<string, (string? Icon, string? Gray, string Name, string Desc)>();
        if (schema != null && schema.RootElement.TryGetProperty("game", out var game) &&
            game.TryGetProperty("availableGameStats", out var stats) && stats.TryGetProperty("achievements", out var defs))
        {
            foreach (var d in defs.EnumerateArray())
                icons[d.GetProperty("name").GetString() ?? ""] = (
                    d.TryGetProperty("icon", out var i) ? i.GetString() : null,
                    d.TryGetProperty("icongray", out var g) ? g.GetString() : null,
                    d.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                    d.TryGetProperty("description", out var de) ? de.GetString() ?? "" : "");
        }

        var list = new List<Achievement>();
        foreach (var a in achs.EnumerateArray())
        {
            var api = a.GetProperty("apiname").GetString() ?? "";
            bool done = a.GetProperty("achieved").GetInt32() == 1;
            var info = icons.GetValueOrDefault(api);
            var name = a.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } nn ? nn : info.Name;
            var desc = a.TryGetProperty("description", out var ds) && ds.GetString() is { Length: > 0 } dd ? dd : info.Desc;
            long ut = a.TryGetProperty("unlocktime", out var u) ? u.GetInt64() : 0;
            list.Add(new Achievement(name.Length > 0 ? name : api, desc, done ? info.Icon : info.Gray ?? info.Icon, done,
                ut > 0 ? DateTimeOffset.FromUnixTimeSeconds(ut).LocalDateTime : null));
        }
        return (list.OrderByDescending(a => a.Achieved).ThenByDescending(a => a.Unlocked).ToList(), null);
    }

    /// <summary>appmanifest dosyasından indirme / güncelleme ilerlemesi. İndirme yoksa null.</summary>
    public static (double Progress, long Done, long Total)? DownloadState(string acf)
    {
        try
        {
            var text = File.ReadAllText(acf);
            long Get(string k) => long.TryParse(Regex.Match(text, "\"" + k + "\"\\s+\"(\\d+)\"").Groups[1].Value, out var v) ? v : 0;
            long total = Get("BytesToDownload"), done = Get("BytesDownloaded");
            if (total <= 0 || done >= total) return null;
            return ((double)done / total, done, total);
        }
        catch { return null; }
    }
}
