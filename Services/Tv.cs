using System.Windows.Media;
using Nova.Core;
using Nova.Models;

namespace Nova.Services;

/// <summary>TV ve Video sekmesi: yayın servislerini tarayıcının tam ekran uygulama modunda açar.</summary>
public static class Tv
{
    static readonly (string Name, string Url, string Color)[] Builtin =
    {
        ("YouTube", "https://www.youtube.com", "#E02B2B"),
        ("Netflix", "https://www.netflix.com", "#B20710"),
        ("Disney+", "https://www.disneyplus.com", "#0E3B8C"),
        ("Prime Video", "https://www.primevideo.com", "#1A98FF"),
        ("HBO Max", "https://www.hbomax.com", "#5822B4"),
        ("Exxen", "https://www.exxen.com", "#F2B705"),
        ("tabii", "https://www.tabii.com", "#0B8A6F"),
        ("Twitch", "https://www.twitch.tv", "#7A3EE8"),
        ("Spotify", "https://open.spotify.com", "#1DB954"),
    };

    static string? _edge;

    static string? EdgePath => _edge ??= new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
    }.FirstOrDefault(File.Exists);

    public static List<TileItem> Items()
    {
        var list = Builtin.Select(b => Tile(b.Name, b.Url, Theme.Hex(b.Color), custom: false)).ToList();
        list.AddRange(AppSettings.Current.TvServices.Select(s => Tile(s.Name, s.Url, Theme.Slate, custom: true)));
        list.Add(new TileItem
        {
            Kind = TileKind.Action,
            Title = "Site ekle",
            Subtitle = "Sık izlediğin bir siteyi bu sekmeye ekle",
            InitGlyph = Glyphs.Add,
            TileBrush = Theme.Translucent,
            ActionLabel = "Ekle",
            ActionGlyph = Glyphs.Add,
            Tag = "add-tv",
        });
        return list;
    }

    static TileItem Tile(string name, string url, Color color, bool custom)
    {
        var host = new Uri(url).Host;
        var edge = EdgePath;
        return new TileItem
        {
            Kind = TileKind.App,
            Title = name,
            Key = "tv:" + host,
            Path = edge,
            LaunchArgs = edge != null ? $"--app={url} --start-fullscreen" : null,
            LaunchUri = edge == null ? url : null,
            InitGlyph = Glyphs.Tv,
            Accent = color,
            TileBrush = Theme.Tile(color),
            Subtitle = (custom ? "Eklediğin site" : "Yayın servisi") + "  ·  " + host,
            ActionLabel = "İzle",
            ActionGlyph = Glyphs.Play,
            IsCustom = custom,
            Tag = url,
        };
    }

    /// <summary>Servis simgelerini indirip kartlara uygular (önbellekte varsa ağa çıkmaz).</summary>
    public static async Task LoadIconsAsync(IEnumerable<TileItem> items)
    {
        var dir = Path.Combine(App.DataDir, "art");
        Directory.CreateDirectory(dir);
        foreach (var it in items.Where(i => i.Key.StartsWith("tv:")).ToList())
        {
            var host = it.Key[3..];
            var file = Path.Combine(dir, "tv_" + host + ".png");
            try
            {
                if (!File.Exists(file))
                {
                    var bytes = await Net.Http.GetByteArrayAsync($"https://www.google.com/s2/favicons?domain={host}&sz=256");
                    if (bytes.Length < 200) continue;
                    await File.WriteAllBytesAsync(file, bytes);
                }
                it.SetIconFile(file);
            }
            catch { }
        }
    }
}
