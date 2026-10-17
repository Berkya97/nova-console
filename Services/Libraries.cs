using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using Nova.Core;
using Nova.Models;

namespace Nova.Services;

/// <summary>shell:AppsFolder içindeki bir uygulama (masaüstü veya Microsoft Store).</summary>
public sealed record AppEntry(string Name, string ParsingPath);

/// <summary>Kurulu oyunları tüm platformlardan toplar.</summary>
public static class GameLibrary
{
    static readonly HashSet<string> IgnoredSteamIds = new() { "228980", "1070560", "1391110", "1628350", "1493710", "2180100" };

    public static List<TileItem> LoadAll(IReadOnlyList<AppEntry> apps)
    {
        var all = new List<TileItem>();
        void Try(Func<IEnumerable<TileItem>> f)
        {
            try { all.AddRange(f()); } catch (Exception e) { App.Log(e); }
        }
        Try(Steam);
        Try(Epic);
        Try(Gog);
        Try(() => Xbox(apps));
        Try(Ubisoft);
        Try(UninstallBased);
        Try(Custom);

        // Aynı oyun iki kaynaktan gelirse (ör. GOG + kaldırma kaydı) bir kez göster.
        all = all.GroupBy(g => g.Title.ToLowerInvariant()).Select(g => g.First()).ToList();

        var settings = AppSettings.Current;
        foreach (var g in all)
        {
            g.LastPlayed = Math.Max(g.LastPlayed, settings.LastLaunchOf(g.Key));
            if (!g.Key.StartsWith("steam:")) g.PlaytimeMinutes = settings.PlaytimeOf(g.Key);
            UpdateSubtitle(g);
        }
        return all
            .OrderByDescending(g => g.LastPlayed)
            .ThenBy(g => g.Title, StringComparer.Create(Fmt.Tr, true))
            .ToList();
    }

    public static void UpdateSubtitle(TileItem g)
    {
        var parts = new List<string> { g.Source.Length > 0 ? g.Source : "Oyun" };
        var play = Fmt.Playtime(g.PlaytimeMinutes);
        if (play.Length > 0) parts.Add(play);
        parts.Add(Fmt.Ago(g.LastPlayed));
        g.SetSubtitle(string.Join("  ·  ", parts));
    }

    static TileItem Game(string source, string key, string title, string? exe = null, string? uri = null,
        string? installDir = null, string? cover = null, string? hero = null, string? logo = null, bool custom = false, string? statusFile = null) => new()
    {
        Kind = TileKind.Game,
        Title = title,
        Key = key,
        Path = exe != null && File.Exists(exe) ? exe : null,
        LaunchUri = uri,
        InstallDir = installDir,
        InitCover = cover,
        InitHero = hero,
        InitLogo = logo,
        InitGlyph = cover == null && (exe == null || !File.Exists(exe)) ? Glyphs.Game : null,
        TileBrush = Theme.Tile(Theme.Slate),
        ActionLabel = "Oyna",
        ActionGlyph = Glyphs.Play,
        IsCustom = custom,
        Source = source,
        StatusFile = statusFile,
    };

    // ───────────── Steam ─────────────

    public static string? SteamPath()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return string.IsNullOrEmpty(steam) ? null : steam.Replace('/', '\\');
    }

    static IEnumerable<TileItem> Steam()
    {
        var steam = SteamPath();
        if (steam == null) yield break;

        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        var stats = SteamUserStats(steam);
        var cache = Path.Combine(steam, "appcache", "librarycache");
        var seen = new HashSet<string>();
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf); } catch { continue; }
                var id = Field(text, "appid");
                var name = Field(text, "name");
                if (id == null || name == null || IgnoredSteamIds.Contains(id) || !seen.Add(id)) continue;
                if (name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)) continue;

                long.TryParse(Field(text, "LastPlayed"), out var lastPlayed);
                var installDir = Field(text, "installdir") is { } dir ? Path.Combine(apps, "common", dir) : null;
                var item = Game("Steam", "steam:" + id, name, uri: "steam://rungameid/" + id, installDir: installDir,
                    cover: Art(cache, id, "library_600x900.jpg") ?? Art(cache, id, "header.jpg"),
                    hero: Art(cache, id, "library_hero.jpg"),
                    logo: Art(cache, id, "logo.png"), statusFile: acf);
                item.LastPlayed = lastPlayed;
                if (stats.TryGetValue(id, out var st))
                {
                    item.PlaytimeMinutes = st.Minutes;
                    item.LastPlayed = Math.Max(item.LastPlayed, st.LastPlayed);
                }
                yield return item;
            }
        }
    }

    /// <summary>Steam'in kendi tuttuğu oynama süreleri (userdata\*\config\localconfig.vdf).</summary>
    static Dictionary<string, (double Minutes, long LastPlayed)> SteamUserStats(string steam)
    {
        var result = new Dictionary<string, (double, long)>();
        var userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata)) return result;
        foreach (var user in Directory.EnumerateDirectories(userdata))
        {
            var file = Path.Combine(user, "config", "localconfig.vdf");
            if (!File.Exists(file)) continue;
            try
            {
                var root = Vdf.Parse(File.ReadAllText(file));
                var apps = Vdf.Path(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
                if (apps == null) continue;
                foreach (var (id, value) in apps)
                {
                    if (value is not Dictionary<string, object> app) continue;
                    double.TryParse(app.GetValueOrDefault("Playtime") as string, out var minutes);
                    long.TryParse(app.GetValueOrDefault("LastPlayed") as string, out var last);
                    var prev = result.GetValueOrDefault(id);
                    result[id] = (Math.Max(prev.Item1, minutes), Math.Max(prev.Item2, last));
                }
            }
            catch { }
        }
        return result;
    }

    static string? Field(string text, string key)
    {
        var m = Regex.Match(text, "\"" + key + "\"\\s+\"(.*?)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Steam kapak görselleri eski (appid_dosya) veya yeni (appid\...\dosya) düzende olabilir.</summary>
    static string? Art(string cache, string id, string file)
    {
        var flat = Path.Combine(cache, id + "_" + file);
        if (File.Exists(flat)) return flat;
        var dir = Path.Combine(cache, id);
        if (!Directory.Exists(dir)) return null;
        try { return Directory.EnumerateFiles(dir, file, SearchOption.AllDirectories).FirstOrDefault(); }
        catch { return null; }
    }

    // ───────────── Epic Games ─────────────

    static IEnumerable<TileItem> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            TileItem? item = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

                if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                if (r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array &&
                    !cats.EnumerateArray().Any(c => c.GetString() == "games")) continue;
                var appName = S("AppName");
                if (S("MainGameAppName") is { Length: > 0 } main && main != appName) continue;
                var exe = S("LaunchExecutable");
                if (exe.Length == 0) continue;

                item = Game("Epic Games", "epic:" + appName, S("DisplayName"),
                    exe: Path.Combine(S("InstallLocation"), exe),
                    uri: $"com.epicgames.launcher://apps/{S("CatalogNamespace")}%3A{S("CatalogItemId")}%3A{appName}?action=launch&silent=true",
                    installDir: S("InstallLocation"));
            }
            catch { }
            if (item != null) yield return item;
        }
    }

    // ───────────── GOG Galaxy ─────────────

    static IEnumerable<TileItem> Gog()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (root == null) yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            var name = k?.GetValue("gameName") as string;
            var exe = k?.GetValue("exe") as string;
            if (string.IsNullOrEmpty(name) || exe == null || !File.Exists(exe)) continue;
            yield return Game("GOG", "gog:" + id, name, exe: exe, installDir: k?.GetValue("path") as string);
        }
    }

    // ───────────── Xbox / Game Pass ─────────────

    static IEnumerable<TileItem> Xbox(IReadOnlyList<AppEntry> apps)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            var marker = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
            List<string> roots;
            try
            {
                if (!File.Exists(marker)) continue;
                var bytes = File.ReadAllBytes(marker);
                if (bytes.Length <= 8) continue;
                roots = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(r => Path.Combine(drive.RootDirectory.FullName, r.TrimStart('\\')))
                    .Where(Directory.Exists)
                    .ToList();
            }
            catch { continue; }

            foreach (var root in roots)
            {
                foreach (var gameDir in Directory.EnumerateDirectories(root))
                {
                    var content = Path.Combine(gameDir, "Content");
                    var config = Path.Combine(content, "MicrosoftGame.config");
                    if (!File.Exists(config)) continue;
                    TileItem? item = null;
                    try
                    {
                        var x = XDocument.Load(config).Root!;
                        var identity = x.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value;
                        var visuals = x.Elements().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
                        var name = visuals?.Attribute("DefaultDisplayName")?.Value;
                        if (string.IsNullOrEmpty(name) || name.StartsWith("ms-resource")) name = Path.GetFileName(gameDir);
                        var logo = new[] { "Square480x480Logo", "Square150x150Logo", "StoreLogo" }
                            .Select(a => visuals?.Attribute(a)?.Value)
                            .Where(v => v != null)
                            .Select(v => Path.Combine(content, v!))
                            .FirstOrDefault(File.Exists);
                        var aumid = identity == null ? null
                            : apps.FirstOrDefault(a => a.ParsingPath.StartsWith(identity + "_", StringComparison.OrdinalIgnoreCase))?.ParsingPath;
                        if (aumid == null) continue;
                        item = Game("Xbox", "xbox:" + identity, name, uri: @"shell:AppsFolder\" + aumid, installDir: content, cover: logo);
                    }
                    catch { }
                    if (item != null) yield return item;
                }
            }
        }
    }

    // ───────────── Ubisoft Connect ─────────────

    static IEnumerable<TileItem> Ubisoft()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
        if (root == null) yield break;
        var uninstall = UninstallEntries().ToList();
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            var dir = (k?.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var entry = uninstall.FirstOrDefault(u => u.KeyName == "Uplay Install " + id);
            var name = entry?.DisplayName ?? Path.GetFileName(dir);
            yield return Game("Ubisoft Connect", "ubisoft:" + id, name, exe: entry?.IconExe, uri: $"uplay://launch/{id}/0", installDir: dir);
        }
    }

    // ───────────── EA app ve Battle.net (kaldırma kayıtlarından) ─────────────

    static IEnumerable<TileItem> UninstallBased()
    {
        foreach (var u in UninstallEntries())
        {
            if (u.IconExe == null || string.IsNullOrEmpty(u.DisplayName)) continue;
            string? source = null;
            if (u.Publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(u.DisplayName, "EA app|Origin|EA Desktop", RegexOptions.IgnoreCase))
                source = "EA app";
            else if (u.Publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) &&
                     !u.DisplayName.Contains("Battle.net", StringComparison.OrdinalIgnoreCase))
                source = "Battle.net";
            if (source == null) continue;
            yield return Game(source, "reg:" + u.KeyName, u.DisplayName, exe: u.IconExe, installDir: u.InstallLocation);
        }
    }

    sealed record UninstallEntry(string KeyName, string DisplayName, string Publisher, string? InstallLocation, string? IconExe);

    static IEnumerable<UninstallEntry> UninstallEntries()
    {
        var sources = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        };
        var list = new List<UninstallEntry>();
        foreach (var (hive, view) in sources)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var un = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (un == null) continue;
                foreach (var name in un.GetSubKeyNames())
                {
                    using var k = un.OpenSubKey(name);
                    if (k == null) continue;
                    var icon = (k.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"', ' ');
                    list.Add(new UninstallEntry(name,
                        k.GetValue("DisplayName") as string ?? "",
                        k.GetValue("Publisher") as string ?? "",
                        k.GetValue("InstallLocation") as string,
                        icon != null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) ? icon : null));
                }
            }
            catch { }
        }
        return list;
    }

    // ───────────── Elle eklenenler ─────────────

    static IEnumerable<TileItem> Custom()
    {
        foreach (var g in AppSettings.Current.CustomGames.ToList())
            yield return Game("Kütüphanem", "custom:" + g.Path.ToLowerInvariant(), g.Title, exe: g.Path,
                installDir: Path.GetDirectoryName(g.Path), custom: true);
    }

    public static TileItem AddTile() => new()
    {
        Kind = TileKind.Action,
        Title = "Oyun Ekle",
        Subtitle = "Bilgisayarındaki bir oyunu ya da programı kütüphaneye ekle",
        InitGlyph = Glyphs.Add,
        TileBrush = Theme.Translucent,
        ActionLabel = "Ekle",
        ActionGlyph = Glyphs.Add,
        Tag = "add-game",
    };
}

/// <summary>Uygulama listesi: shell:AppsFolder (Store uygulamaları dahil), olmazsa Başlat menüsü.</summary>
public static class AppLibrary
{
    static readonly Regex Skip = new(
        @"uninstall|kaldır|readme|help|yardım|website|web site|documentation|license|lisans|release notes|manual|sürüm notları|belgeler$",
        RegexOptions.IgnoreCase);

    /// <summary>STA iş parçacığında çağrılmalı.</summary>
    public static List<AppEntry> EnumerateAppsFolder()
    {
        var list = new List<AppEntry>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return list;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string path = item.Path;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path)) list.Add(new AppEntry(name, path));
            }
            Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception e) { App.Log(e); }
        return list;
    }

    public static List<TileItem> Build(IReadOnlyList<AppEntry> entries)
    {
        if (entries.Count == 0) return StartMenu();
        var byName = new Dictionary<string, TileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            var p = e.ParsingPath;
            if (Skip.IsMatch(e.Name) || byName.ContainsKey(e.Name)) continue;
            if (p.Contains("://") || Regex.IsMatch(p, @"\.(url|chm|txt|pdf|html?|rtf|ini|log)$", RegexOptions.IgnoreCase)) continue;
            bool store = p.Contains('!') && !p.Contains('\\');
            byName[e.Name] = new TileItem
            {
                Kind = TileKind.App,
                Title = e.Name,
                Key = "app:" + e.Name.ToLowerInvariant(),
                LaunchUri = @"shell:AppsFolder\" + p,
                IconPath = @"shell:AppsFolder\" + p,
                Path = File.Exists(p) ? p : null,
                TileBrush = Theme.Translucent,
                Subtitle = store ? "Microsoft Store uygulaması" : "Uygulama",
                Source = store ? "Store" : "",
                ActionLabel = "Başlat",
                ActionGlyph = Glyphs.Play,
            };
        }
        return byName.Values.OrderBy(a => a.Title, StringComparer.Create(Fmt.Tr, true)).ToList();
    }

    static List<TileItem> StartMenu()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var byName = new Dictionary<string, TileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", opts))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (Skip.IsMatch(name) || byName.ContainsKey(name)) continue;
                byName[name] = new TileItem
                {
                    Kind = TileKind.App,
                    Title = name,
                    Path = lnk,
                    Key = "app:" + name.ToLowerInvariant(),
                    TileBrush = Theme.Translucent,
                    Subtitle = "Uygulama",
                    ActionLabel = "Başlat",
                    ActionGlyph = Glyphs.Play,
                };
            }
        }
        return byName.Values.OrderBy(a => a.Title, StringComparer.Create(Fmt.Tr, true)).ToList();
    }
}

public enum MediaKind { None, Image, Video, Audio }

public static class MediaTypes
{
    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".jfif" };
    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp" };
    static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg" };
    static readonly HashSet<string> Texts = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".log", ".json", ".xml", ".csv", ".ini", ".cfg", ".yaml", ".yml", ".cs", ".py", ".js", ".ts",
          ".html", ".css", ".c", ".cpp", ".h", ".java", ".bat", ".ps1", ".sql", ".toml", ".gd", ".srt" };

    public static MediaKind KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return MediaKind.Image;
        if (Videos.Contains(ext)) return MediaKind.Video;
        if (Audio.Contains(ext)) return MediaKind.Audio;
        return MediaKind.None;
    }

    public static bool IsText(string path) => Texts.Contains(Path.GetExtension(path));
    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static string Label(MediaKind k) => k switch
    {
        MediaKind.Image => "Fotoğraf",
        MediaKind.Video => "Video",
        MediaKind.Audio => "Müzik",
        _ => "Dosya",
    };

    /// <summary>Aynı klasördeki aynı türden dosyalar: görüntüleyicide önceki/sonraki için.</summary>
    public static List<string> Siblings(string path)
    {
        var kind = KindOf(path);
        var list = new List<string>();
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            var opts = new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            list = Directory.EnumerateFiles(dir, "*", opts).Where(f => KindOf(f) == kind).ToList();
            list.Sort(NaturalComparer.Instance);
        }
        catch { }
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Insert(0, path);
        return list;
    }
}

/// <summary>Windows Gezgini ile aynı "doğal" sıralama (dosya2 &lt; dosya10).</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
