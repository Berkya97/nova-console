using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Nova.Core;
using Nova.Services;

namespace Nova.Views;

/// <summary>"Kim oynuyor?" kullanıcı seçim ekranı.</summary>
public sealed class ProfilePicker : Grid, IOverlay
{
    public static readonly Color[] Colors =
    {
        Theme.Hex("#5B8CFF"), Theme.Hex("#E0484E"), Theme.Hex("#12A37F"), Theme.Hex("#E59A14"),
        Theme.Hex("#D9468F"), Theme.Hex("#7C5CE0"), Theme.Hex("#1593C9"), Theme.Hex("#5C6B8A"),
    };

    readonly List<(Profile? Profile, Border View)> _items = new();
    readonly bool _cancellable;
    int _sel;
    bool _closing;

    public event Action? Closed;
    public event Action<Profile>? Picked;
    public event Action? AddRequested;

    public ProfilePicker(ProfileStore store, BitmapSource? mainPhoto, bool cancellable)
    {
        _cancellable = cancellable;
        Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x05, 0x08, 0x12));
        var (box, canvas) = Ui.Stage();
        Children.Add(box);

        var title = Ui.Text("Kim oynuyor?", 54, weight: FontWeights.Light);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.VerticalAlignment = VerticalAlignment.Top;
        title.Margin = new Thickness(0, 250, 0, 0);
        canvas.Children.Add(title);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };
        foreach (var p in store.Profiles)
            row.Children.Add(Item(p, p.Id == "main" ? mainPhoto : null));
        row.Children.Add(Item(null, null));
        canvas.Children.Add(row);

        _sel = Math.Max(0, store.Profiles.FindIndex(p => p.Id == store.LastId));
        Loaded += (_, _) => { Ui.FadeIn(box, 20); Refresh(); };
    }

    FrameworkElement Item(Profile? p, BitmapSource? photo)
    {
        var circle = new Grid { Width = 170, Height = 170 };
        var color = p == null ? Color.FromArgb(0x26, 255, 255, 255) : Colors[p.Color % Colors.Length];
        circle.Children.Add(new Ellipse { Fill = new LinearGradientBrush(Theme.Shade(color, 1.2), Theme.Shade(color, 0.7), 45) });
        if (photo != null) circle.Children.Add(new Ellipse { Fill = new ImageBrush(photo) { Stretch = Stretch.UniformToFill } });
        else if (p == null) circle.Children.Add(Ui.Glyph(Glyphs.Add, 56));
        else
        {
            var initial = Ui.Text(p.Name.Length > 0 ? p.Name[..1].ToUpper(Fmt.Tr) : "?", 72, weight: FontWeights.SemiBold);
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            circle.Children.Add(initial);
        }
        var ring = new Border { CornerRadius = new CornerRadius(95), BorderThickness = new Thickness(4), Margin = new Thickness(-10), Child = circle };
        var name = Ui.Text(p?.Name ?? "Kullanıcı ekle", 24);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Margin = new Thickness(0, 26, 0, 0);
        var stack = new StackPanel { Margin = new Thickness(40, 0, 40, 0), Cursor = Cursors.Hand };
        stack.Children.Add(ring);
        stack.Children.Add(name);
        var entry = (p, ring);
        _items.Add(entry);
        stack.MouseLeftButtonUp += (_, _) => { _sel = _items.IndexOf(entry); Sfx.Confirm(); Choose(); };
        return stack;
    }

    void Refresh()
    {
        for (int i = 0; i < _items.Count; i++) Ui.SetRing(_items[i].View, i == _sel);
    }

    public IEnumerable<(NavAction, string)> Hints => _cancellable
        ? new[] { (NavAction.Confirm, "Seç"), (NavAction.Back, "Vazgeç") }
        : new[] { (NavAction.Confirm, "Seç") };

    public bool Handle(NavAction a)
    {
        switch (a)
        {
            case NavAction.Left: if (_sel == 0) return false; _sel--; Refresh(); return true;
            case NavAction.Right: if (_sel == _items.Count - 1) return false; _sel++; Refresh(); return true;
            case NavAction.Confirm: Choose(); return true;
            case NavAction.Back when _cancellable: Close(); return true;
        }
        return false;
    }

    void Choose()
    {
        var p = _items[_sel].Profile;
        if (p == null) { AddRequested?.Invoke(); return; }
        Close();
        Picked?.Invoke(p);
    }

    public void Close()
    {
        if (_closing) return;
        _closing = true;
        Ui.FadeOut(this, () => Closed?.Invoke());
    }
}

/// <summary>Kupalar ekranı: Nova kupaları ve (varsa) seçili Steam oyununun başarımları.</summary>
public sealed class TrophiesView : Grid, IOverlay
{
    readonly ScrollViewer _scroll;
    readonly StackPanel _list = new();
    double _offset;
    bool _closing;

    public event Action? Closed;

    public TrophiesView(string? steamAppId, string? gameTitle)
    {
        Background = Ui.Backdrop;
        var (box, canvas) = Ui.Stage();
        Children.Add(box);

        var title = Ui.Text("Kupalar", 54, weight: FontWeights.Light);
        title.VerticalAlignment = VerticalAlignment.Top;
        title.Margin = new Thickness(120, 100, 0, 0);
        canvas.Children.Add(title);

        _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Margin = new Thickness(120, 200, 120, 90), Focusable = false };
        canvas.Children.Add(_scroll);

        Section($"Nova kupaları  ·  {Trophies.Earned} / {Trophies.All.Length}");
        var s = AppSettings.Current;
        var wrap = new WrapPanel();
        foreach (var d in Trophies.All)
        {
            bool won = s.Trophies.TryGetValue(d.Id, out var when);
            wrap.Children.Add(TrophyCard(d.Glyph, d.Title, d.Description, won,
                won ? DateTimeOffset.FromUnixTimeSeconds(when).LocalDateTime.ToString("d MMM yyyy", Fmt.Tr) : null, d.Tier, null));
        }
        _list.Children.Add(wrap);

        if (steamAppId != null) _ = LoadSteam(steamAppId, gameTitle ?? "");
        Loaded += (_, _) => Ui.FadeIn(box, 20);
    }

    void Section(string text)
    {
        var t = Ui.Text(text, 22, Ui.Dim, FontWeights.SemiBold);
        t.Margin = new Thickness(0, _list.Children.Count == 0 ? 0 : 36, 0, 16);
        _list.Children.Add(t);
    }

    static Border TrophyCard(string glyph, string title, string desc, bool won, string? date, string tier, string? iconUrl)
    {
        var tierColor = tier switch
        {
            "platin" => Theme.Hex("#B8D8FF"),
            "altın" => Theme.Hex("#F2C14E"),
            "gümüş" => Theme.Hex("#C9D1DC"),
            _ => Theme.Hex("#D08B5B"),
        };
        var icon = new Grid { Width = 64, Height = 64 };
        icon.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(won ? Color.FromArgb(0x40, tierColor.R, tierColor.G, tierColor.B) : Color.FromArgb(0x1A, 255, 255, 255)) });
        if (iconUrl != null)
        {
            try { icon.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = new ImageBrush(new BitmapImage(new Uri(iconUrl))) }); }
            catch { }
        }
        else icon.Children.Add(Ui.Glyph(won ? Glyphs.Trophy : glyph, 28, won ? new SolidColorBrush(tierColor) : Ui.Faint));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0), Width = 340 };
        text.Children.Add(Ui.Text(title, 20, won ? Brushes.White : Ui.Dim, FontWeights.SemiBold));
        var d = Ui.Text(desc, 15, Ui.Faint);
        d.TextWrapping = TextWrapping.Wrap;
        d.MaxHeight = 40;
        text.Children.Add(d);
        if (date != null) text.Children.Add(Ui.Text(date, 14, new SolidColorBrush(tierColor)));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        return new Border
        {
            Width = 530, Height = 110, Margin = new Thickness(0, 0, 18, 18), Padding = new Thickness(20, 0, 20, 0),
            CornerRadius = new CornerRadius(18), Background = Ui.CardBg, Opacity = won ? 1 : 0.6, Child = row,
        };
    }

    async Task LoadSteam(string appId, string game)
    {
        Section($"{game}  ·  Steam başarımları");
        var status = Ui.Text("Yükleniyor…", 18, Ui.Faint);
        _list.Children.Add(status);
        var (list, error) = await SteamOnline.AchievementsAsync(appId, AppSettings.Current.SteamApiKey);
        _list.Children.Remove(status);
        if (error != null || list.Count == 0)
        {
            var t = Ui.Text(error ?? "Bu oyunun başarımı yok", 18, Ui.Faint);
            t.TextWrapping = TextWrapping.Wrap;
            _list.Children.Add(t);
            if (error?.Contains("anahtar") == true)
                _list.Children.Add(Ui.Text("Ayarlar › Steam Web API anahtarı bölümünden ekleyebilirsin.", 16, Ui.Faint));
            return;
        }
        var done = list.Count(a => a.Achieved);
        ((TextBlock)_list.Children[^1]).Text = $"{game}  ·  Steam başarımları  ·  {done} / {list.Count}";
        var wrap = new WrapPanel();
        foreach (var a in list)
            wrap.Children.Add(TrophyCard(Glyphs.Trophy, a.Name, a.Description, a.Achieved, a.Unlocked?.ToString("d MMM yyyy", Fmt.Tr), "altın", a.Icon));
        _list.Children.Add(wrap);
    }

    public IEnumerable<(NavAction, string)> Hints => new[] { (NavAction.Up, "Kaydır"), (NavAction.Back, "Kapat") };

    public bool Handle(NavAction a)
    {
        switch (a)
        {
            case NavAction.Up: Scroll(-160); return true;
            case NavAction.Down: Scroll(160); return true;
            case NavAction.PageUp: Scroll(-600); return true;
            case NavAction.PageDown: Scroll(600); return true;
            case NavAction.Back:
            case NavAction.Menu:
                if (_closing) return false;
                _closing = true;
                Ui.FadeOut(this, () => Closed?.Invoke());
                return true;
        }
        return false;
    }

    void Scroll(double d)
    {
        _offset = Math.Clamp(_offset + d, 0, Math.Max(0, _scroll.ExtentHeight - _scroll.ViewportHeight));
        _scroll.ScrollToVerticalOffset(_offset);
    }
}
