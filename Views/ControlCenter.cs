using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nova.Core;

namespace Nova.Views;

/// <summary>Alttan açılan hızlı panel: çalan medya, ses, parlaklık, ağ, pil, bildirimler.</summary>
public sealed class ControlCenter : Grid, IOverlay
{
    sealed class Card
    {
        public Border View = null!;
        public string ConfirmLabel = "Seç";
        public Action? Confirm;
        public Action<int>? Adjust;
    }

    readonly List<Card> _cards = new();
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Border _panel;
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    int _sel;
    bool _closing;

    // Kart içerikleri
    readonly TextBlock _mediaTitle = Ui.Text("Şu an bir şey çalmıyor", 22, weight: FontWeights.SemiBold);
    readonly TextBlock _mediaSub = Ui.Text("Spotify, tarayıcı veya başka bir oynatıcı başlat", 16, Ui.Dim);
    readonly ImageBrush _mediaArt = new() { Stretch = Stretch.UniformToFill };
    readonly TextBlock _mediaGlyph = Ui.Glyph(Glyphs.Music, 42, Ui.Faint);
    readonly TextBlock _playGlyph = Ui.Glyph(Glyphs.Play, 30);
    (TextBlock Value, Border Fill) _volume, _brightness;
    readonly TextBlock _netName = Ui.Text("…", 18, weight: FontWeights.SemiBold);
    readonly TextBlock _netGlyph = Ui.Glyph(Glyphs.Wifi, 34);
    int? _vol, _bright;

    public event Action? Closed;
    public event Action? OpenSettings, OpenPower, OpenNotifications;

    public ControlCenter(int notificationCount)
    {
        Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
        var (box, canvas) = Ui.Stage();
        Children.Add(box);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 26) };
        var clock = Ui.Text(DateTime.Now.ToString("HH:mm", Fmt.Tr), 46, weight: FontWeights.Light);
        var date = Ui.Text(DateTime.Now.ToString("d MMMM dddd", Fmt.Tr), 20, Ui.Dim);
        date.Margin = new Thickness(20, 12, 0, 0);
        header.Children.Add(clock);
        header.Children.Add(date);
        var title = Ui.Text("Kontrol Merkezi", 20, Ui.Faint);
        title.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(title, Dock.Right);
        header.Children.Insert(0, title);

        var content = new StackPanel { Margin = new Thickness(120, 36, 120, 0) };
        content.Children.Add(header);
        content.Children.Add(_row);
        _panel = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 400,
            CornerRadius = new CornerRadius(34, 34, 0, 0),
            Background = new LinearGradientBrush(Color.FromArgb(0xF5, 0x16, 0x1E, 0x33), Color.FromArgb(0xFA, 0x0A, 0x0E, 0x1A), 90),
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = content,
            RenderTransform = new TranslateTransform(0, 400),
        };
        canvas.Children.Add(_panel);

        BuildCards(notificationCount);
        _refresh.Tick += async (_, _) => await RefreshMedia();
        Loaded += async (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ((TranslateTransform)_panel.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            Select(0);
            _refresh.Start();
            await LoadAsync();
        };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == this) Close(); };
    }

    void BuildCards(int notifications)
    {
        // Medya
        var art = new Grid { Width = 120, Height = 120 };
        art.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = Ui.CardBg });
        art.Children.Add(_mediaGlyph);
        art.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = _mediaArt });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0), Width = 230 };
        info.Children.Add(Ui.Text("Şimdi çalıyor", 15, Ui.Faint));
        _mediaTitle.Margin = new Thickness(0, 6, 0, 4);
        info.Children.Add(_mediaTitle);
        info.Children.Add(_mediaSub);
        var media = new StackPanel { Orientation = Orientation.Horizontal };
        media.Children.Add(art);
        media.Children.Add(info);
        AddCard(media, 420, "Oynat / Duraklat", async () => { await NowPlaying.PlayPause(); await Task.Delay(300); await RefreshMedia(); });

        AddCard(Ui.Glyph(Glyphs.Previous, 30), 84, "Önceki", async () => { await NowPlaying.Previous(); await Task.Delay(500); await RefreshMedia(); });
        AddCard(_playGlyph, 84, "Oynat / Duraklat", async () => { await NowPlaying.PlayPause(); await Task.Delay(300); await RefreshMedia(); });
        AddCard(Ui.Glyph(Glyphs.Next, 30), 84, "Sonraki", async () => { await NowPlaying.Next(); await Task.Delay(500); await RefreshMedia(); });

        _volume = AddSlider(Glyphs.Volume, "Ses", d => SetVolume(d));
        _brightness = AddSlider(Glyphs.Brightness, "Parlaklık", d => SetBrightness(d));

        var net = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        net.Children.Add(_netGlyph);
        _netName.HorizontalAlignment = HorizontalAlignment.Center;
        _netName.Margin = new Thickness(0, 16, 0, 2);
        net.Children.Add(_netName);
        var netSub = Ui.Text("Ağ ayarları", 15, Ui.Dim);
        netSub.HorizontalAlignment = HorizontalAlignment.Center;
        net.Children.Add(netSub);
        AddCard(net, 164, "Ağ ayarları", () => OpenUri("ms-settings:network-status"));

        if (Native.Battery() is { } bat)
        {
            var b = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            b.Children.Add(Ui.Glyph(bat.Charging ? Glyphs.Charging : Glyphs.Battery, 34));
            var v = Ui.Text($"%{bat.Percent}", 26, weight: FontWeights.SemiBold);
            v.HorizontalAlignment = HorizontalAlignment.Center;
            v.Margin = new Thickness(0, 12, 0, 0);
            b.Children.Add(v);
            var s = Ui.Text(bat.Charging ? "Şarj oluyor" : "Pil", 15, Ui.Dim);
            s.HorizontalAlignment = HorizontalAlignment.Center;
            b.Children.Add(s);
            AddCard(b, 130, "Güç ayarları", () => OpenUri("ms-settings:batterysaver"));
        }

        AddCard(IconCard(Glyphs.Bell, notifications > 0 ? $"{notifications} bildirim" : "Bildirim yok"), 130, "Bildirimler",
            () => { Close(); OpenNotifications?.Invoke(); });
        AddCard(IconCard(Glyphs.Settings, "Ayarlar"), 124, "Ayarlar", () => { Close(); OpenSettings?.Invoke(); });
        AddCard(IconCard(Glyphs.Power, "Güç"), 110, "Güç", () => { Close(); OpenPower?.Invoke(); });
    }

    static FrameworkElement IconCard(string glyph, string label)
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(Ui.Glyph(glyph, 34));
        var t = Ui.Text(label, 17);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 18, 0, 0);
        p.Children.Add(t);
        return p;
    }

    (TextBlock, Border) AddSlider(string glyph, string label, Action<int> adjust)
    {
        var value = Ui.Text("—", 26, weight: FontWeights.SemiBold);
        var fill = new Border { CornerRadius = new CornerRadius(3), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var bar = new Grid { Height = 6, Width = 116, Margin = new Thickness(0, 14, 0, 0) };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Ui.Line });
        bar.Children.Add(fill);
        var top = new DockPanel();
        top.Children.Add(Ui.Glyph(glyph, 26));
        value.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(value);
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 116 };
        p.Children.Add(top);
        p.Children.Add(bar);
        var l = Ui.Text(label, 15, Ui.Dim);
        l.Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(l);
        AddCard(p, 164, "Ayarla", null, adjust);
        return (value, fill);
    }

    void AddCard(FrameworkElement content, double width, string label, Action? confirm, Action<int>? adjust = null)
    {
        var view = new Border
        {
            Width = width,
            Height = 186,
            Margin = new Thickness(0, 0, 12, 0),
            CornerRadius = new CornerRadius(20),
            Background = Ui.CardBg,
            BorderThickness = new Thickness(3),
            Padding = new Thickness(18),
            Child = content,
            Cursor = Cursors.Hand,
        };
        var card = new Card { View = view, ConfirmLabel = label, Confirm = confirm, Adjust = adjust };
        view.MouseLeftButtonUp += (_, _) => { Select(_cards.IndexOf(card)); Sfx.Confirm(); card.Confirm?.Invoke(); };
        view.MouseWheel += (_, e) => card.Adjust?.Invoke(e.Delta > 0 ? 5 : -5);
        _cards.Add(card);
        _row.Children.Add(view);
    }

    async Task LoadAsync()
    {
        var volTask = Task.Run(SystemVolume.Get);
        var brightTask = Task.Run(Brightness.Get);
        var net = Network.Current();
        _netName.Text = net?.Name ?? "Bağlantı yok";
        _netGlyph.Text = net == null ? Glyphs.NoNetwork : net.Value.Wireless ? Glyphs.Wifi : Glyphs.Ethernet;
        await RefreshMedia();
        _vol = await volTask;
        ShowSlider(_volume, _vol);
        _bright = await brightTask;
        ShowSlider(_brightness, _bright);
        if (_bright == null) ((FrameworkElement)_brightness.Value.Parent).Parent.SetValue(OpacityProperty, 0.4);
    }

    static void ShowSlider((TextBlock Value, Border Fill) s, int? v)
    {
        s.Value.Text = v is { } x ? $"%{x}" : "—";
        s.Fill.Width = (v ?? 0) / 100.0 * 116;
    }

    async Task RefreshMedia()
    {
        var info = await NowPlaying.GetAsync();
        if (info == null || info.Title.Length == 0)
        {
            _mediaTitle.Text = "Şu an bir şey çalmıyor";
            _mediaSub.Text = "Spotify, tarayıcı veya başka bir oynatıcı başlat";
            _mediaArt.ImageSource = null;
            _playGlyph.Text = Glyphs.Play;
            return;
        }
        _mediaTitle.Text = info.Title;
        _mediaSub.Text = string.Join("  ·  ", new[] { info.Artist, info.App }.Where(s => s.Length > 0));
        _mediaArt.ImageSource = info.Art;
        _playGlyph.Text = info.Playing ? Glyphs.Pause : Glyphs.Play;
    }

    void SetVolume(int delta)
    {
        if (_vol == null) return;
        _vol = Math.Clamp(_vol.Value + delta, 0, 100);
        ShowSlider(_volume, _vol);
        var v = _vol.Value;
        Task.Run(() => SystemVolume.Set(v));
    }

    void SetBrightness(int delta)
    {
        if (_bright == null) return;
        _bright = Math.Clamp(_bright.Value + delta, 0, 100);
        ShowSlider(_brightness, _bright);
        var v = _bright.Value;
        Task.Run(() => Brightness.Set(v));
    }

    static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    void Select(int i)
    {
        _sel = Math.Clamp(i, 0, _cards.Count - 1);
        for (int k = 0; k < _cards.Count; k++)
        {
            Ui.SetRing(_cards[k].View, k == _sel);
            _cards[k].View.Background = k == _sel ? new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)) : Ui.CardBg;
        }
    }

    public IEnumerable<(NavAction, string)> Hints
    {
        get
        {
            var c = _cards.ElementAtOrDefault(_sel);
            if (c?.Adjust != null) yield return (NavAction.Up, "Ayarla");
            if (c?.Confirm != null) yield return (NavAction.Confirm, c.ConfirmLabel);
            yield return (NavAction.Left, "Kartlar");
            yield return (NavAction.Back, "Kapat");
        }
    }

    public bool Handle(NavAction a)
    {
        var card = _cards[_sel];
        switch (a)
        {
            case NavAction.Left:
                if (_sel == 0) return false;
                Select(_sel - 1); return true;
            case NavAction.Right:
                if (_sel == _cards.Count - 1) return false;
                Select(_sel + 1); return true;
            case NavAction.Up:
            case NavAction.Down:
                if (card.Adjust == null) return false;
                card.Adjust(a == NavAction.Up ? 5 : -5);
                return true;
            case NavAction.Confirm:
                card.Confirm?.Invoke();
                return card.Confirm != null;
            case NavAction.Back:
            case NavAction.Control:
            case NavAction.Menu:
                Close();
                return true;
        }
        return false;
    }

    public void Close()
    {
        if (_closing) return;
        _closing = true;
        _refresh.Stop();
        ((TranslateTransform)_panel.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(400, TimeSpan.FromMilliseconds(220)));
        Ui.FadeOut(this, () => Closed?.Invoke());
    }
}
