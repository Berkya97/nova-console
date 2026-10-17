using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nova.Core;
using Nova.Models;
using Nova.Services;
using Nova.Views;
using Rect = System.Windows.Shapes.Rectangle;

namespace Nova;

public partial class MainWindow : Window
{
    sealed record Tab(string Name, FrameworkElement View, IScreen Screen);
    sealed record Notice(string Glyph, string Text, DateTime Time);

    readonly List<Tab> _tabs = new();
    readonly List<TextBlock> _tabLabels = new();
    readonly List<IOverlay> _overlays = new();
    readonly GamepadPoller _pad = new();
    readonly HomeRowView _games = new() { CountFormat = "{0} öğe" };
    readonly HomeRowView _media = new();
    readonly TileGrid _apps = new();
    readonly FileBrowser _files = new();
    readonly BackgroundMusic _music = new();
    readonly List<Notice> _notices = new();

    List<TileItem> _allGames = new(), _allApps = new();
    List<string> _clipboard = new();
    bool _clipboardCut;
    SettingsView? _settingsView;

    int _tab = -1;
    InputKind _device = InputKind.Keyboard;
    bool _booting = true;
    string _hintKey = "";
    DispatcherTimer? _bootTimer;
    IntPtr _hwnd;

    // Ortam ışığı / kapak sanatı
    readonly DispatcherTimer _ambientTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    TileItem? _ambientPending, _ambientItem;
    Rect _glowFront = null!, _glowBack = null!;
    Grid _heroFront = null!, _heroBack = null!;
    ImageSource? _heroShown;

    static AppSettings Settings => AppSettings.Current;

    public MainWindow(ProfileStore store, string? forcedProfile, bool startHidden, bool skipBoot)
    {
        _store = store;
        _forcedProfile = forcedProfile;
        _startHidden = startHidden;
        _skipBoot = skipBoot;
        InitializeComponent();
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/nova.ico"));
        _glowFront = GlowA; _glowBack = GlowB;
        _heroFront = HeroA; _heroBack = HeroB;

        Sfx.Enabled = Settings.Sounds;
        Sfx.Init();
        if (DevScript.Active)
        {
            // Tanıtım/test modu: ekran kaydında her zaman en üstte ve sesli (ayar kaydedilmez).
            Topmost = true;
            Sfx.Enabled = true;
            Sfx.StartRecording();
        }

        Loaded += OnLoaded;
        PreviewKeyDown += OnKeyDown;
        PreviewTextInput += OnTextInput;
        Activated += (_, _) => { if (!_overlays.Any(o => o is not OptionsMenu)) Particles.Resume(); UpdateMusic(); };
        Deactivated += (_, _) => { Particles.Pause(); UpdateMusic(); };
        _pad.Action += a =>
        {
            SetDevice(InputKind.Gamepad);
            if (_desktop.Active) _desktop.Nav(a);
            else if (_quick != null) _quick.Handle(a);
            else Dispatch(a);
        };
        _pad.ReturnRequested += OnReturnCombo;
        _pad.DesktopToggleRequested += ToggleDesktopMode;
        _pad.Start();
        _ambientTimer.Tick += (_, _) => ApplyAmbient();
        MenuScrim.MouseLeftButtonDown += (_, _) => { if (_overlays.LastOrDefault() is OptionsMenu m) m.Close(null); };
        SearchBtn.MouseLeftButtonUp += (_, _) => { if (_overlays.Count == 0) OpenSearch(); };
        ControlBtn.MouseLeftButtonUp += (_, _) => { if (_overlays.Count == 0) OpenControlCenter(); };
        PlaytimeTracker.Recorded += OnPlaytimeRecorded;
        PlaytimeTracker.Started += OnGameStarted;
        InitExtras();
    }

    // ───────────────────────────── Başlangıç ─────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        // Ctrl+Alt+N: oyundayken bile Nova'yı öne getirir.
        RegisterHotkeys();
    }

    protected override void OnClosed(EventArgs e)
    {
        CleanupExtras();
        base.OnClosed(e);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            OnHotkey(wParam.ToInt32());
            handled = true;
        }
        else if (msg == Native.WM_DEVICECHANGE && lParam != IntPtr.Zero &&
                 (wParam.ToInt32() == Native.DBT_DEVICEARRIVAL || wParam.ToInt32() == Native.DBT_DEVICEREMOVECOMPLETE) &&
                 Marshal.ReadInt32(lParam, 4) == Native.DBT_DEVTYP_VOLUME)
        {
            int mask = Marshal.ReadInt32(lParam, 12);
            var letters = string.Join(", ", Enumerable.Range(0, 26).Where(i => (mask & (1 << i)) != 0).Select(i => (char)('A' + i) + ":"));
            bool arrived = wParam.ToInt32() == Native.DBT_DEVICEARRIVAL;
            Dispatcher.BeginInvoke(() =>
            {
                Toast(Glyphs.Usb, arrived ? $"USB sürücü takıldı ({letters}) · Dosyalar sekmesinde" : $"USB sürücü çıkarıldı ({letters})");
                if (_files.CurrentPath == null || !arrived) _files.ShowRoot();
            });
        }
        return IntPtr.Zero;
    }

    public void BringBack()
    {
        _quick?.Close();
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowStyle == WindowStyle.None ? WindowState.Maximized : WindowState.Normal;
        Native.ForceForeground(_hwnd);
        Activate();
        Focus();
        Sfx.Confirm();
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildTabs();
        StartClock();
        ApplyProfileVisuals();
        Focus();
        _pendingPicker = _forcedProfile == null && _store.Profiles.Count > 1;
        if (_skipBoot) EndBoot(); else PlayBoot();
        DevScript.RunIfRequested(Root, a => { if (_desktop.Active) _desktop.Nav(a); else Dispatch(a); }, DevCommand, Close);
        StartExtras();
        await LoadDataAsync();
        await LoadExtrasAsync();
    }

    /// <summary>Test / tanıtım betiği komutları ('|' yollarda boşluk yerine geçer).</summary>
    void DevCommand(string token)
    {
        if (token == "trophies") OpenTrophies(null, null);
        else if (token == "picker") ShowProfilePicker(initial: false);
        else if (token == "pad") SetDevice(InputKind.Gamepad);
        else if (token == "kb") SetDevice(InputKind.Keyboard);
        else if (token.StartsWith("type:")) (_overlays.LastOrDefault() as ITextTarget)?.TypeText(token[5..].Replace('_', ' '));
        else if (DesktopDevCommand(token)) { }
        else if (token.StartsWith("goto:"))
        {
            var p = token[5..].Replace('|', ' ') switch
            {
                "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                var other => other,
            };
            int files = _tabs.FindIndex(t => t.Screen is FileBrowser);
            if (_tab != files) { Sfx.Tab(); SwitchTab(files); }
            _files.NavigateFresh(p);
        }
        else if (token.StartsWith("pick:"))
        {
            var name = token[5..].Replace('|', ' ');
            var items = _files.Grid.Items;
            int i = items.ToList().FindIndex(t => t.Title.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { Sfx.Move(); _files.Grid.SelectIndex(i); }
        }
    }


    void BuildTabs()
    {
        _games.Activated += OnActivated;
        _games.OptionsRequested += ShowItemOptions;
        _games.SelectionChanged += OnSelectionChanged;

        _apps.Header = "Uygulamalar";
        _apps.SubHeader = "Masaüstü programları ve Microsoft Store uygulamaları";
        _apps.EmptyMessage = "Uygulamalar yükleniyor…";
        _apps.Activated += OnActivated;
        _apps.OptionsRequested += ShowItemOptions;
        _apps.SelectionChanged += OnSelectionChanged;

        _media.Activated += OnActivated;
        _media.OptionsRequested += ShowItemOptions;
        _media.SelectionChanged += OnSelectionChanged;

        _files.OpenFile += OnActivated;
        _files.Error += msg => { Sfx.Bump(); Toast(Glyphs.Error, msg); };
        _files.FolderOptions += () => ShowFolderOptions();
        _files.FolderOpened += () => { Settings.Count("folders", 1); CheckTrophies(); };

        _tv.Activated += OnActivated;
        _tv.OptionsRequested += ShowItemOptions;
        _tv.SelectionChanged += OnSelectionChanged;
        _files.Grid.OptionsRequested += ShowItemOptions;
        _files.Grid.SelectionChanged += OnSelectionChanged;

        AddTab("Oyunlar", _games, _games);
        AddTab("Uygulamalar", _apps, _apps);
        AddTab("Medya", _media, _media);
        AddTab("TV ve Video", _tv, _tv);
        AddTab("Dosyalar", _files.Grid, _files);
        SwitchTab(Math.Clamp(Settings.DefaultTab, 0, _tabs.Count - 1), animate: false);
    }

    void AddTab(string name, FrameworkElement view, IScreen screen)
    {
        int index = _tabs.Count;
        _tabs.Add(new Tab(name, view, screen));
        view.Visibility = Visibility.Collapsed;
        view.RenderTransform = new TranslateTransform();
        ContentHost.Children.Add(view);

        var label = new TextBlock
        {
            Text = name,
            FontSize = 26,
            Margin = new Thickness(0, 0, 44, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.MouseLeftButtonUp += (_, _) => { if (index != _tab && _overlays.Count == 0) { Sfx.Tab(); SwitchTab(index); } };
        _tabLabels.Add(label);
        TabBar.Children.Add(label);
    }

    async Task LoadDataAsync()
    {
        _files.ShowRoot();
        var mediaTask = Task.Run(() => FileSystemService.RecentMedia(30));

        // Store uygulamalarını da içeren liste STA iş parçacığında okunur; Xbox oyunları da bunu kullanır.
        List<AppEntry> entries;
        try { entries = await StaWorker.Run(AppLibrary.EnumerateAppsFolder); }
        catch { entries = new(); }

        var gamesTask = Task.Run(() => GameLibrary.LoadAll(entries));
        var appsTask = Task.Run(() => AppLibrary.Build(entries));

        try { _allGames = await gamesTask; } catch (Exception ex) { App.Log(ex); _allGames = new(); }
        RebuildHome();
        try { _allApps = await appsTask; } catch (Exception ex) { App.Log(ex); }
        _apps.EmptyMessage = "Uygulama bulunamadı";
        _apps.SetItems(_allApps);
        try { SetMedia(await mediaTask); } catch (Exception ex) { App.Log(ex); SetMedia(new()); }

        await SteamGridDb.EnrichAsync(_allGames, Settings.SteamGridDbKey);
    }

    async Task ReloadLibrary()
    {
        var entries = await StaWorker.Run(AppLibrary.EnumerateAppsFolder);
        _allGames = await Task.Run(() => GameLibrary.LoadAll(entries));
        _allApps = await Task.Run(() => AppLibrary.Build(entries));
        RebuildHome();
        _apps.SetItems(_allApps);
        SetMedia(await Task.Run(() => FileSystemService.RecentMedia(30)));
        Toast(Glyphs.Refresh, $"Kütüphane yenilendi: {_allGames.Count} oyun, {_allApps.Count} uygulama");
        await SteamGridDb.EnrichAsync(_allGames, Settings.SteamGridDbKey);
    }

    /// <summary>Oyunlar satırı: önce sabitlenenler, sonra son oynananlar, en sonda "Oyun Ekle".</summary>
    void RebuildHome(string? selectKey = null)
    {
        selectKey ??= _games.Selected?.Key;
        var pinnedKeys = Settings.Pinned.Select(p => p.Key).ToList();
        foreach (var g in _allGames) g.IsPinned = pinnedKeys.Contains(g.Key);

        var row = new List<TileItem>();
        foreach (var p in Settings.Pinned)
        {
            var game = _allGames.FirstOrDefault(g => g.Key == p.Key);
            var tile = game ?? PinnedTile(p);
            if (tile != null) row.Add(tile);
        }
        row.AddRange(_allGames.Where(g => !g.IsPinned));
        row.Add(GameLibrary.AddTile());
        int idx = selectKey == null ? 0 : row.FindIndex(t => t.Key == selectKey && t.Key.Length > 0);
        _games.SetItems(row, Math.Max(0, idx));
    }

    static TileItem? PinnedTile(PinnedItem p)
    {
        if (!Enum.TryParse<TileKind>(p.Kind, out var kind)) return null;
        switch (kind)
        {
            case TileKind.Folder or TileKind.Drive when p.Path != null && Directory.Exists(p.Path):
                return new TileItem
                {
                    Kind = kind, Title = p.Title, Path = p.Path, Key = p.Key, IsPinned = true,
                    InitGlyph = kind == TileKind.Drive ? Glyphs.Drive : Glyphs.Folder,
                    Accent = Theme.Amber, TileBrush = Theme.Tile(Theme.Amber),
                    Subtitle = "Sabitlenmiş klasör  ·  " + p.Path, ActionLabel = "Göz at", ActionGlyph = Glyphs.OpenFolder,
                };
            case TileKind.File when p.Path != null && File.Exists(p.Path):
                var f = FileSystemService.FileTile(new FileInfo(p.Path));
                return new TileItem
                {
                    Kind = kind, Title = p.Title, Path = p.Path, Key = p.Key, IsPinned = true, TileBrush = Theme.Translucent,
                    Subtitle = "Sabitlenmiş dosya  ·  " + p.Path, ActionLabel = f.ActionLabel, ActionGlyph = f.ActionGlyph,
                };
            case TileKind.App:
                return new TileItem
                {
                    Kind = kind, Title = p.Title, Path = p.Path, LaunchUri = p.LaunchUri, Key = p.Key, IsPinned = true,
                    IconPath = p.LaunchUri?.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) == true ? p.LaunchUri : null,
                    TileBrush = Theme.Translucent, Subtitle = "Sabitlenmiş uygulama", ActionLabel = "Başlat", ActionGlyph = Glyphs.Play,
                };
        }
        return null;
    }

    void SetMedia(List<TileItem> recent)
    {
        var items = FileSystemService.QuickFolders()
            .Where(q => q.Title is "Resimler" or "Videolar" or "Müzik" or "İndirilenler" && Directory.Exists(q.Path))
            .Select(q => FileSystemService.FolderTile(q.Title, q.Path, q.Glyph, q.Color))
            .ToList();
        if (Directory.Exists(Capture.ScreenshotDir))
            items.Add(FileSystemService.FolderTile("Ekran görüntüleri", Capture.ScreenshotDir, Glyphs.Picture, Theme.Cyan));
        if (Directory.Exists(Capture.ClipsDir))
            items.Add(FileSystemService.FolderTile("Klipler", Capture.ClipsDir, Glyphs.Videos, Theme.Violet));
        items.AddRange(recent);
        _media.SetItems(items);
    }

    void StartClock()
    {
        void Update() => ClockText.Text = DateTime.Now.ToString("HH:mm", Fmt.Tr);
        Update();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => Update();
        t.Start();
    }

    void PlayBoot()
    {
        Sfx.Boot();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BootLogo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)));
        BootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(1400)) { EasingFunction = ease });
        BootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(1400)) { EasingFunction = ease });
        Orbit.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever });
        Boot.MouseLeftButtonDown += (_, _) => EndBoot();

        _bootTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2300) };
        _bootTimer.Tick += (_, _) => EndBoot();
        _bootTimer.Start();
    }

    void EndBoot()
    {
        if (!_booting) return;
        _booting = false;
        _bootTimer?.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(550));
        fade.Completed += (_, _) =>
        {
            Boot.Visibility = Visibility.Collapsed;
            Orbit.BeginAnimation(RotateTransform.AngleProperty, null);
        };
        Boot.BeginAnimation(OpacityProperty, fade);

        if (_pendingPicker)
        {
            // Birden fazla kullanıcı varsa önce "Kim oynuyor?" ekranı.
            _pendingPicker = false;
            ShowProfilePicker(initial: true);
            return;
        }
        RevealStage();
    }

    void RevealStage()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromMilliseconds(150) });
        StageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(150), EasingFunction = ease });
        HintBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromMilliseconds(600) });
        Dispatcher.BeginInvoke(() => MoveIndicator(false), DispatcherPriority.Loaded);
        UpdateMusic();
        UpdateHints();
    }

    // ───────────────────────────── Görünüm ayarları ─────────────────────────────

    void ApplyTheme()
    {
        BaseBg.Fill = Themes.Background(Themes.ByName(Settings.Theme));
        CheckTrophies();
    }

    async void ApplyWallpaper()
    {
        var path = Settings.WallpaperPath;
        if (path == null || !File.Exists(path))
        {
            WallpaperLayer.Visibility = Visibility.Collapsed;
            return;
        }
        var img = await ImageCache.LoadAsync(path, 2560, cache: false);
        WallpaperImg.Source = img;
        WallpaperLayer.Visibility = img == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void UpdateMusic() => _music.SetActive(IsActive && !_booting && !_overlays.Any(o => o is MediaViewer));

    // ───────────────────────────── Girdi ─────────────────────────────

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.F4 && mods.HasFlag(ModifierKeys.Alt)) return;
        if (key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }

        // Yazı kabul eden katmanlarda harfler yazıya gider; yalnızca gezinme tuşları eyleme çevrilir.
        if (_overlays.LastOrDefault() is ITextTarget text)
        {
            NavAction? t = key switch
            {
                Key.Left => NavAction.Left,
                Key.Right => NavAction.Right,
                Key.Up => NavAction.Up,
                Key.Down => NavAction.Down,
                Key.Escape => NavAction.Back,
                Key.PageUp => NavAction.PageUp,
                Key.PageDown => NavAction.PageDown,
                _ => null,
            };
            SetDevice(InputKind.Keyboard);
            if (key == Key.Enter) { text.Submit(); e.Handled = true; }
            else if (key == Key.Back) { text.Backspace(); e.Handled = true; }
            else if (t != null) { e.Handled = true; Dispatch(t.Value); }
            return;
        }

        NavAction? a = key switch
        {
            Key.Left => NavAction.Left,
            Key.Right => NavAction.Right,
            Key.Up => NavAction.Up,
            Key.Down => NavAction.Down,
            Key.Enter or Key.Space => NavAction.Confirm,
            Key.Escape or Key.Back => NavAction.Back,
            Key.O or Key.Apps => NavAction.Options,
            Key.Q => NavAction.TabPrev,
            Key.E => NavAction.TabNext,
            Key.Tab => mods.HasFlag(ModifierKeys.Shift) ? NavAction.TabPrev : NavAction.TabNext,
            Key.PageUp => NavAction.PageUp,
            Key.PageDown => NavAction.PageDown,
            Key.P => NavAction.Menu,
            Key.S => NavAction.Search,
            Key.F when mods.HasFlag(ModifierKeys.Control) => NavAction.Search,
            Key.C => NavAction.Control,
            _ => null,
        };

        if (a == null && key is >= Key.D1 and <= Key.D5 && _overlays.Count == 0 && !_booting)
        {
            int idx = key - Key.D1;
            if (idx != _tab) { Sfx.Tab(); SwitchTab(idx); }
            e.Handled = true;
            return;
        }
        if (a == null) return;
        e.Handled = true;
        SetDevice(InputKind.Keyboard);
        Dispatch(a.Value);
    }

    void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_overlays.LastOrDefault() is not ITextTarget text || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        text.TypeText(e.Text);
        e.Handled = true;
    }

    void SetDevice(InputKind d)
    {
        if (_device == d) return;
        _device = d;
        UpdateHints();
    }

    void Dispatch(NavAction a)
    {
        if (_booting) { EndBoot(); return; }

        if (_overlays.Count > 0)
        {
            if (_overlays[^1].Handle(a)) PlayFor(a);
            UpdateHints();
            return;
        }

        switch (a)
        {
            case NavAction.TabPrev:
            case NavAction.TabNext:
                Sfx.Tab();
                SwitchTab(_tab + (a == NavAction.TabNext ? 1 : -1));
                return;
            case NavAction.Menu: Sfx.Confirm(); ShowPowerMenu(); return;
            case NavAction.Search: Sfx.Confirm(); OpenSearch(); return;
            case NavAction.Control: Sfx.Confirm(); OpenControlCenter(); return;
        }

        if (_tabs[_tab].Screen.Handle(a)) PlayFor(a);
        else if (a == NavAction.Back) { Sfx.Confirm(); ShowPowerMenu(); }
        else if (a is NavAction.Left or NavAction.Right or NavAction.Up or NavAction.Down) Sfx.Bump();
        UpdateHints();
    }

    static void PlayFor(NavAction a)
    {
        switch (a)
        {
            case NavAction.Confirm: Sfx.Confirm(); break;
            case NavAction.Back: Sfx.Back(); break;
            case NavAction.Options: Sfx.Confirm(); break;
            default: Sfx.Move(); break;
        }
    }

    void ToggleFullscreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
    }

    // ───────────────────────────── Sekmeler ─────────────────────────────

    void SwitchTab(int i, bool animate = true)
    {
        i = (i + _tabs.Count) % _tabs.Count;
        if (i == _tab) return;
        var old = _tab >= 0 ? _tabs[_tab] : null;
        int dir = old == null ? 0 : (i > _tab ? 1 : -1);
        _tab = i;
        var t = _tabs[i];

        if (old != null) old.View.Visibility = Visibility.Collapsed;
        t.View.Visibility = Visibility.Visible;
        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            t.View.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
            ((TranslateTransform)t.View.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(60 * dir, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        }

        for (int k = 0; k < _tabLabels.Count; k++)
        {
            _tabLabels[k].Foreground = k == i ? Brushes.White : (Brush)FindResource("TextFaint");
            _tabLabels[k].FontWeight = k == i ? FontWeights.SemiBold : FontWeights.Normal;
        }
        Dispatcher.BeginInvoke(() => MoveIndicator(animate), DispatcherPriority.Loaded);

        OnSelectionChanged(t.Screen.Selected);
        UpdateHints();
    }

    void MoveIndicator(bool animate)
    {
        if (_tab < 0) return;
        var label = _tabLabels[_tab];
        if (label.ActualWidth <= 0) return;
        var p = label.TranslatePoint(new Point(0, 0), IndicatorCanvas);
        double w = label.ActualWidth * 0.5, left = p.X + label.ActualWidth * 0.25;
        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            TabIndicator.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(left, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            TabIndicator.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        }
        else
        {
            TabIndicator.BeginAnimation(Canvas.LeftProperty, null);
            TabIndicator.BeginAnimation(WidthProperty, null);
            Canvas.SetLeft(TabIndicator, left);
            TabIndicator.Width = w;
        }
    }

    // ───────────────────────────── Eylemler ─────────────────────────────

    void OnActivated(TileItem item)
    {
        switch (item.Kind)
        {
            case TileKind.Game:
            case TileKind.App:
                Launch(item);
                break;
            case TileKind.Action when item.Tag as string == "add-game":
                AddGame();
                break;
            case TileKind.Action when item.Tag as string == "add-tv":
                AddTvSite();
                break;
            case TileKind.Folder:
            case TileKind.Drive:
                if (_tabs[_tab].Screen is FileBrowser) _files.Activate(item);
                else if (item.Path != null)
                {
                    SwitchTab(_tabs.FindIndex(t => t.Screen is FileBrowser));
                    _files.NavigateFresh(item.Path);
                }
                break;
            case TileKind.File when item.Path != null:
                if (MediaTypes.KindOf(item.Path) != MediaKind.None) OpenViewer(item.Path);
                else if (MediaTypes.IsText(item.Path) || MediaTypes.IsPdf(item.Path)) OpenDocument(item.Path);
                else ShellOpen(item.Path, item.Title);
                break;
        }
    }

    void Launch(TileItem item)
    {
        var target = item.LaunchUri ?? item.Path;
        if (target == null) return;
        // Kol artık başlatılan uygulamanın; masaüstü modu açık kalırsa oyunda imleç oynar, LB/RB pencere değiştirir.
        _desktop.Disable();
        try
        {
            var psi = item.LaunchArgs != null && item.Path != null
                ? new ProcessStartInfo(item.Path, item.LaunchArgs) { UseShellExecute = true }
                : new ProcessStartInfo(target) { UseShellExecute = true };
            if (item.LaunchUri == null && item.LaunchArgs == null && File.Exists(target))
                psi.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
            Process.Start(psi);
            Settings.MarkLaunched(item.Key);
            PlaytimeTracker.Track(item);
            OnLaunched(item);
            Toast(Glyphs.Play, $"{item.Title} başlatılıyor…");
        }
        catch (Exception ex)
        {
            Sfx.Bump();
            Toast(Glyphs.Error, $"Başlatılamadı: {ex.Message}");
        }
    }

    void OnPlaytimeRecorded(string key, double total)
    {
        var game = _allGames.FirstOrDefault(g => g.Key == key);
        if (game == null) return;
        game.PlaytimeMinutes = total;
        game.LastPlayed = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        GameLibrary.UpdateSubtitle(game);
        Toast(Glyphs.Timer, $"{game.Title}  ·  toplam {Fmt.Playtime(total)}");
        _allGames = _allGames.OrderByDescending(g => g.LastPlayed).ToList();
        RebuildHome();
    }

    void ShellOpen(string path, string title)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Toast(Glyphs.Open, $"{title} açılıyor…");
        }
        catch (Exception ex)
        {
            Sfx.Bump();
            Toast(Glyphs.Error, $"Açılamadı: {ex.Message}");
        }
    }

    static void Reveal(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
            else Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { }
    }

    async void AddGame()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Kütüphaneye eklenecek oyunu veya programı seç",
            Filter = "Programlar (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|Tüm dosyalar (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        if (!Settings.CustomGames.Any(g => string.Equals(g.Path, dlg.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            Settings.CustomGames.Add(new CustomGame { Title = Path.GetFileNameWithoutExtension(dlg.FileName), Path = dlg.FileName });
            Settings.Save();
        }
        await ReloadGames("custom:" + dlg.FileName.ToLowerInvariant());
        Toast(Glyphs.Add, "Kütüphaneye eklendi");
    }

    async Task ReloadGames(string? selectKey = null)
    {
        var entries = await StaWorker.Run(AppLibrary.EnumerateAppsFolder);
        _allGames = await Task.Run(() => GameLibrary.LoadAll(entries));
        RebuildHome(selectKey);
        await SteamGridDb.EnrichAsync(_allGames, Settings.SteamGridDbKey);
    }

    async void RemoveCustom(TileItem item)
    {
        Settings.CustomGames.RemoveAll(g => string.Equals(g.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        await ReloadGames();
        Toast(Glyphs.Delete, $"{item.Title} kütüphaneden kaldırıldı");
    }

    void OpenViewer(string path)
    {
        var list = MediaTypes.Siblings(path);
        int index = list.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        var viewer = new MediaViewer(list, Math.Max(0, index));
        viewer.Failed += p =>
        {
            viewer.Close();
            Toast(Glyphs.Info, "Nova bu dosyayı oynatamadı, varsayılan uygulamayla açılıyor");
            ShellOpen(p, Path.GetFileName(p));
        };
        PushOverlay(viewer, viewer, fullBleed: true);
    }

    void OpenDocument(string path)
    {
        var doc = new DocumentPreview(path);
        doc.Failed += p =>
        {
            doc.Close();
            ShellOpen(p, Path.GetFileName(p));
        };
        PushOverlay(doc, doc, fullBleed: true);
    }

    void OpenSearch()
    {
        Settings.Count("searches", 1);
        CheckTrophies();
        var search = new SearchOverlay(() => _allGames.Concat(_allApps).Concat(_tv.Items.Where(t => t.Kind == TileKind.App)), _device == InputKind.Gamepad);
        search.Activated += OnActivated;
        PushOverlay(search, search, fullBleed: true);
    }

    void OpenControlCenter()
    {
        var cc = new ControlCenter(new ControlCenter.Options
        {
            Notifications = _notices.Count,
            Weather = _weather,
            RemoteStatus = RemoteStatus,
            ToggleRemote = () => SetRemote(!Settings.RemoteEnabled),
        });
        cc.OpenTrophies += () => OpenTrophies(null, null);
        cc.OpenSettings += OpenSettings;
        cc.OpenPower += ShowPowerMenu;
        cc.OpenNotifications += ShowNotifications;
        PushOverlay(cc, cc, fullBleed: true);
    }

    // ───────────────────────────── Katman yönetimi ─────────────────────────────

    /// <summary>
    /// Tam ekran katmanlar (görüntüleyici, arama, ayarlar...) MediaHost'a, seçenek menüleri
    /// ölçekli MenuStage'e eklenir; menüler her zaman tam ekran katmanların üstünde kalır.
    /// </summary>
    void PushOverlay(FrameworkElement view, IOverlay nav, bool fullBleed)
    {
        nav.Closed += () =>
        {
            (fullBleed ? MediaHost : MenuStage).Children.Remove(view);
            _overlays.Remove(nav);
            OverlaysChanged();
        };
        if (fullBleed) MediaHost.Children.Add(view);
        else MenuStage.Children.Add(view);
        _overlays.Add(nav);
        OverlaysChanged();
    }

    void OverlaysChanged()
    {
        bool menus = _overlays.OfType<OptionsMenu>().Any();
        if (menus)
        {
            MenuHost.Visibility = Visibility.Visible;
            MenuScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        }
        else if (MenuHost.Visibility == Visibility.Visible)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) => { if (!_overlays.OfType<OptionsMenu>().Any()) MenuHost.Visibility = Visibility.Collapsed; };
            MenuScrim.BeginAnimation(OpacityProperty, fade);
        }

        if (_overlays.Any(o => o is not OptionsMenu)) Particles.Pause();
        else if (IsActive) Particles.Resume();
        if (!_overlays.OfType<SettingsView>().Any()) _settingsView = null;
        UpdateMusic();
        UpdateHints();
    }

    void ShowMenu(OptionsMenu menu) => PushOverlay(menu, menu, fullBleed: false);

    void AskText(string title, string initial, Action<string> submit, string? help = null, bool secret = false)
    {
        var input = new TextInputOverlay(title, initial, submit, help, secret);
        PushOverlay(input, input, fullBleed: true);
    }

    // ───────────────────────────── Menüler ─────────────────────────────

    static string PinKey(TileItem t) => t.Key.Length > 0 ? t.Key : "path:" + t.Path?.ToLowerInvariant();

    void ShowItemOptions(TileItem item)
    {
        var e = new List<OptionsMenu.Entry>();
        bool inFiles = _tabs[_tab].Screen is FileBrowser && _files.CurrentPath != null;
        var pinEntry = new OptionsMenu.Entry(
            Settings.IsPinned(PinKey(item)) ? Glyphs.Unpin : Glyphs.Pin,
            Settings.IsPinned(PinKey(item)) ? "Ana ekrandan kaldır" : "Ana ekrana sabitle",
            () => TogglePin(item));

        switch (item.Kind)
        {
            case TileKind.Game:
            case TileKind.App:
                e.Add(new(item.ActionGlyph, item.ActionLabel, () => Launch(item)));
                e.Add(pinEntry);
                var dir = item.Path ?? item.InstallDir;
                if (dir != null) e.Add(new(Glyphs.OpenFolder, "Dosya konumunu aç", () => Reveal(dir)));
                if (item.IsCustom) e.Add(new(Glyphs.Delete, "Kütüphaneden kaldır", () => RemoveCustom(item)));
                break;
            case TileKind.File when item.Path != null:
                e.Add(new(item.ActionGlyph, item.ActionLabel, () => OnActivated(item)));
                e.Add(new(Glyphs.Open, "Varsayılan uygulamayla aç", () => ShellOpen(item.Path, item.Title)));
                e.Add(pinEntry);
                if (inFiles) AddFileOps(e, item);
                e.Add(new(Glyphs.OpenFolder, "Dosya Gezgini'nde göster", () => Reveal(item.Path)));
                e.Add(new(Glyphs.Copy, "Yolu kopyala", () => CopyPath(item.Path)));
                break;
            case TileKind.Folder when item.Path != null:
            case TileKind.Drive when item.Path != null:
                e.Add(new(Glyphs.OpenFolder, "Aç", () => OnActivated(item)));
                e.Add(pinEntry);
                if (inFiles && item.Kind == TileKind.Folder) AddFileOps(e, item);
                e.Add(new(Glyphs.Folder, "Dosya Gezgini'nde aç", () => Reveal(item.Path)));
                e.Add(new(Glyphs.Copy, "Yolu kopyala", () => CopyPath(item.Path)));
                break;
            default:
                return;
        }
        AddExtraOptions(e, item);
        if (inFiles) AddFolderOps(e);
        ShowMenu(new OptionsMenu(item.Title, e));
    }

    void ShowFolderOptions()
    {
        var e = new List<OptionsMenu.Entry>();
        AddFolderOps(e);
        ShowMenu(new OptionsMenu(FileSystemService.DisplayName(_files.CurrentPath ?? ""), e));
    }

    void TogglePin(TileItem item)
    {
        var key = PinKey(item);
        if (Settings.IsPinned(key))
        {
            Settings.Pinned.RemoveAll(p => p.Key == key);
            Toast(Glyphs.Unpin, $"{item.Title} ana ekrandan kaldırıldı");
        }
        else
        {
            Settings.Pinned.Insert(0, new PinnedItem
            {
                Key = key,
                Title = item.Title,
                Kind = item.Kind.ToString(),
                Path = item.Path,
                LaunchUri = item.LaunchUri,
            });
            Toast(Glyphs.Pin, $"{item.Title} ana ekrana sabitlendi");
        }
        Settings.Save();
        RebuildHome();
        CheckTrophies();
    }

    void CopyPath(string path)
    {
        try { Clipboard.SetText(path); Toast(Glyphs.Copy, "Yol panoya kopyalandı"); } catch { }
    }

    // ───────────────────────────── Dosya işlemleri ─────────────────────────────

    void AddFileOps(List<OptionsMenu.Entry> e, TileItem item)
    {
        var path = item.Path!;
        e.Add(new(Glyphs.Copy, "Kopyala", () => { _clipboard = new() { path }; _clipboardCut = false; Toast(Glyphs.Copy, $"{item.Title} kopyalandı. Hedef klasörde Seçenekler › Yapıştır"); }));
        e.Add(new(Glyphs.Cut, "Kes", () => { _clipboard = new() { path }; _clipboardCut = true; Toast(Glyphs.Cut, $"{item.Title} kesildi. Hedef klasörde Seçenekler › Yapıştır"); }));
        e.Add(new(Glyphs.Rename, "Yeniden adlandır", () => Rename(item)));
        e.Add(new(Glyphs.Delete, "Geri dönüşüm kutusuna taşı", () => ConfirmDelete(item)));
    }

    void AddFolderOps(List<OptionsMenu.Entry> e)
    {
        var dir = _files.CurrentPath;
        if (dir == null) return;
        if (_clipboard.Count > 0)
            e.Add(new(Glyphs.Paste, _clipboardCut ? "Buraya taşı" : "Buraya yapıştır", () => Paste(dir)));
        e.Add(new(Glyphs.NewFolder, "Yeni klasör", () => AskText("Yeni klasör", "Yeni klasör", name =>
        {
            try
            {
                var p = Path.Combine(dir, name);
                Directory.CreateDirectory(p);
                _files.Refresh(p);
            }
            catch (Exception ex) { Toast(Glyphs.Error, ex.Message); }
        })));
        int si = Array.IndexOf(FileSystemService.SortModes, Settings.FileSort);
        var nextSort = FileSystemService.SortModes[(si + 1) % FileSystemService.SortModes.Length];
        e.Add(new(Glyphs.Sort, $"Sırala: {Settings.FileSort} → {nextSort}", () =>
        {
            Settings.FileSort = nextSort;
            Settings.Save();
            _files.Refresh();
        }));
        e.Add(new(Glyphs.View, Settings.ShowHidden ? "Gizli dosyaları gizle" : "Gizli dosyaları göster", () =>
        {
            Settings.ShowHidden = !Settings.ShowHidden;
            Settings.Save();
            _files.Refresh();
        }));
    }

    void Rename(TileItem item)
    {
        var path = item.Path!;
        AskText("Yeniden adlandır", item.Title, name =>
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Toast(Glyphs.Error, "Ad geçersiz karakter içeriyor"); return; }
            var target = Path.Combine(Path.GetDirectoryName(path)!, name);
            try
            {
                if (Directory.Exists(path)) Directory.Move(path, target); else File.Move(path, target);
                _files.Refresh(target);
                Toast(Glyphs.Rename, $"Yeni ad: {name}");
            }
            catch (Exception ex) { Toast(Glyphs.Error, ex.Message); }
        });
    }

    void ConfirmDelete(TileItem item)
    {
        ShowMenu(new OptionsMenu($"\"{item.Title}\" geri dönüşüm kutusuna taşınsın mı?", new List<OptionsMenu.Entry>
        {
            new(Glyphs.Delete, "Evet, taşı", () => RunFileOp(Native.FileOp.Delete, new[] { item.Path! }, null, $"{item.Title} geri dönüşüm kutusuna taşındı")),
            new(Glyphs.Close, "Vazgeç", () => { }),
        }));
    }

    void Paste(string dir)
    {
        var op = _clipboardCut ? Native.FileOp.Move : Native.FileOp.Copy;
        var items = _clipboard.ToList();
        if (_clipboardCut) _clipboard.Clear();
        RunFileOp(op, items, dir, op == Native.FileOp.Move ? "Taşıma tamamlandı" : "Kopyalama tamamlandı");
    }

    /// <summary>Dosya işlemini ayrı bir STA iş parçacığında çalıştırır (Windows ilerleme penceresi görünebilir).</summary>
    void RunFileOp(Native.FileOp op, IEnumerable<string> sources, string? target, string doneMessage)
    {
        var list = sources.ToList();
        var hwnd = _hwnd;
        var thread = new Thread(() =>
        {
            bool ok = Native.ShellFileOperation(hwnd, op, list, target);
            Dispatcher.BeginInvoke(() =>
            {
                _files.Refresh();
                Toast(ok ? Glyphs.Accept : Glyphs.Error, ok ? doneMessage : "İşlem iptal edildi ya da tamamlanamadı");
            });
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // ───────────────────────────── Güç, bildirimler, ayarlar ─────────────────────────────

    void ShowPowerMenu()
    {
        var e = new List<OptionsMenu.Entry>
        {
            new(Glyphs.Minimize, "Masaüstüne dön", () => WindowState = WindowState.Minimized),
            new(Glyphs.Settings, "Ayarlar", OpenSettings),
            new(Glyphs.Sleep, "Uyku modu", () => SetSuspendState(false, false, false)),
            new(Glyphs.Restart, "Yeniden başlat", () => RunShutdown("/r /t 0")),
            new(Glyphs.Power, "Bilgisayarı kapat", () => RunShutdown("/s /t 0")),
        };
        if (_store.Profiles.Count > 1) e.Insert(1, new(Glyphs.Person, "Kullanıcı değiştir", () => ShowProfilePicker(initial: false)));
        if (Settings.BackgroundMode)
        {
            e.Add(new(Glyphs.Minimize, "Nova'yı arka plana al", HideToTray));
            e.Add(new(Glyphs.Close, "Nova'yı kapat", ReallyExit));
        }
        else e.Add(new(Glyphs.Close, "Nova'dan çık", ReallyExit));
        ShowMenu(new OptionsMenu("Güç", e));
    }

    void ShowNotifications()
    {
        var e = _notices.AsEnumerable().Reverse().Take(8)
            .Select(n => new OptionsMenu.Entry(n.Glyph, $"{n.Time:HH:mm}   {n.Text}", () => { }))
            .ToList();
        if (e.Count == 0) e.Add(new(Glyphs.Bell, "Yeni bildirim yok", () => { }));
        else e.Add(new(Glyphs.Delete, "Bildirimleri temizle", () => _notices.Clear()));
        ShowMenu(new OptionsMenu("Bildirimler", e));
    }

    static void RunShutdown(string args)
    {
        try { Process.Start(new ProcessStartInfo("shutdown", args) { CreateNoWindow = true, UseShellExecute = false }); } catch { }
    }

    [DllImport("powrprof.dll")]
    static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

    void OpenSettings()
    {
        static string OnOff(bool b) => b ? "Açık" : "Kapalı";
        var tabNames = _tabs.Select(t => t.Name).ToArray();

        var rows = new List<SettingRow>
        {
            new()
            {
                Section = "Görünüm", Glyph = Glyphs.Palette, Label = "Tema",
                Help = "Arka planın renk tonu. Sol/sağ ile değiştir.",
                Value = () => Settings.Theme,
                Change = d =>
                {
                    int i = Array.FindIndex(Themes.All, t => t.Name == Settings.Theme);
                    Settings.Theme = Themes.All[((i + d) % Themes.All.Length + Themes.All.Length) % Themes.All.Length].Name;
                    Settings.Save();
                    ApplyTheme();
                },
            },
            new()
            {
                Glyph = Glyphs.Picture, Label = "Duvar kağıdı",
                Help = "Arka planda kendi resmini kullan. Okunabilirlik için hafifçe karartılır.",
                Value = () => Settings.WallpaperPath is { } w ? Path.GetFileName(w) : "Yok",
                Activate = () => ShowMenu(new OptionsMenu("Duvar kağıdı", new List<OptionsMenu.Entry>
                {
                    new(Glyphs.Picture, "Resim seç…", PickWallpaper),
                    new(Glyphs.Delete, "Duvar kağıdını kaldır", () => { Settings.WallpaperPath = null; Settings.Save(); ApplyWallpaper(); _settingsView?.Refresh(); }),
                })),
            },
            new()
            {
                Glyph = Glyphs.Volume, Label = "Arayüz sesleri",
                Help = "Gezinme, seçme ve açılış sesleri.",
                Value = () => OnOff(Settings.Sounds),
                Change = _ => { Settings.Sounds = Sfx.Enabled = !Settings.Sounds; Settings.Save(); },
            },
            new()
            {
                Section = "Müzik", Glyph = Glyphs.Music, Label = "Arka plan müziği",
                Help = "Nova öndeyken çalan müzik. Tek bir dosya ya da bir klasör (karışık çalar) seçebilirsin. Oyuna geçince veya video izlerken durur.",
                Value = () => Settings.MusicPath is { } m ? Path.GetFileName(m.TrimEnd('\\')) : "Kapalı",
                Activate = () => ShowMenu(new OptionsMenu("Arka plan müziği", new List<OptionsMenu.Entry>
                {
                    new(Glyphs.Music, "Dosya seç…", () => PickMusic(folder: false)),
                    new(Glyphs.Folder, "Klasör seç…", () => PickMusic(folder: true)),
                    new(Glyphs.Mute, "Müziği kapat", () => { Settings.MusicPath = null; Settings.Save(); _music.Configure(null, Settings.MusicVolume); _settingsView?.Refresh(); }),
                })),
            },
            new()
            {
                Glyph = Glyphs.Volume, Label = "Müzik ses seviyesi",
                Help = "Arka plan müziğinin seviyesi.",
                Value = () => $"%{Settings.MusicVolume}",
                Change = d =>
                {
                    Settings.MusicVolume = Math.Clamp(Settings.MusicVolume + d * 10, 0, 100);
                    Settings.Save();
                    _music.Configure(Settings.MusicPath, Settings.MusicVolume);
                },
            },
            new()
            {
                Section = "Sistem", Glyph = Glyphs.Power, Label = "Windows açılışında başlat",
                Help = "Bilgisayar açıldığında Nova otomatik olarak başlar.",
                Value = () => OnOff(StartupRegistration.IsEnabled()),
                Change = _ => StartupRegistration.Set(!StartupRegistration.IsEnabled()),
            },
            new()
            {
                Glyph = Glyphs.Home, Label = "Açılış sekmesi",
                Help = "Nova açıldığında ilk gösterilecek sekme.",
                Value = () => tabNames[Math.Clamp(Settings.DefaultTab, 0, tabNames.Length - 1)],
                Change = d => { Settings.DefaultTab = ((Settings.DefaultTab + d) % tabNames.Length + tabNames.Length) % tabNames.Length; Settings.Save(); },
            },
            new()
            {
                Glyph = Glyphs.View, Label = "Gizli dosyaları göster",
                Help = "Dosyalar sekmesinde gizli dosya ve klasörleri de listeler.",
                Value = () => OnOff(Settings.ShowHidden),
                Change = _ => { Settings.ShowHidden = !Settings.ShowHidden; Settings.Save(); _files.Refresh(); },
            },
            new()
            {
                Section = "Kütüphane", Glyph = Glyphs.Key, Label = "SteamGridDB API anahtarı",
                Help = "Steam dışındaki oyunlar (Epic, GOG, Xbox, elle eklenenler) için kapak, arka plan ve logo indirilir. " +
                       "Ücretsiz anahtar: steamgriddb.com › Hesap › Tercihler › API.",
                Value = () => Settings.SteamGridDbKey is { Length: > 4 } k ? "••••" + k[^4..] : "Ayarlanmadı",
                Activate = () => AskText("SteamGridDB API anahtarı", Settings.SteamGridDbKey ?? "", async key =>
                {
                    Settings.SteamGridDbKey = key;
                    Settings.Save();
                    _settingsView?.Refresh();
                    Toast(Glyphs.Picture, "Kapak resimleri indiriliyor…");
                    await SteamGridDb.EnrichAsync(_allGames, key);
                }, "steamgriddb.com adresinden aldığın anahtarı yaz veya yapıştır (Ctrl+V çalışmaz; klavyeyle yaz).", secret: true),
            },
            new()
            {
                Glyph = Glyphs.Refresh, Label = "Kütüphaneyi yenile",
                Help = "Oyunları, uygulamaları ve medyayı yeniden tarar.",
                Value = () => $"{_allGames.Count} oyun · {_allApps.Count} uygulama",
                Activate = () => _ = ReloadLibrary(),
            },
            new()
            {
                Section = "Kısayollar", Glyph = Glyphs.Game, Label = "Nova'ya dön",
                Help = "Bir oyundayken ya da Nova küçültülmüşken bu kısayollar Nova'yı öne getirir.\n\n" +
                       "Klavye: Ctrl + Alt + N\nXbox kolu: Start + View birlikte\nPS kolu: Options + Create birlikte\n\n" +
                       "Arama: S · Kontrol Merkezi: C · Güç: P",
                Value = () => "Ctrl+Alt+N · Start+View",
            },
        };
        rows.InsertRange(rows.Count - 1, ExtraSettingRows());
        var view = new SettingsView(rows);
        _settingsView = view;
        PushOverlay(view, view, fullBleed: true);
    }

    void PickWallpaper()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Duvar kağıdı seç", Filter = "Resimler|*.jpg;*.jpeg;*.png;*.bmp;*.webp|Tüm dosyalar|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        Settings.WallpaperPath = dlg.FileName;
        Settings.Save();
        ApplyWallpaper();
        _settingsView?.Refresh();
    }

    void PickMusic(bool folder)
    {
        string? path = null;
        if (folder)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Müzik klasörü seç" };
            if (dlg.ShowDialog(this) == true) path = dlg.FolderName;
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Müzik dosyası seç", Filter = "Ses dosyaları|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac|Tüm dosyalar|*.*" };
            if (dlg.ShowDialog(this) == true) path = dlg.FileName;
        }
        if (path == null) return;
        Settings.MusicPath = path;
        Settings.Save();
        _music.Configure(path, Settings.MusicVolume);
        UpdateMusic();
        _settingsView?.Refresh();
    }

    // ───────────────────────────── Ortam ışığı ─────────────────────────────

    void OnSelectionChanged(TileItem? item)
    {
        // Yalnızca görünen sekmenin seçimi arka planı belirler.
        if (_tab >= 0 && item != null && !ReferenceEquals(_tabs[_tab].Screen.Selected, item)) return;
        if (_tab >= 0) UpdateHints();
        _ambientPending = item;
        _ambientTimer.Stop();
        _ambientTimer.Start();
    }

    async void ApplyAmbient()
    {
        _ambientTimer.Stop();
        var item = _ambientPending;
        if (_ambientItem != null) _ambientItem.PropertyChanged -= OnAmbientItemChanged;
        _ambientItem = item;
        if (item != null) item.PropertyChanged += OnAmbientItemChanged;

        if (item?.HeroPath != null)
        {
            var hero = await ImageCache.LoadAsync(item.HeroPath, 1920);
            if (_ambientItem != item) return;
            if (hero != null)
            {
                ShowHero(hero);
                ShowGlow(null);
                return;
            }
        }

        ShowHero(null);
        ShowGlow(item == null ? null : GlowBrushFor(item));
    }

    void OnAmbientItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TileItem t || t != _ambientItem) return;
        if (e.PropertyName == nameof(TileItem.HeroPath)) { _ambientPending = t; ApplyAmbient(); }
        else if (e.PropertyName == nameof(TileItem.Thumb) && t.HeroPath == null) ShowGlow(GlowBrushFor(t));
    }

    static Brush? GlowBrushFor(TileItem item)
    {
        if (ImageCache.Tiny(item.Thumb) is { } tiny)
            return Theme.Freeze(new ImageBrush(tiny) { Stretch = Stretch.Fill });
        if (item.Accent is { } c)
            return Theme.Freeze(new SolidColorBrush(c));
        return null;
    }

    void ShowGlow(Brush? brush)
    {
        var dur = TimeSpan.FromMilliseconds(650);
        if (brush == null)
        {
            _glowFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
            return;
        }
        _glowBack.Fill = brush;
        _glowBack.BeginAnimation(OpacityProperty, new DoubleAnimation(0.8, dur));
        _glowFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
        (_glowFront, _glowBack) = (_glowBack, _glowFront);
    }

    void ShowHero(ImageSource? img)
    {
        if (ReferenceEquals(img, _heroShown)) return;
        _heroShown = img;
        var dur = TimeSpan.FromMilliseconds(600);
        if (img == null)
        {
            _heroFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
            return;
        }
        ((Image)_heroBack.Children[0]).Source = img;
        _heroBack.BeginAnimation(OpacityProperty, new DoubleAnimation(1, dur));
        _heroFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
        (_heroFront, _heroBack) = (_heroBack, _heroFront);
    }

    // ───────────────────────────── İpuçları ve bildirimler ─────────────────────────────

    void UpdateHints()
    {
        List<(NavAction, string)> hints;
        if (_overlays.Count > 0) hints = _overlays[^1].Hints.ToList();
        else
        {
            hints = _tabs[Math.Max(0, _tab)].Screen.Hints.Where(h => h.Item1 != NavAction.Menu).ToList();
            hints.Add((NavAction.Search, "Ara"));
            hints.Add((NavAction.Control, "Kontrol"));
        }
        var key = _device + "|" + string.Join(";", hints.Select(h => h.Item1 + h.Item2));
        if (key == _hintKey) return;
        _hintKey = key;

        HintBar.Children.Clear();
        foreach (var (action, label) in hints)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 0, 0, 0) };
            row.Children.Add(KeyBadge(action));
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 18,
                Foreground = (Brush)FindResource("TextDim"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 2),
            });
            HintBar.Children.Add(row);
        }
    }

    FrameworkElement KeyBadge(NavAction a)
    {
        bool typing = _overlays.LastOrDefault() is ITextTarget;
        (string text, Color? ring) = _device == InputKind.Gamepad
            ? a switch
            {
                NavAction.Confirm => ("A", Theme.Hex("#4CC38A")),
                NavAction.Back => ("B", Theme.Hex("#F0626A")),
                NavAction.Options => ("Y", Theme.Hex("#F2C14E")),
                NavAction.Search => ("X", Theme.Hex("#5B9BF8")),
                NavAction.Control => ("View", (Color?)null),
                NavAction.TabPrev or NavAction.TabNext => ("LB  RB", null),
                NavAction.Menu => ("☰", null),
                NavAction.PageUp or NavAction.PageDown => ("LT  RT", null),
                NavAction.Up or NavAction.Down => ("▲▼", null),
                _ => ("◀ ▶", null),
            }
            : a switch
            {
                NavAction.Confirm => ("Enter", (Color?)null),
                NavAction.Back => ("Esc", null),
                NavAction.Options => (typing ? "Boşluk" : "O", null),
                NavAction.Search => (typing ? "⌫" : "S", null),
                NavAction.Control => ("C", null),
                NavAction.TabPrev or NavAction.TabNext => ("Q  E", null),
                NavAction.Menu => ("P", null),
                NavAction.PageUp or NavAction.PageDown => ("PgUp PgDn", null),
                NavAction.Up or NavAction.Down => ("▲▼", null),
                _ => ("◀ ▶", null),
            };

        var tb = new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 1),
        };
        if (ring is { } c)
        {
            tb.Foreground = new SolidColorBrush(c);
            return new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                BorderBrush = new SolidColorBrush(c), BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
                Child = tb,
            };
        }
        return new Border
        {
            MinWidth = 30, Height = 30, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 0, 10, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), BorderThickness = new Thickness(1),
            Child = tb,
        };
    }

    void Toast(string glyph, string text)
    {
        _notices.Add(new Notice(glyph, text, DateTime.Now));
        if (_notices.Count > 30) _notices.RemoveAt(0);
        // Nova arka plandayken (oyun açıkken) bildirimi oyunun üstünde göster.
        if (!IsVisible || !IsActive) { _hud.Notify(glyph, text); return; }

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = glyph,
            Style = (Style)FindResource("GlyphText"),
            FontSize = 22,
            Width = 30,
        });
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 19,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Margin = new Thickness(16, 0, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var shift = new TranslateTransform(520, 0);
        var card = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x18, 0x20, 0x34)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 18, 24, 18),
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Right,
            RenderTransform = shift,
            Child = content,
        };
        ToastHost.Children.Insert(0, card);
        while (ToastHost.Children.Count > 3) ToastHost.Children.RemoveAt(ToastHost.Children.Count - 1);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromSeconds(3.2) };
        fade.Completed += (_, _) => ToastHost.Children.Remove(card);
        card.BeginAnimation(OpacityProperty, fade);
    }
}
