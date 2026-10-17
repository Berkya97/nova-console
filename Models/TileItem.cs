using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nova.Core;

namespace Nova.Models;

public enum TileKind { Game, App, Folder, Drive, File, Action }

/// <summary>Ekrandaki her kart: oyun, uygulama, klasör, sürücü, dosya veya özel eylem.</summary>
public sealed class TileItem : INotifyPropertyChanged
{
    public TileKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>Kartın altındaki küçük ikinci satır (ör. disk boş alanı).</summary>
    public string Caption { get; init; } = "";
    public string? Path { get; init; }
    public string? LaunchUri { get; init; }
    /// <summary>Simge bu kabuk yolundan alınır (Store uygulamaları için shell:AppsFolder\...).</summary>
    public string? IconPath { get; init; }
    public string Key { get; init; } = "";
    public string? Glyph { get; private set; }
    public Brush? TileBrush { get; init; }
    public Color? Accent { get; init; }
    public string? CoverPath { get; private set; }
    public string? HeroPath { get; private set; }
    public string? LogoPath { get; private set; }
    public string ActionLabel { get; init; } = "Aç";
    public string ActionGlyph { get; init; } = Glyphs.Open;
    public double UsageWidth { get; init; }
    public Visibility UsageVisibility => UsageWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsCustom { get; init; }
    public object? Tag { get; init; }
    public long LastPlayed { get; set; }
    /// <summary>Oyunun kurulu olduğu klasör: oynama süresi ölçümü için.</summary>
    public string? InstallDir { get; init; }
    public double PlaytimeMinutes { get; set; }
    /// <summary>Platform adı: Steam, Epic Games, GOG, Xbox…</summary>
    public string Source { get; init; } = "";
    /// <summary>Path ile birlikte verilen komut satırı (ör. tarayıcıyı uygulama modunda açmak).</summary>
    public string? LaunchArgs { get; init; }
    /// <summary>Steam appmanifest dosyası: indirme / güncelleme ilerlemesini izlemek için.</summary>
    public string? StatusFile { get; init; }
    /// <summary>Kapak yerine ortalanmış simge olarak gösterilecek görsel dosyası.</summary>
    public string? IconFile { get; private set; }

    double _progress = double.NaN;
    /// <summary>0–1 arası indirme ilerlemesi; NaN ise çubuk gizli.</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (_progress.Equals(value)) return;
            _progress = value;
            Changed();
            Changed(nameof(ProgressVisibility));
        }
    }
    public Visibility ProgressVisibility => double.IsNaN(_progress) ? Visibility.Collapsed : Visibility.Visible;

    public void SetIconFile(string file)
    {
        IconFile = file;
        Glyph = null;
        Changed(nameof(Glyph));
        _thumbRequested = false;
        _thumb = null;
        EnsureThumb();
    }

    public void SetSubtitle(string s)
    {
        if (Subtitle == s) return;
        Subtitle = s;
        Changed(nameof(Subtitle));
    }

    // Nesne başlatıcıyla verilebilen görsel bilgileri.
    public string? InitGlyph { init => Glyph = value; }
    public string? InitCover { init => CoverPath = value; }
    public string? InitHero { init => HeroPath = value; }
    public string? InitLogo { init => LogoPath = value; }

    /// <summary>Kullanıcı bu öğenin bulunduğu klasörden ayrıldıysa bekleyen küçük resim işini atla.</summary>
    public bool Abandoned { get; set; }

    bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Changed(); } }
    }

    bool _pinned;
    public bool IsPinned
    {
        get => _pinned;
        set
        {
            if (_pinned == value) return;
            _pinned = value;
            Changed();
            Changed(nameof(PinVisibility));
        }
    }
    public Visibility PinVisibility => _pinned ? Visibility.Visible : Visibility.Collapsed;

    ImageSource? _thumb;
    bool _thumbRequested;

    public bool ThumbIsIcon { get; private set; }

    /// <summary>İlk okunduğunda (kart ekrana geldiğinde) yüklemeyi başlatır.</summary>
    public ImageSource? Thumb
    {
        get { EnsureThumb(); return _thumb; }
    }

    public ImageSource? FillImage => Thumb is { } t && !ThumbIsIcon ? t : null;
    public ImageSource? IconImage => Thumb is { } t && ThumbIsIcon ? t : null;

    public void EnsureThumb()
    {
        if (_thumbRequested) return;
        _thumbRequested = true;
        var shellPath = IconPath ?? Path;
        if (CoverPath == null && IconFile == null && (shellPath == null || Glyph != null)) return;
        _ = LoadThumbAsync();
    }

    async Task LoadThumbAsync()
    {
        try
        {
            if (CoverPath != null)
            {
                var cover = await ImageCache.LoadAsync(CoverPath, 320);
                if (cover != null) { SetThumb(cover, false); return; }
            }
            if (IconFile != null)
            {
                var icon = await ImageCache.LoadAsync(IconFile, 256);
                if (icon != null) { SetThumb(icon, true); return; }
            }

            var path = IconPath ?? Path;
            if (path == null || Glyph != null) return;
            var (img, isIcon) = await StaWorker.Run(() => Abandoned ? ((BitmapSource?)null, false) : ShellThumbnail.Get(path, 256));
            if (img != null) SetThumb(img, isIcon);
        }
        catch { }
    }

    void SetThumb(ImageSource img, bool isIcon)
    {
        _thumb = img;
        ThumbIsIcon = isIcon;
        Changed(nameof(Thumb));
        Changed(nameof(FillImage));
        Changed(nameof(IconImage));
    }

    /// <summary>Sonradan indirilen kapak / arka plan / logo görsellerini uygular.</summary>
    public void SetArt(string? cover, string? hero, string? logo)
    {
        if (hero != null) { HeroPath = hero; Changed(nameof(HeroPath)); }
        if (logo != null) { LogoPath = logo; Changed(nameof(LogoPath)); }
        if (cover != null)
        {
            CoverPath = cover;
            Glyph = null;
            Changed(nameof(Glyph));
            _thumbRequested = false;
            _thumb = null;
            EnsureThumb();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
