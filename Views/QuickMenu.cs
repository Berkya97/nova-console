using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nova.Core;
using Nova.Services;

namespace Nova.Views;

/// <summary>
/// Oyun içi hızlı menü: oyunun üstünde alttan açılan panel. Nova'ya dön, ekran görüntüsü,
/// klip kaydı, ses ve oyunu kapatma. Pencereli / kenarlıksız tam ekran oyunlarda görünür.
/// </summary>
public sealed class QuickMenu : Window
{
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);

    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "Nova", "dwm", "csrss", "winlogon", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost" };

    sealed record Card(Border View, string Label, Action Run);

    readonly IntPtr _target;
    readonly int _pid;
    readonly string _title;
    readonly List<Card> _cards = new();
    readonly TextBlock _volText = Ui.Text("", 20, weight: FontWeights.SemiBold);
    int _sel, _volume = -1;
    bool _confirmKill, _closing;

    public event Action? ReturnToNova;
    public event Action<string, string>? Notify;
    /// <summary>Kolu fare ve klavye olarak kullan (başlatıcının giriş penceresi gibi araya girenler için).</summary>
    public event Action? DesktopModeRequested;

    /// <param name="target">Menü açılmadan önce öndeki pencere (oyun).</param>
    public QuickMenu(IntPtr target)
    {
        _target = target;
        GetWindowThreadProcessId(target, out var pid);
        _pid = (int)pid;
        _title = WindowTitle(target) ?? Capture.ForegroundName(target) ?? "";
        var proc = Capture.ForegroundName(target) ?? "";
        bool canKill = target != IntPtr.Zero && !Protected.Contains(proc);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        Foreground = Brushes.White;
        Left = 0; Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        var (box, canvas) = Ui.Stage();
        Content = box;

        // Üst bilgi: oyun adı, oturum süresi, saat, pil
        var info = new StackPanel { Margin = new Thickness(120, 80, 120, 0), VerticalAlignment = VerticalAlignment.Top };
        info.Children.Add(Ui.Text(_title.Length > 0 ? _title : "Oyun", 40, weight: FontWeights.Light));
        var parts = new List<string>();
        if (PlaytimeTracker.Current is { } cur) parts.Add($"Bu oturum: {Minutes(cur.Minutes)}");
        var today = AppSettings.Current.PlayedToday();
        if (today >= 1) parts.Add($"Bugün: {Minutes(today)}");
        if (AppSettings.Current.PlayLimitMinutes > 0) parts.Add($"Günlük sınır: {Minutes(AppSettings.Current.PlayLimitMinutes)}");
        info.Children.Add(Ui.Text(string.Join("  ·  ", parts), 20, Ui.Dim));
        canvas.Children.Add(info);

        var clock = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 86, 120, 0) };
        if (Native.Battery() is { } bat)
        {
            clock.Children.Add(Ui.Glyph(bat.Charging ? Glyphs.Charging : Glyphs.Battery, 22));
            var b = Ui.Text($"%{bat.Percent}", 22);
            b.Margin = new Thickness(10, 0, 30, 2);
            clock.Children.Add(b);
        }
        clock.Children.Add(Ui.Text(DateTime.Now.ToString("HH:mm", Fmt.Tr), 34, weight: FontWeights.Light));
        canvas.Children.Add(clock);

        // Alt panel
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var panel = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 300,
            CornerRadius = new CornerRadius(34, 34, 0, 0),
            Background = new LinearGradientBrush(Color.FromArgb(0xF5, 0x16, 0x1E, 0x33), Color.FromArgb(0xFA, 0x0A, 0x0E, 0x1A), 90),
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 50, 0, 0),
            Child = row,
            RenderTransform = new TranslateTransform(0, 300),
        };
        canvas.Children.Add(panel);

        AddCard(row, Glyphs.Home, "Nova'ya dön", () => { _closing = true; Close(); ReturnToNova?.Invoke(); });
        AddCard(row, Glyphs.Picture, "Ekran görüntüsü", TakeScreenshot);
        AddCard(row, Glyphs.Videos, "Son 30 saniyeyi kaydet", RecordClip);
        AddCard(row, Glyphs.Mouse, "Masaüstü modu", () => { Close(); DesktopModeRequested?.Invoke(); });
        AddCard(row, Glyphs.Mute, "Ses −", () => ChangeVolume(-10));
        var vol = AddCard(row, Glyphs.Volume, "Ses +", () => ChangeVolume(10));
        if (canKill) AddCard(row, Glyphs.Close, "Oyunu kapat", KillGame);
        AddCard(row, Glyphs.Back, "Oyuna devam", Close);
        _volText.HorizontalAlignment = HorizontalAlignment.Center;
        ((StackPanel)vol.View.Child).Children.Add(_volText);

        var hint = Ui.Text("A / Enter: Seç    ◀ ▶: Gezin    B / Esc: Oyuna dön", 17, Ui.Faint);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.VerticalAlignment = VerticalAlignment.Bottom;
        hint.Margin = new Thickness(0, 0, 0, 316);
        canvas.Children.Add(hint);

        Loaded += async (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ((TranslateTransform)panel.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            Native.ForceForeground(new WindowInteropHelper(this).Handle);
            Select(0);
            _volume = await Task.Run(() => SystemVolume.Get() ?? -1);
            _volText.Text = _volume >= 0 ? $"%{_volume}" : "";
        };
        PreviewKeyDown += OnKey;
    }

    static string Minutes(double m) => m >= 60 ? $"{(int)(m / 60)} sa {(int)(m % 60)} dk" : $"{Math.Max(1, (int)m)} dk";

    static string? WindowTitle(IntPtr h)
    {
        int n = GetWindowTextLength(h);
        if (n <= 0) return null;
        var sb = new System.Text.StringBuilder(n + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    Card AddCard(StackPanel row, string glyph, string label, Action run)
    {
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(Ui.Glyph(glyph, 34));
        var t = Ui.Text(label, 17);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextWrapping = TextWrapping.Wrap;
        t.TextAlignment = TextAlignment.Center;
        t.Margin = new Thickness(0, 16, 0, 0);
        content.Children.Add(t);
        var view = new Border
        {
            Width = 170, Height = 170, Margin = new Thickness(0, 0, 14, 0),
            CornerRadius = new CornerRadius(20), Background = Ui.CardBg, BorderThickness = new Thickness(3),
            Padding = new Thickness(14), Child = content, Cursor = Cursors.Hand,
        };
        var card = new Card(view, label, run);
        view.MouseLeftButtonUp += (_, _) => { Select(_cards.IndexOf(card)); card.Run(); };
        _cards.Add(card);
        row.Children.Add(view);
        return card;
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

    void OnKey(object sender, KeyEventArgs e)
    {
        NavAction? a = e.Key switch
        {
            Key.Left => NavAction.Left,
            Key.Right => NavAction.Right,
            Key.Enter or Key.Space => NavAction.Confirm,
            Key.Escape or Key.Back => NavAction.Back,
            _ => null,
        };
        if (a == null) return;
        e.Handled = true;
        Handle(a.Value);
    }

    public bool Handle(NavAction a)
    {
        switch (a)
        {
            case NavAction.Left: if (_sel == 0) return false; Select(_sel - 1); Sfx.Move(); return true;
            case NavAction.Right: if (_sel == _cards.Count - 1) return false; Select(_sel + 1); Sfx.Move(); return true;
            case NavAction.Up: ChangeVolume(10); return true;
            case NavAction.Down: ChangeVolume(-10); return true;
            case NavAction.Confirm: Sfx.Confirm(); _cards[_sel].Run(); return true;
            case NavAction.Back:
            case NavAction.Control:
            case NavAction.Menu:
                Close(); return true;
        }
        return false;
    }

    async void TakeScreenshot()
    {
        Hide();
        Native.ForceForeground(_target);
        await Task.Delay(300);
        var path = Capture.Screenshot(Capture.ForegroundName(_target));
        if (path != null)
        {
            AppSettings.Current.Count("screenshots", 1);
            AppSettings.Current.Save();
        }
        Notify?.Invoke(Glyphs.Picture, path != null ? "Ekran görüntüsü kaydedildi: Resimler › Nova" : "Ekran görüntüsü alınamadı");
        _closing = true;
        Close();
    }

    async void RecordClip()
    {
        Hide();
        Native.ForceForeground(_target);
        await Task.Delay(300);
        Capture.RecordLastSeconds();
        Notify?.Invoke(Glyphs.Videos, "Son 30 saniye Xbox Game Bar ile kaydediliyor (Videolar › Captures)");
        _closing = true;
        Close();
    }

    void ChangeVolume(int delta)
    {
        if (_volume < 0) return;
        _volume = Math.Clamp(_volume + delta, 0, 100);
        _volText.Text = $"%{_volume}";
        var v = _volume;
        Task.Run(() => SystemVolume.Set(v));
    }

    void KillGame()
    {
        var card = _cards[_sel];
        if (!_confirmKill)
        {
            _confirmKill = true;
            ((TextBlock)((StackPanel)card.View.Child).Children[1]).Text = "Emin misin? Tekrar bas";
            return;
        }
        try
        {
            var p = Process.GetProcessById(_pid);
            p.CloseMainWindow();
            if (!p.WaitForExit(3000)) p.Kill(entireProcessTree: true);
            Notify?.Invoke(Glyphs.Close, $"{_title} kapatıldı");
        }
        catch (Exception ex) { Notify?.Invoke(Glyphs.Error, "Oyun kapatılamadı: " + ex.Message); }
        _closing = true;
        Close();
        ReturnToNova?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Menü kapanınca oyuna geri dön (ekran görüntüsü / öldürme sonrası değil).
        if (!_closing && _target != IntPtr.Zero) Native.ForceForeground(_target);
    }
}
