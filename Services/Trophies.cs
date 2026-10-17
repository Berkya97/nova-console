using Nova.Core;
using Nova.Models;

namespace Nova.Services;

/// <summary>Nova'nın kendi kupaları: kullanım alışkanlıklarına göre kazanılır.</summary>
public static class Trophies
{
    public sealed record Def(string Id, string Title, string Description, string Glyph, string Tier);

    /// <summary>Kupa koşullarının değerlendirildiği bağlam.</summary>
    public sealed record Context(IReadOnlyList<TileItem> Games);

    public static readonly Def[] All =
    {
        new("first_launch", "İlk adım", "Nova'dan bir oyun başlat", Glyphs.Play, "bronz"),
        new("regular", "Müdavim", "Nova'dan 25 kez oyun ya da uygulama başlat", Glyphs.Restart, "gümüş"),
        new("collector", "Koleksiyoncu", "Kütüphanende 10 oyun olsun", Glyphs.Game, "bronz"),
        new("marathon", "Maratoncu", "Tek bir oyunda 10 saati geç", Glyphs.Timer, "altın"),
        new("night_owl", "Gece kuşu", "Gece yarısından sonra bir oyun başlat", Glyphs.Sleep, "bronz"),
        new("photographer", "Fotoğrafçı", "10 ekran görüntüsü al", Glyphs.Picture, "gümüş"),
        new("organizer", "Düzenli", "Ana ekrana 3 öğe sabitle", Glyphs.Pin, "bronz"),
        new("explorer", "Kaşif", "Dosyalarda 50 klasör aç", Glyphs.Folder, "bronz"),
        new("detective", "Dedektif", "10 arama yap", Glyphs.Search, "bronz"),
        new("loyal", "Sadık", "Nova'yı 7 farklı gün kullan", Glyphs.Accept, "altın"),
        new("stylist", "Stil sahibi", "Temayı ya da duvar kağıdını değiştir", Glyphs.Palette, "bronz"),
        new("remote", "Uzaktan kumandacı", "Nova'yı telefonundan kontrol et", Glyphs.Wifi, "gümüş"),
        new("platinum", "Platin", "Diğer tüm kupaları kazan", Glyphs.Accept, "platin"),
    };

    static bool Met(string id, AppSettings s, Context c) => id switch
    {
        "first_launch" => s.Count("game_launches") >= 1,
        "regular" => s.Count("launches") >= 25,
        "collector" => c.Games.Count >= 10,
        "marathon" => c.Games.Any(g => g.PlaytimeMinutes >= 600),
        "night_owl" => s.Count("night_launches") >= 1,
        "photographer" => s.Count("screenshots") >= 10,
        "organizer" => s.Pinned.Count >= 3,
        "explorer" => s.Count("folders") >= 50,
        "detective" => s.Count("searches") >= 10,
        "loyal" => s.DaysUsed.Count >= 7,
        "stylist" => s.Theme != Themes.All[0].Name || s.WallpaperPath != null,
        "remote" => s.Count("remote") >= 1,
        "platinum" => All.Where(d => d.Id != "platinum").All(d => s.Trophies.ContainsKey(d.Id)),
        _ => false,
    };

    /// <summary>Yeni kazanılan kupaları kaydeder ve döndürür.</summary>
    public static List<Def> Check(Context c)
    {
        var s = AppSettings.Current;
        var won = new List<Def>();
        foreach (var d in All)
        {
            if (s.Trophies.ContainsKey(d.Id) || !Met(d.Id, s, c)) continue;
            s.Trophies[d.Id] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            won.Add(d);
        }
        if (won.Count > 0) s.Save();
        return won;
    }

    public static int Earned => All.Count(d => AppSettings.Current.Trophies.ContainsKey(d.Id));
}

/// <summary>Uyku zamanlayıcısı: süre dolunca bilgisayarı uyutur ya da kapatır.</summary>
public static class SleepTimer
{
    static readonly System.Windows.Threading.DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(5) };
    static bool _warned;

    public static DateTime? Until { get; private set; }
    public static bool Shutdown { get; set; }

    public static event Action<string>? Warning;
    public static event Action? Expired;

    static SleepTimer() => Timer.Tick += (_, _) => Tick();

    public static readonly int[] Steps = { 0, 15, 30, 60, 90, 120 };

    public static void Set(int minutes)
    {
        _warned = false;
        if (minutes <= 0) { Until = null; Timer.Stop(); return; }
        Until = DateTime.Now.AddMinutes(minutes);
        Timer.Start();
    }

    public static int NextStep()
    {
        int left = Until is { } u ? (int)Math.Ceiling((u - DateTime.Now).TotalMinutes) : 0;
        int next = Steps.FirstOrDefault(s => s > left + 1);
        Set(next);
        return next;
    }

    public static string Describe()
    {
        if (Until is not { } u) return "Kapalı";
        var left = u - DateTime.Now;
        return left.TotalMinutes >= 60 ? $"{(int)left.TotalHours} sa {left.Minutes} dk" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} dk";
    }

    static void Tick()
    {
        if (Until is not { } u) return;
        var left = u - DateTime.Now;
        if (!_warned && left <= TimeSpan.FromMinutes(1))
        {
            _warned = true;
            Warning?.Invoke(Shutdown ? "Bilgisayar 1 dakika içinde kapanacak" : "Bilgisayar 1 dakika içinde uyku moduna geçecek");
        }
        if (left > TimeSpan.Zero) return;
        Until = null;
        Timer.Stop();
        Expired?.Invoke();
    }
}
