using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Nova.Core;

/// <summary>Segoe Fluent Icons glifleri.</summary>
public static class Glyphs
{
    public const string Folder = "";
    public const string Desktop = "";
    public const string Documents = "";
    public const string Downloads = "";
    public const string Pictures = "";
    public const string Videos = "";
    public const string Music = "";
    public const string Drive = "";
    public const string Usb = "";
    public const string Disc = "";
    public const string Network = "";
    public const string Add = "";
    public const string Play = "";
    public const string Pause = "";
    public const string More = "";
    public const string Power = "";
    public const string Game = "";
    public const string Error = "";
    public const string Info = "";
    public const string Sleep = "";
    public const string Restart = "";
    public const string Minimize = "";
    public const string Close = "";
    public const string Copy = "";
    public const string OpenFolder = "";
    public const string Delete = "";
    public const string Volume = "";
    public const string Mute = "";
    public const string Apps = "";
    public const string Open = "";
    public const string View = "";
    public const string Search = "";
    public const string Settings = "";
    public const string Pin = "";
    public const string Unpin = "";
    public const string Wifi = "";
    public const string Ethernet = "";
    public const string NoNetwork = "";
    public const string Battery = "";
    public const string Charging = "";
    public const string Brightness = "";
    public const string Bell = "";
    public const string Previous = "";
    public const string Next = "";
    public const string Cut = "";
    public const string Paste = "";
    public const string Rename = "";
    public const string NewFolder = "";
    public const string Sort = "";
    public const string Palette = "";
    public const string Picture = "";
    public const string Timer = "";
    public const string Key = "";
    public const string Refresh = "";
    public const string Backspace = "";
    public const string Shift = "";
    public const string Accept = "";
    public const string Pdf = "";
    public const string Text = "";
    public const string Store = "";
    public const string Home = "";
    public static readonly string Back = "";
    public static readonly string Trophy = "";
    public static readonly string Calendar = "";
    public static readonly string Cpu = "";
    public static readonly string Phone = "";
    public static readonly string Person = "";
    public static readonly string AddFriend = "";
    public static readonly string Tv = "";
    public static readonly string Globe = "";
    public static readonly string Language = "";
    public static readonly string Install = "";
    public static readonly string Download = "";
    public static readonly string News = "";
}

/// <summary>Arka plan renk temaları.</summary>
public sealed record ThemeDef(string Name, Color Center, Color Mid, Color Edge);

public static class Themes
{
    public static readonly ThemeDef[] All =
    {
        new("Gece Mavisi", Theme.Hex("#173A80"), Theme.Hex("#0B1B40"), Theme.Hex("#040811")),
        new("Kozmik Mor", Theme.Hex("#4A2A8C"), Theme.Hex("#1D1242"), Theme.Hex("#07040F")),
        new("Okyanus", Theme.Hex("#0E6273"), Theme.Hex("#0A2B3B"), Theme.Hex("#03080C")),
        new("Gün Batımı", Theme.Hex("#8C3B2A"), Theme.Hex("#3A1628"), Theme.Hex("#0C0508")),
        new("Orman", Theme.Hex("#1F5E3C"), Theme.Hex("#0C2719"), Theme.Hex("#030805")),
        new("Grafit", Theme.Hex("#3B404C"), Theme.Hex("#16181E"), Theme.Hex("#050506")),
    };

    public static ThemeDef ByName(string? name) => All.FirstOrDefault(t => t.Name == name) ?? All[0];

    public static Brush Background(ThemeDef t)
    {
        var b = new RadialGradientBrush
        {
            Center = new Point(0.78, 0.05),
            GradientOrigin = new Point(0.78, 0.05),
            RadiusX = 1.1,
            RadiusY = 1.35,
        };
        b.GradientStops.Add(new GradientStop(t.Center, 0));
        b.GradientStops.Add(new GradientStop(t.Mid, 0.42));
        b.GradientStops.Add(new GradientStop(t.Edge, 1));
        b.Freeze();
        return b;
    }
}

public static class Theme
{
    public static readonly Brush Translucent = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)));

    public static readonly Color Blue = Hex("#2F6FEB");
    public static readonly Color Violet = Hex("#7C5CE0");
    public static readonly Color Green = Hex("#12A37F");
    public static readonly Color Amber = Hex("#E59A14");
    public static readonly Color Red = Hex("#E0484E");
    public static readonly Color Pink = Hex("#D9468F");
    public static readonly Color Cyan = Hex("#1593C9");
    public static readonly Color Slate = Hex("#3C4A68");

    public static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    public static Brush Tile(Color c)
    {
        var b = new LinearGradientBrush(Shade(c, 1.18), Shade(c, 0.62), new Point(0, 0), new Point(1, 1));
        b.Freeze();
        return b;
    }

    public static Color Shade(Color c, double f) => Color.FromArgb(c.A,
        (byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));

    public static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}

public static class Fmt
{
    /// <summary>Seçili dile göre biçimlendirme kültürü (tarih, sayı).</summary>
    public static CultureInfo Tr => L.Culture;

    public static string Size(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v < 10 ? "0.0" : "0", Tr) + " " + units[u];
    }

    public static string Date(DateTime d) => d.ToString("d MMMM yyyy, HH:mm", Tr);

    public static string Playtime(double minutes) => minutes switch
    {
        < 1 => "",
        < 60 => $"{(int)minutes} dakika oynandı",
        < 600 => $"{minutes / 60:0.#} saat oynandı".Replace('.', ','),
        _ => $"{(int)(minutes / 60)} saat oynandı",
    };

    public static string Ago(long unixSeconds)
    {
        if (unixSeconds <= 0) return "Henüz oynanmadı";
        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (span.TotalMinutes < 2) return "Son oynama: az önce";
        if (span.TotalHours < 1) return $"Son oynama: {(int)span.TotalMinutes} dakika önce";
        if (span.TotalDays < 1) return $"Son oynama: {(int)span.TotalHours} saat önce";
        if (span.TotalDays < 30) return $"Son oynama: {(int)span.TotalDays} gün önce";
        if (span.TotalDays < 365) return $"Son oynama: {(int)(span.TotalDays / 30)} ay önce";
        return $"Son oynama: {(int)(span.TotalDays / 365)} yıl önce";
    }
}
