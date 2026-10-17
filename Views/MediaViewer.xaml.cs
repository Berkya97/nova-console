using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nova.Core;
using Nova.Models;
using Nova.Services;

namespace Nova.Views;

/// <summary>Tam ekran fotoğraf görüntüleyici, video oynatıcı ve müzik çalar.</summary>
public partial class MediaViewer : UserControl, IOverlay
{
    static double _volume = 0.8;

    readonly List<string> _list;
    int _index;
    MediaKind _kind;
    bool _playing, _closing;
    int _token;
    readonly DispatcherTimer _progress = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(3) };

    public event Action? Closed;
    public event Action<string>? Failed;

    public MediaViewer(List<string> list, int index)
    {
        InitializeComponent();
        _list = list;
        _index = Math.Clamp(index, 0, list.Count - 1);
        _kind = MediaTypes.KindOf(list[_index]);

        _progress.Tick += (_, _) => UpdateProgress();
        _hide.Tick += (_, _) => { _hide.Stop(); if (_kind != MediaKind.Audio && (_playing || _kind == MediaKind.Image)) FadeChrome(0); };
        Player.MediaOpened += (_, _) => UpdateProgress();
        Player.MediaEnded += (_, _) => OnEnded();
        Player.MediaFailed += (_, _) => Failed?.Invoke(_list[_index]);

        // Açan tıklamanın "bırakma" olayı oynatıcıya düşmesin diye yalnızca burada başlayan tıklamaları say.
        bool pressed = false;
        MouseLeftButtonDown += (_, _) => pressed = true;
        MouseLeftButtonUp += (_, _) => { if (pressed) Handle(NavAction.Confirm); pressed = false; };
        MouseRightButtonUp += (_, _) => Handle(NavAction.Back);
        MouseWheel += (_, e) => Handle(e.Delta > 0 ? NavAction.TabPrev : NavAction.TabNext);
        MouseMove += (_, _) => ShowChrome();

        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            Show(_index);
        };
    }

    public TileItem? Selected => null;

    public IEnumerable<(NavAction, string)> Hints => _kind switch
    {
        MediaKind.Image => new[] { (NavAction.Left, "Önceki / Sonraki"), (NavAction.Back, "Kapat") },
        _ => new[]
        {
            (NavAction.Confirm, _playing ? "Duraklat" : "Oynat"),
            (NavAction.Left, "10 sn"),
            (NavAction.Up, "Ses"),
            (NavAction.TabNext, "Önceki / Sonraki"),
            (NavAction.Back, "Kapat"),
        },
    };

    async void Show(int i)
    {
        _index = i;
        var path = _list[i];
        _kind = MediaTypes.KindOf(path);
        int token = ++_token;

        TitleText.Text = Path.GetFileName(path);
        IndexText.Text = $"{MediaTypes.Label(_kind)}  ·  {i + 1} / {_list.Count}";
        StopPlayer();

        Photo.Visibility = _kind == MediaKind.Image ? Visibility.Visible : Visibility.Collapsed;
        Player.Visibility = _kind == MediaKind.Video ? Visibility.Visible : Visibility.Collapsed;
        AudioView.Visibility = _kind == MediaKind.Audio ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = _kind == MediaKind.Image ? Visibility.Collapsed : Visibility.Visible;
        ShowChrome();

        if (_kind == MediaKind.Image)
        {
            Photo.Opacity = 0;
            var img = await ImageCache.LoadPhotoAsync(path, 3000);
            if (token != _token) return;
            if (img == null) { Failed?.Invoke(path); return; }
            Photo.Source = img;
            Photo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            return;
        }

        Player.Source = new Uri(path);
        Player.Volume = _volume;
        Player.Play();
        _playing = true;
        StateGlyph.Text = Glyphs.Pause;
        _progress.Start();

        if (_kind == MediaKind.Audio)
        {
            AudioTitle.Text = Path.GetFileNameWithoutExtension(path);
            AudioSub.Text = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
            AudioArt.ImageSource = null;
            AudioGlow.Fill = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x8A));
            var (art, isIcon) = await StaWorker.Run(() => ShellThumbnail.Get(path, 512, allowIcon: false));
            if (token != _token || art == null || isIcon) return;
            AudioArt.ImageSource = art;
            if (ImageCache.Tiny(art) is BitmapSource tiny)
                AudioGlow.Fill = new ImageBrush(tiny) { Stretch = Stretch.Fill };
        }
    }

    public bool Handle(NavAction a)
    {
        if (_closing) return false;
        ShowChrome();
        switch (a)
        {
            case NavAction.Back:
            case NavAction.Menu:
                Close();
                return true;
            case NavAction.Left:
                if (_kind == MediaKind.Image) return Step(-1);
                Seek(-10); return true;
            case NavAction.Right:
                if (_kind == MediaKind.Image) return Step(1);
                Seek(10); return true;
            case NavAction.TabPrev:
            case NavAction.PageUp:
                return Step(-1);
            case NavAction.TabNext:
            case NavAction.PageDown:
                return Step(1);
            case NavAction.Up:
                if (_kind == MediaKind.Image) return false;
                SetVolume(_volume + 0.1); return true;
            case NavAction.Down:
                if (_kind == MediaKind.Image) return false;
                SetVolume(_volume - 0.1); return true;
            case NavAction.Confirm:
                if (_kind != MediaKind.Image) TogglePlay();
                return true;
        }
        return false;
    }

    bool Step(int d)
    {
        int n = _index + d;
        if (n < 0 || n >= _list.Count) return false;
        Show(n);
        return true;
    }

    void Seek(double seconds)
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        var dur = Player.NaturalDuration.TimeSpan;
        var pos = Player.Position + TimeSpan.FromSeconds(seconds);
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (pos > dur) pos = dur;
        Player.Position = pos;
        UpdateProgress();
        ShowOsd(seconds > 0 ? "" : "", seconds > 0 ? "+10 sn" : "−10 sn");
    }

    void SetVolume(double v)
    {
        _volume = Math.Clamp(Math.Round(v, 1), 0, 1);
        Player.Volume = _volume;
        ShowOsd(_volume == 0 ? Glyphs.Mute : Glyphs.Volume, $"Ses  %{(int)(_volume * 100)}");
    }

    void TogglePlay()
    {
        if (_playing) Player.Pause(); else Player.Play();
        _playing = !_playing;
        StateGlyph.Text = _playing ? Glyphs.Pause : Glyphs.Play;
        FlashIcon(_playing ? Glyphs.Play : Glyphs.Pause);
    }

    void OnEnded()
    {
        if (_kind == MediaKind.Audio && Step(1)) return;
        _playing = false;
        Player.Pause();
        StateGlyph.Text = Glyphs.Play;
        ShowChrome();
    }

    void UpdateProgress()
    {
        if (!Player.NaturalDuration.HasTimeSpan) { TimeText.Text = ""; return; }
        var dur = Player.NaturalDuration.TimeSpan;
        var pos = Player.Position;
        double frac = dur.TotalSeconds > 0 ? pos.TotalSeconds / dur.TotalSeconds : 0;
        ProgressFill.Width = Math.Clamp(frac, 0, 1) * Track.ActualWidth;
        TimeText.Text = $"{Time(pos)} / {Time(dur)}";
    }

    static string Time(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void StopPlayer()
    {
        _progress.Stop();
        Player.Stop();
        Player.Source = null;
        _playing = false;
        ProgressFill.Width = 0;
        TimeText.Text = "";
    }

    void ShowChrome()
    {
        if (Chrome.Opacity < 1) FadeChrome(1);
        _hide.Stop();
        _hide.Start();
    }

    void FadeChrome(double to) =>
        Chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 150 : 600)));

    void FlashIcon(string glyph)
    {
        FlashGlyph.Text = glyph;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(750))));
        Flash.BeginAnimation(OpacityProperty, a);
    }

    void ShowOsd(string glyph, string text)
    {
        OsdGlyph.Text = glyph;
        OsdText.Text = text;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
        Osd.BeginAnimation(OpacityProperty, a);
    }

    public void Close()
    {
        if (_closing) return;
        _closing = true;
        _hide.Stop();
        StopPlayer();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => Closed?.Invoke();
        BeginAnimation(OpacityProperty, fade);
    }
}
