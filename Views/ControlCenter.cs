using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nova.Core;
using Nova.Services;

namespace Nova.Views;

/// <summary>
/// Alttan açılan hızlı panel (iki sıra kart): çalan medya, ses, parlaklık, ağ, pil; sistem durumu,
/// takvim, hava durumu, kupalar, uyku zamanlayıcısı, telefonla kumanda, bildirimler, ayarlar, güç.
/// </summary>
public sealed class ControlCenter : Grid, IOverlay
{
    sealed class Card
    {
        public Border View = null!;
        public int Row;
        public string ConfirmLabel = "Seç";
        public Action? Confirm;
        public Action<int>? Adjust;
    }

    public sealed class Options
    {
        public int Notifications;
        public string? Weather;
        public Func<string> RemoteStatus = () => "Kapalı";
        public Action? ToggleRemote;
    }

    readonly List<Card> _cards = new();
    readonly StackPanel _row1 = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    readonly StackPanel _row2 = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    readonly Border _panel;
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Options _opt;
    int _sel;
    bool _closing;

    readonly TextBlock _mediaTitle = Ui.Text("Şu an bir şey çalmıyor", 22, weight: FontWeights.SemiBold);
    readonly TextBlock _mediaSub = Ui.Text("Spotify, tarayıcı veya başka bir oynatıcı başlat", 16, Ui.Dim);
    readonly ImageBrush _mediaArt = new() { Stretch = Stretch.UniformToFill };
    readonly TextBlock _playGlyph = Ui.Glyph(Glyphs.Play, 30);
    (TextBlock Value, Border Fill) _volume, _brightness;
    readonly TextBlock _netName = Ui.Text("…", 18, weight: FontWeights.SemiBold);
    readonly TextBlock _netGlyph = Ui.Glyph(Glyphs.Wifi, 34);
    readonly TextBlock _cpu = Ui.Text("CPU —", 16), _ram = Ui.Text("RAM —", 16), _gpu = Ui.Text("GPU —", 16);
    readonly Border _cpuBar = Bar(), _ramBar = Bar(), _gpuBar = Bar();
    readonly TextBlock _sleepText = Ui.Text("", 18, weight: FontWeights.SemiBold);
    readonly TextBlock _remoteText = Ui.Text("", 15, Ui.Dim);
    int? _vol, _bright;
    bool _statsBusy;

    public event Action? Closed;
    public event Action? OpenSettings, OpenPower, OpenNotifications, OpenTrophies;

    public ControlCenter(Options opt)
    {
        _opt = opt;
        Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
        var (box, canvas) = Ui.Stage();
        Children.Add(box);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        var clock = Ui.Text(DateTime.Now.ToString("HH:mm", Fmt.Tr), 46, weight: FontWeights.Light);
        var date = Ui.Text(DateTime.Now.ToString("d MMMM dddd", Fmt.Tr), 20, Ui.Dim);
        date.Margin = new Thickness(20, 12, 0, 0);
        var title = Ui.Text("Kontrol Merkezi", 20, Ui.Faint);
        title.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(title, Dock.Right);
        header.Children.Add(title);
        header.Children.Add(clock);
        header.Children.Add(date);

        var content = new StackPanel { Margin = new Thickness(120, 34, 120, 0) };
        content.Children.Add(header);
        content.Children.Add(_row1);
        content.Children.Add(_row2);
        _panel = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 600,
            CornerRadius = new CornerRadius(34, 34, 0, 0),
            Background = new LinearGradientBrush(Color.FromArgb(0xF5, 0x16, 0x1E, 0x33), Color.FromArgb(0xFA, 0x0A, 0x0E, 0x1A), 90),
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = content,
            RenderTransform = new TranslateTransform(0, 600),
        };
        canvas.Children.Add(_panel);

        BuildRow1();
        BuildRow2();
        _refresh.Tick += async (_, _) => { await RefreshMedia(); RefreshStats(); UpdateDynamic(); };
        Loaded += async (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ((TranslateTransform)_panel.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            Select(0);
            UpdateDynamic();
            RefreshStats();
            _refresh.Start();
            await LoadAsync();
        };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == this) Close(); };
    }

    static Border Bar() => new() { CornerRadius = new CornerRadius(2), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Height = 4 };

    void BuildRow1()
    {
        var art = new Grid { Width = 120, Height = 120 };
        art.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = Ui.CardBg });
        art.Children.Add(Ui.Glyph(Glyphs.Music, 42, Ui.Faint));
        art.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = _mediaArt });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0), Width = 230 };
        info.Children.Add(Ui.Text("Şimdi çalıyor", 15, Ui.Faint));
        _mediaTitle.Margin = new Thickness(0, 6, 0, 4);
        info.Children.Add(_mediaTitle);
        info.Children.Add(_mediaSub);
        var media = new StackPanel { Orientation = Orientation.Horizontal };
        media.Children.Add(art);
        media.Children.Add(info);
        AddCard(1, media, 420, "Oynat / Duraklat", async () => { await NowPlaying.PlayPause(); await Task.Delay(300); await RefreshMedia(); });
        AddCard(1, Ui.Glyph(Glyphs.Previous, 30), 84, "Önceki", async () => { await NowPlaying.Previous(); await Task.Delay(500); await RefreshMedia(); });
        AddCard(1, _playGlyph, 84, "Oynat / Duraklat", async () => { await NowPlaying.PlayPause(); await Task.Delay(300); await RefreshMedia(); });
        AddCard(1, Ui.Glyph(Glyphs.Next, 30), 84, "Sonraki", async () => { await NowPlaying.Next(); await Task.Delay(500); await RefreshMedia(); });

        _volume = AddSlider(Glyphs.Volume, "Ses", SetVolume);
        _brightness = AddSlider(Glyphs.Brightness, "Parlaklık", SetBrightness);

        var net = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        net.Children.Add(_netGlyph);
        _netName.HorizontalAlignment = HorizontalAlignment.Center;
        _netName.Margin = new Thickness(0, 16, 0, 2);
        net.Children.Add(_netName);
        var netSub = Ui.Text("Ağ ayarları", 15, Ui.Dim);
        netSub.HorizontalAlignment = HorizontalAlignment.Center;
        net.Children.Add(netSub);
        AddCard(1, net, 164, "Ağ ayarları", () => OpenUri("ms-settings:network-status"));

        if (Native.Battery() is { } bat)
            AddCard(1, IconCard(bat.Charging ? Glyphs.Charging : Glyphs.Battery, $"%{bat.Percent}", bat.Charging ? "Şarj oluyor" : "Pil"), 130,
                "Güç ayarları", () => OpenUri("ms-settings:batterysaver"));
    }

    void BuildRow2()
    {
        // Sistem durumu
        var sys = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var (t, b) in new[] { (_cpu, _cpuBar), (_ram, _ramBar), (_gpu, _gpuBar) })
        {
            sys.Children.Add(t);
            var track = new Grid { Width = 200, Height = 4, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Ui.Line });
            track.Children.Add(b);
            sys.Children.Add(track);
        }
        AddCard(2, sys, 244, "Görev Yöneticisi", () => { try { Process.Start(new ProcessStartInfo("taskmgr") { UseShellExecute = true }); } catch { } });

        AddCard(2, MiniCalendar(), 230, "Takvim", () => OpenUri("outlookcal:"));

        var (sym, desc) = _opt.Weather is { } w ? (w.Split('|')[0], w.Split('|').ElementAtOrDefault(1) ?? "") : ("—", "Hava durumu");
        var weather = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var big = Ui.Text(sym, 30);
        big.FontFamily = new FontFamily("Segoe UI Symbol");
        big.HorizontalAlignment = HorizontalAlignment.Center;
        weather.Children.Add(big);
        var wd = Ui.Text(desc, 15, Ui.Dim);
        wd.HorizontalAlignment = HorizontalAlignment.Center;
        wd.TextWrapping = TextWrapping.Wrap;
        wd.TextAlignment = TextAlignment.Center;
        wd.Margin = new Thickness(0, 10, 0, 0);
        weather.Children.Add(wd);
        AddCard(2, weather, 150, "Hava durumu", () => OpenUri("msnweather:"));

        AddCard(2, IconCard(Glyphs.Trophy, $"{Trophies.Earned}/{Trophies.All.Length}", "Kupalar"), 120, "Kupalar", () => { Close(); OpenTrophies?.Invoke(); });

        var sleep = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sleep.Children.Add(Ui.Glyph(Glyphs.Sleep, 30));
        _sleepText.HorizontalAlignment = HorizontalAlignment.Center;
        _sleepText.Margin = new Thickness(0, 12, 0, 0);
        sleep.Children.Add(_sleepText);
        var sl = Ui.Text("Uyku zamanlayıcısı", 14, Ui.Dim);
        sl.HorizontalAlignment = HorizontalAlignment.Center;
        sleep.Children.Add(sl);
        AddCard(2, sleep, 150, "Süreyi değiştir", () => { SleepTimer.NextStep(); UpdateDynamic(); });

        var phone = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        phone.Children.Add(Ui.Glyph(Glyphs.Phone, 30));
        var pl = Ui.Text("Telefonla kumanda", 15);
        pl.HorizontalAlignment = HorizontalAlignment.Center;
        pl.Margin = new Thickness(0, 10, 0, 2);
        phone.Children.Add(pl);
        _remoteText.HorizontalAlignment = HorizontalAlignment.Center;
        _remoteText.TextAlignment = TextAlignment.Center;
        _remoteText.TextWrapping = TextWrapping.Wrap;
        phone.Children.Add(_remoteText);
        AddCard(2, phone, 210, "Aç / Kapat", () => { _opt.ToggleRemote?.Invoke(); UpdateDynamic(); });

        AddCard(2, IconCard(Glyphs.Bell, _opt.Notifications > 0 ? $"{_opt.Notifications}" : "0", "Bildirimler"), 110, "Bildirimler",
            () => { Close(); OpenNotifications?.Invoke(); });
        AddCard(2, IconCard(Glyphs.Settings, null, "Ayarlar"), 110, "Ayarlar", () => { Close(); OpenSettings?.Invoke(); });
        AddCard(2, IconCard(Glyphs.Power, null, "Güç"), 100, "Güç", () => { Close(); OpenPower?.Invoke(); });
    }

    static FrameworkElement MiniCalendar()
    {
        var now = DateTime.Now;
        var g = new UniformGrid { Columns = 7, Width = 190 };
        var days = Fmt.Tr.DateTimeFormat.AbbreviatedDayNames;
        for (int i = 0; i < 7; i++)
        {
            var name = days[(i + 1) % 7];
            var d = Ui.Text(name[..Math.Min(2, name.Length)], 11, Ui.Faint);
            d.HorizontalAlignment = HorizontalAlignment.Center;
            g.Children.Add(d);
        }
        var first = new DateTime(now.Year, now.Month, 1);
        int offset = ((int)first.DayOfWeek + 6) % 7;
        for (int i = 0; i < offset; i++) g.Children.Add(new Border());
        for (int day = 1; day <= DateTime.DaysInMonth(now.Year, now.Month); day++)
        {
            var t = Ui.Text(day.ToString(), 12, day == now.Day ? Ui.Ink : Brushes.White, day == now.Day ? FontWeights.Bold : FontWeights.Normal);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            g.Children.Add(day == now.Day
                ? new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), Child = t, Margin = new Thickness(1) }
                : t);
        }
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(Ui.Text(now.ToString("MMMM yyyy", Fmt.Tr), 15, weight: FontWeights.SemiBold));
        g.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(g);
        return p;
    }

    static FrameworkElement IconCard(string glyph, string? value, string label)
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(Ui.Glyph(glyph, 30));
        if (value != null)
        {
            var v = Ui.Text(value, 22, weight: FontWeights.SemiBold);
            v.HorizontalAlignment = HorizontalAlignment.Center;
            v.Margin = new Thickness(0, 10, 0, 0);
            p.Children.Add(v);
        }
        var t = Ui.Text(label, 15, value != null ? Ui.Dim : Brushes.White);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, value != null ? 2 : 16, 0, 0);
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
        AddCard(1, p, 164, "Ayarla", null, adjust);
        return (value, fill);
    }

    void AddCard(int row, FrameworkElement content, double width, string label, Action? confirm, Action<int>? adjust = null)
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
        var card = new Card { View = view, Row = row, ConfirmLabel = label, Confirm = confirm, Adjust = adjust };
        view.MouseLeftButtonUp += (_, _) => { Select(_cards.IndexOf(card)); Sfx.Confirm(); card.Confirm?.Invoke(); };
        view.MouseWheel += (_, e) => card.Adjust?.Invoke(e.Delta > 0 ? 5 : -5);
        _cards.Add(card);
        (row == 1 ? _row1 : _row2).Children.Add(view);
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

    async void RefreshStats()
    {
        if (_statsBusy) return;
        _statsBusy = true;
        try
        {
            var (cpu, mem, gpu) = await Task.Run(() => (SystemStats.Cpu(), SystemStats.Memory(), SystemStats.Gpu()));
            _cpu.Text = $"CPU  %{cpu}";
            _cpuBar.Width = cpu * 2.0;
            _ram.Text = $"RAM  %{mem.Percent}  ·  {mem.UsedGb.ToString("0.0", Fmt.Tr)} / {mem.TotalGb:0} GB";
            _ramBar.Width = mem.Percent * 2.0;
            _gpu.Text = gpu.Percent is { } g ? $"GPU  %{g}" + (gpu.Temp is { } t ? $"  ·  {t}°C" : "") : "GPU  —";
            _gpuBar.Width = (gpu.Percent ?? 0) * 2.0;
        }
        finally { _statsBusy = false; }
    }

    void UpdateDynamic()
    {
        _sleepText.Text = SleepTimer.Describe();
        _remoteText.Text = _opt.RemoteStatus();
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
                if (_sel == 0 || _cards[_sel - 1].Row != card.Row) return false;
                Select(_sel - 1); return true;
            case NavAction.Right:
                if (_sel == _cards.Count - 1 || _cards[_sel + 1].Row != card.Row) return false;
                Select(_sel + 1); return true;
            case NavAction.Up:
            case NavAction.Down:
                if (card.Adjust != null) { card.Adjust(a == NavAction.Up ? 5 : -5); return true; }
                int target = a == NavAction.Up ? 1 : 2;
                if (card.Row == target) return false;
                // Diğer sırada yatayda en yakın karta geç.
                double cx = card.View.TranslatePoint(new Point(card.View.ActualWidth / 2, 0), this).X;
                var best = _cards.Where(c => c.Row == target)
                    .MinBy(c => Math.Abs(c.View.TranslatePoint(new Point(c.View.ActualWidth / 2, 0), this).X - cx));
                if (best == null) return false;
                Select(_cards.IndexOf(best));
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
        ((TranslateTransform)_panel.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(600, TimeSpan.FromMilliseconds(220)));
        Ui.FadeOut(this, () => Closed?.Invoke());
    }
}
