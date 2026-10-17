using System.Data.OleDb;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Nova.Core;
using Nova.Models;

namespace Nova.Services;

/// <summary>
/// Oyun oturumlarını izler: oyun başlatıldıktan sonra kurulum klasöründen çalışan bir süreç görünür,
/// kapanınca geçen süre kaydedilir. Steam dışındaki oyunlarda oyun başına süre, tüm oyunlarda
/// günlük toplam (süre sınırı için) tutulur.
/// </summary>
public static class PlaytimeTracker
{
    sealed class Session
    {
        public string Key = "", Dir = "", Title = "";
        public bool CountPlaytime;
        public DateTime Launched;
        public DateTime? Seen, LastTick;
    }

    static readonly List<Session> Sessions = new();
    static DispatcherTimer? _timer;
    static bool _busy;
    static string _warnedDay = "", _warnedSoonDay = "";

    public static event Action<string, double>? Recorded;
    /// <summary>Günlük oyun süresi sınırı uyarısı (mesaj).</summary>
    public static event Action<string>? LimitWarning;

    /// <summary>Şu an çalışan oyunun bu oturumdaki süresi (dakika), yoksa null.</summary>
    public static (string Title, double Minutes)? Current =>
        Sessions.Where(s => s.Seen != null).Select(s => (s.Title, (DateTime.UtcNow - s.Seen!.Value).TotalMinutes)).Cast<(string, double)?>().FirstOrDefault();

    public static void Track(TileItem game)
    {
        if (game.Kind != TileKind.Game || string.IsNullOrEmpty(game.InstallDir)) return;
        var dir = game.InstallDir.TrimEnd('\\') + "\\";
        if (dir.Length <= 4) return; // sürücü kökü: güvenilmez
        Sessions.RemoveAll(s => s.Key == game.Key);
        Sessions.Add(new Session
        {
            Key = game.Key, Dir = dir, Title = game.Title, Launched = DateTime.UtcNow,
            CountPlaytime = !game.Key.StartsWith("steam:"), // Steam süresini zaten kendisi tutuyor
        });
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _timer.Tick += async (_, _) => await Tick();
        }
        _timer.Start();
    }

    static async Task Tick()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var paths = await Task.Run(Native.RunningImagePaths);
            var now = DateTime.UtcNow;
            var s = AppSettings.Current;
            foreach (var x in Sessions.ToList())
            {
                bool running = paths.Any(p => p.StartsWith(x.Dir, StringComparison.OrdinalIgnoreCase));
                if (running)
                {
                    if (x.Seen == null) { x.Seen = x.LastTick = now; continue; }
                    s.AddDailyPlay((now - x.LastTick!.Value).TotalMinutes);
                    x.LastTick = now;
                }
                else if (x.Seen is { } start)
                {
                    s.AddDailyPlay((now - x.LastTick!.Value).TotalMinutes);
                    Sessions.Remove(x);
                    if (x.CountPlaytime)
                    {
                        s.AddPlaytime(x.Key, (now - start).TotalMinutes);
                        Recorded?.Invoke(x.Key, s.PlaytimeOf(x.Key));
                    }
                    else s.Save();
                }
                else if (now - x.Launched > TimeSpan.FromMinutes(3)) Sessions.Remove(x);
            }
            CheckLimit(s);
            if (Sessions.Count == 0) _timer?.Stop();
        }
        finally { _busy = false; }
    }

    static void CheckLimit(AppSettings s)
    {
        if (s.PlayLimitMinutes <= 0 || !Sessions.Any(x => x.Seen != null)) return;
        var played = s.PlayedToday();
        var today = AppSettings.Today;
        if (played >= s.PlayLimitMinutes && _warnedDay != today)
        {
            _warnedDay = today;
            LimitWarning?.Invoke("Bugünkü oyun süren doldu. Biraz mola vermeye ne dersin?");
        }
        else if (played >= s.PlayLimitMinutes - 10 && _warnedSoonDay != today && played < s.PlayLimitMinutes)
        {
            _warnedSoonDay = today;
            LimitWarning?.Invoke("Bugünkü oyun süren 10 dakika içinde doluyor");
        }
    }
}
/// <summary>SteamGridDB'den kapak, arka plan ve logo indirir (kullanıcının API anahtarıyla).</summary>
public static class SteamGridDb
{
    static readonly HttpClient Http = new() { BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/"), Timeout = TimeSpan.FromSeconds(15) };
    static string ArtDir => Path.Combine(App.DataDir, "art");

    /// <summary>Kapağı olmayan oyunlar için görselleri bulur; önbellekteyse ağa çıkmaz.</summary>
    public static async Task EnrichAsync(IEnumerable<TileItem> games, string? key)
    {
        Directory.CreateDirectory(ArtDir);
        foreach (var g in games.Where(g => g.Kind == TileKind.Game && g.CoverPath == null).ToList())
        {
            var baseName = Path.Combine(ArtDir, Safe(g.Key));
            var cached = Cached(baseName);
            if (cached.Cover != null) { g.SetArt(cached.Cover, cached.Hero, cached.Logo); continue; }
            if (string.IsNullOrWhiteSpace(key) || File.Exists(baseName + ".none")) continue;

            try
            {
                string kind, id;
                if (g.Key.StartsWith("steam:")) { kind = "steam"; id = g.Key[6..]; }
                else
                {
                    var search = await Get(key, "search/autocomplete/" + Uri.EscapeDataString(g.Title));
                    var first = search?.EnumerateArray().FirstOrDefault();
                    if (first is not { ValueKind: JsonValueKind.Object } f) { await File.WriteAllTextAsync(baseName + ".none", ""); continue; }
                    kind = "game";
                    id = f.GetProperty("id").ToString();
                }

                var cover = await Download(key, $"grids/{kind}/{id}?dimensions=512x512,1024x1024", baseName + ".grid")
                            ?? await Download(key, $"grids/{kind}/{id}", baseName + ".grid");
                var hero = await Download(key, $"heroes/{kind}/{id}", baseName + ".hero");
                var logo = await Download(key, $"logos/{kind}/{id}", baseName + ".logo");
                if (cover == null) { await File.WriteAllTextAsync(baseName + ".none", ""); continue; }
                g.SetArt(cover, hero, logo);
            }
            catch (Exception e) { App.Log(e); }
        }
    }

    static (string? Cover, string? Hero, string? Logo) Cached(string baseName)
    {
        string? Find(string suffix) => Directory.EnumerateFiles(ArtDir, Path.GetFileName(baseName) + suffix + ".*").FirstOrDefault();
        return (Find(".grid"), Find(".hero"), Find(".logo"));
    }

    static async Task<JsonElement?> Get(string key, string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("data", out var data) ? data.Clone() : null;
    }

    static async Task<string?> Download(string key, string url, string target)
    {
        var data = await Get(key, url);
        var first = data?.ValueKind == JsonValueKind.Array ? data.Value.EnumerateArray().FirstOrDefault() : default;
        if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("url", out var u)) return null;
        var imgUrl = u.GetString();
        if (imgUrl == null) return null;
        var ext = Path.GetExtension(new Uri(imgUrl).AbsolutePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var bytes = await Http.GetByteArrayAsync(imgUrl);
        var path = target + ext;
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}

/// <summary>Arama: oyunlar ve uygulamalar bellekten, dosyalar Windows Arama dizininden.</summary>
public static class SearchService
{
    public static List<TileItem> Match(IEnumerable<TileItem> source, string query, int max)
    {
        var q = query.Trim();
        if (q.Length == 0) return new();
        var tr = Fmt.Tr.CompareInfo;
        return source
            .Select(t => (t, idx: tr.IndexOf(t.Title, q, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace)))
            .Where(x => x.idx >= 0)
            .OrderBy(x => x.idx == 0 ? 0 : 1)
            .ThenBy(x => x.t.Title.Length)
            .Take(max)
            .Select(x => x.t)
            .ToList();
    }

    /// <summary>Windows Arama dizininde dosya ve klasör adı araması.</summary>
    public static List<TileItem> Files(string query, int max)
    {
        var list = new List<TileItem>();
        var q = new string(query.Trim().Where(c => c is not ('\'' or '%' or '_' or '[' or ']')).ToArray());
        if (q.Length < 2) return list;
        try
        {
            using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            conn.Open();
            // ItemPathDisplay yerelleştirilmiş görünen yolu verir (C:\Kullanıcılar\...); gerçek yol ItemUrl'de.
            var sql = $"SELECT TOP {max} System.ItemUrl FROM SystemIndex " +
                      $"WHERE SCOPE='file:' AND System.FileName LIKE '%{q}%' ORDER BY System.DateModified DESC";
            using var cmd = new OleDbCommand(sql, conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r[0] is not string url || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
                var path = Uri.UnescapeDataString(url[5..]).Replace('/', '\\');
                if (Directory.Exists(path))
                    list.Add(new TileItem
                    {
                        Kind = TileKind.Folder,
                        Title = Path.GetFileName(path),
                        Path = path,
                        InitGlyph = Glyphs.Folder,
                        TileBrush = Theme.Translucent,
                        Subtitle = "Klasör  ·  " + path,
                        ActionLabel = "Aç",
                        ActionGlyph = Glyphs.OpenFolder,
                    });
                else if (File.Exists(path))
                {
                    var t = FileSystemService.FileTile(new FileInfo(path));
                    t.Subtitle = path;
                    list.Add(t);
                }
            }
        }
        catch (Exception e) { App.Log(e); }
        return list;
    }
}

/// <summary>Nova açıkken çalan arka plan müziği (dosya ya da klasör).</summary>
public sealed class BackgroundMusic
{
    readonly MediaPlayer _player = new();
    List<string> _playlist = new();
    int _index;
    bool _active, _playing;

    public BackgroundMusic()
    {
        _player.MediaEnded += (_, _) => Next();
        _player.MediaFailed += (_, _) => Next();
    }

    public void Configure(string? path, int volume)
    {
        _player.Volume = Math.Clamp(volume, 0, 100) / 100.0;
        var list = new List<string>();
        if (path != null && Directory.Exists(path))
            list = Directory.EnumerateFiles(path).Where(f => MediaTypes.KindOf(f) == MediaKind.Audio).OrderBy(_ => Random.Shared.Next()).ToList();
        else if (path != null && File.Exists(path)) list.Add(path);

        if (!list.SequenceEqual(_playlist))
        {
            _playlist = list;
            _index = 0;
            _player.Stop();
            _player.Close();
            _playing = false;
        }
        Update();
    }

    /// <summary>Nova önde ve başka bir medya çalmıyorken true.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        Update();
    }

    void Update()
    {
        bool want = _active && _playlist.Count > 0;
        if (want && !_playing)
        {
            if (_player.Source == null) _player.Open(new Uri(_playlist[_index]));
            _player.Play();
            _playing = true;
        }
        else if (!want && _playing)
        {
            _player.Pause();
            _playing = false;
        }
    }

    void Next()
    {
        if (_playlist.Count == 0) return;
        _index = (_index + 1) % _playlist.Count;
        _player.Open(new Uri(_playlist[_index]));
        if (_playing) _player.Play();
    }
}

public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("Nova") != null;
    }

    /// <summary>Açılışta arka planda (gizli) başlar; Ctrl+Alt+N veya kol kombinasyonuyla açılır.</summary>
    public static void Set(bool on, string? exe = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        exe ??= Environment.ProcessPath;
        if (on && exe != null) k.SetValue("Nova", $"\"{exe}\" --background");
        else k.DeleteValue("Nova", false);
    }
}
