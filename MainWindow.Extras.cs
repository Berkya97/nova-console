using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Nova.Core;
using Nova.Models;
using Nova.Services;
using Nova.Views;

namespace Nova;

/// <summary>
/// 0.3 özellikleri: arka plan modu, oyun içi menü, ekran görüntüsü, kupalar, hava durumu, TV sekmesi,
/// indirme durumu, telefonla kumanda, çoklu kullanıcı, uyku zamanlayıcısı, kurulum ve güncelleme.
/// </summary>
public partial class MainWindow
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    readonly ProfileStore _store;
    readonly string? _forcedProfile;
    readonly bool _startHidden, _skipBoot;
    readonly HomeRowView _tv = new() { CountFormat = "{0} servis" };
    readonly RemoteServer _remote = new();
    DesktopMode _desktop = null!;
    readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    readonly DispatcherTimer _downloadTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    HudWindow _hud = null!;
    Tray _tray = null!;
    QuickMenu? _quick;
    bool _pendingPicker, _reallyExit, _noWatcher;
    string? _weather;
    Updater.Release? _pendingUpdate;

    const int HotkeyReturn = 1, HotkeyMenu = 2, HotkeyShot = 3;
    static readonly (int Id, char Key)[] Hotkeys = { (HotkeyReturn, 'N'), (HotkeyMenu, 'G'), (HotkeyShot, 'S') };

    // ───────────────────────────── Kurulum ─────────────────────────────

    void InitExtras()
    {
        _hud = new HudWindow();
        _tray = new Tray { Visible = Settings.BackgroundMode };
        _tray.OpenRequested += BringBack;
        _tray.ExitRequested += QuitCompletely;

        _desktop = new DesktopMode(_pad);
        _desktop.Changed += on => Toast(Glyphs.Mouse, on ? "Masaüstü modu açık: kol fare oldu (çıkmak için View + R3)" : "Masaüstü modu kapatıldı");

        _remote.Pin = Settings.RemotePin;
        _remote.Action += a =>
        {
            // Masaüstü modunda telefon tuşları kolun masaüstü karşılıklarını yapar, Nova'yı öne getirmez.
            if (_desktop.Active) { _desktop.Remote(a); return; }
            if (!IsVisible || !IsActive) BringBack();
            SetDevice(InputKind.Gamepad);
            if (_quick != null) _quick.Handle(a); else Dispatch(a);
        };
        // Nova öndeyse yazı Nova'nın yazı kutusuna, başka bir uygulama öndeyse SendInput ile o uygulamaya gider.
        _remote.Text += t => { if (NovaForeground) (_overlays.LastOrDefault() as ITextTarget)?.TypeText(t); else Injector.Type(t); };
        _remote.Backspace += () => { if (NovaForeground) (_overlays.LastOrDefault() as ITextTarget)?.Backspace(); else Injector.Press(Injector.VK_BACK); };
        _remote.Submit += () => { if (NovaForeground) (_overlays.LastOrDefault() as ITextTarget)?.Submit(); else Injector.Press(Injector.VK_RETURN); };
        _remote.Key += k =>
        {
            if (k == "desktop") ToggleDesktopMode();
            else Injector.Press(k == "tab" ? Injector.VK_TAB : Injector.VK_ESCAPE);
        };
        _remote.Mouse += (dx, dy, w) =>
        {
            if (dx != 0 || dy != 0) Injector.MoveBy(dx, dy);
            if (w != 0) Injector.Wheel(w);
        };
        _remote.Click += right => { if (right) Injector.RightClick(); else Injector.LeftClick(); };
        _remote.Connected += () =>
        {
            Settings.Count("remote", 1);
            Settings.Save();
            Toast(Glyphs.Phone, "Telefon bağlandı");
            CheckTrophies();
        };

        SleepTimer.Warning += msg => Toast(Glyphs.Sleep, msg);
        SleepTimer.Expired += () =>
        {
            if (SleepTimer.Shutdown) RunShutdown("/s /t 0");
            else SetSuspendState(false, false, false);
        };
        PlaytimeTracker.LimitWarning += msg => { Sfx.Bump(); Toast(Glyphs.Timer, msg); };

        _weatherTimer.Tick += (_, _) => RefreshWeather();
        _downloadTimer.Tick += (_, _) => RefreshDownloads();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Nova'nın bir penceresi (ana pencere ya da oyun içi menü) önde mi.</summary>
    static bool NovaForeground => Application.Current.Windows.OfType<Window>().Any(w => w.IsActive);

    // ───────────────────────────── Uyku / uyanma ─────────────────────────────

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.Mode == PowerModes.Suspend) _desktop.ReleaseHeld();
        else if (e.Mode == PowerModes.Resume) OnResume();
    });

    /// <summary>
    /// Uykudan dönüş: kablosuz kollar yeniden bağlanırken XInput yuvaları ve PlayStation kol listesi bayatlamış
    /// olabilir, baştan taranır. Kısayollar da (ör. başka bir oturum araya girdiyse) sessizce yeniden kaydedilir.
    /// </summary>
    void OnResume()
    {
        _pad.Rescan();
        uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        foreach (var (id, key) in Hotkeys)
        {
            Native.UnregisterHotKey(_hwnd, id);
            Native.RegisterHotKey(_hwnd, id, mods, key);
        }
    }

    // ───────────────────────────── Oyun başladı ─────────────────────────────

    /// <summary>
    /// Başlatılan oyunun süreci göründü. Oyun bir başlatıcı (ör. EA app) üzerinden açıldığında Windows ona
    /// odağı vermeyebilir; o zaman Nova etkin pencere olarak kalır ve kolu oyunla birlikte o da okur
    /// (menüde gezinme sesleri gelir). Bu durumda Nova kenara çekilir, kola oyun kalır.
    /// </summary>
    void OnGameStarted(string title)
    {
        _desktop.Disable();
        if (!IsVisible || !IsActive) return;
        if (Settings.BackgroundMode) HideToTray();
        else WindowState = WindowState.Minimized;
    }

    // ───────────────────────────── Masaüstü modu ─────────────────────────────

    void ToggleDesktopMode()
    {
        Sfx.Confirm();
        _desktop.Toggle();
        if (_desktop.Active) Settings.Count("desktopmode", 1);
    }

    /// <summary>
    /// Test betiği komutları: desktop, osk, hide, resume, remote:PIN (kaydetmeden başlatır), fg:süreç-ya-da-başlık,
    /// inject:metin, key:ad, pad:lx,ly,rx,ry[,A+B] (sahte kol durumu), pad:off.
    /// </summary>
    bool DesktopDevCommand(string token)
    {
        if (token == "desktop") ToggleDesktopMode();
        else if (token == "osk") _desktop.ToggleKeyboard();
        else if (token == "hide") Hide();
        else if (token == "resume") OnResume();
        else if (token.StartsWith("remote:")) { _remote.Pin = token[7..]; _remote.Start(); }
        else if (token.StartsWith("fg:"))
        {
            var name = token[3..].Replace('|', ' ');
            var p = Process.GetProcesses().FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero &&
                (p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase) || p.MainWindowTitle == name));
            if (p != null) Native.ForceForeground(p.MainWindowHandle);
        }
        else if (token.StartsWith("inject:")) Injector.Type(token[7..].Replace('_', ' '));
        else if (token.StartsWith("key:"))
        {
            Injector.Press(token[4..] switch
            {
                "enter" => Injector.VK_RETURN, "tab" => Injector.VK_TAB, "back" => Injector.VK_BACK,
                "esc" => Injector.VK_ESCAPE, "up" => Injector.VK_UP, "down" => Injector.VK_DOWN,
                "left" => Injector.VK_LEFT, _ => Injector.VK_RIGHT,
            });
        }
        else if (token == "pad:off") _desktop.Simulated = null;
        else if (token.StartsWith("pad:"))
        {
            var parts = token[4..].Split(',');
            double N(int i) => i < parts.Length && double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            var buttons = PadButtons.None;
            if (parts.Length > 4)
                foreach (var b in parts[4].Split('+'))
                    if (Enum.TryParse<PadButtons>(b, true, out var pb)) buttons |= pb;
            _desktop.Simulated = new PadState(buttons, N(0), N(1), N(2), N(3));
        }
        else return false;
        return true;
    }

    void RegisterHotkeys()
    {
        uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        bool ok = Native.RegisterHotKey(_hwnd, HotkeyReturn, mods, 'N');
        if (ok && !Native.RegisterHotKey(_hwnd, HotkeyMenu, mods, 'G'))
            Dispatcher.BeginInvoke(() => Toast(Glyphs.Info, "Ctrl+Alt+G başka bir uygulamada kullanılıyor; oyun içi menüyü kolda Start+View ile açabilirsin"));
        if (ok && !Native.RegisterHotKey(_hwnd, HotkeyShot, mods, 'S'))
            Dispatcher.BeginInvoke(() => Toast(Glyphs.Info, "Ctrl+Alt+S başka bir uygulamada kullanılıyor"));
        if (!ok)
        {
            // Yeniden başlatmada eski kopya kısayolu henüz bırakmamış olabilir.
            var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            int tries = 0;
            retry.Tick += (_, _) =>
            {
                if (Native.RegisterHotKey(_hwnd, HotkeyReturn, mods, 'N') || ++tries > 10)
                {
                    Native.RegisterHotKey(_hwnd, HotkeyMenu, mods, 'G');
                    Native.RegisterHotKey(_hwnd, HotkeyShot, mods, 'S');
                    retry.Stop();
                }
            };
            retry.Start();
        }
    }

    void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyReturn: BringBack(); break;
            case HotkeyMenu: if (!IsActive) OpenQuickMenu(); break;
            case HotkeyShot: TakeScreenshot(); break;
        }
    }

    void CleanupExtras()
    {
        foreach (var (id, _) in Hotkeys) Native.UnregisterHotKey(_hwnd, id);
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _desktop.Disable();
        _remote.Stop();
        _tray.Dispose();
        _hud.Close();
        _quick?.Close();
        // Kapandıktan sonra da Ctrl+Alt+N ve kol kombinasyonuyla açılabilsin.
        if (!_noWatcher && Settings.WakeWhenClosed && !DevScript.Active) Watcher.Spawn();
    }

    void StartExtras()
    {
        if (!Settings.DaysUsed.Contains(AppSettings.Today))
        {
            Settings.DaysUsed.Add(AppSettings.Today);
            if (Settings.DaysUsed.Count > 60) Settings.DaysUsed.RemoveAt(0);
            Settings.Save();
        }
        if (Settings.RemoteEnabled) _remote.Start();
        RefreshWeather();
        _weatherTimer.Start();
        _downloadTimer.Start();
        _tv.SetItems(Tv.Items());
    }

    async Task LoadExtrasAsync()
    {
        CheckTrophies();
        await Tv.LoadIconsAsync(_tv.Items);
        if (Settings.UpdateCheckedDay != AppSettings.Today)
        {
            Settings.UpdateCheckedDay = AppSettings.Today;
            Settings.Save();
            await CheckUpdatesAsync(manual: false);
        }
    }

    // ───────────────────────────── Arka plan modu ─────────────────────────────

    protected override void OnClosing(CancelEventArgs e)
    {
        // Arka plan modunda pencereyi kapatmak Nova'yı gizler; kol ya da kısayolla geri açılır.
        if (!_reallyExit && Settings.BackgroundMode && !DevScript.Active)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    void HideToTray()
    {
        _overlays.OfType<MediaViewer>().ToList().ForEach(v => v.Close());
        Hide();
        _tray.Visible = true;
        _tray.HintOnce();
        Particles.Pause();
        UpdateMusic();
    }

    void ReallyExit()
    {
        _reallyExit = true;
        Close();
    }

    /// <summary>Dinleyici de kalmadan tamamen kapat (simge alanındaki "Tamamen kapat").</summary>
    void QuitCompletely()
    {
        _noWatcher = true;
        ReallyExit();
    }

    /// <summary>Nova'yı kapatıp verilen exe ile yeniden açar (dil değişikliği, güncelleme, kurulum).</summary>
    void Restart(string? exe = null)
    {
        _reallyExit = true;
        _noWatcher = true;
        SingleInstance.Release();
        try { Process.Start(new ProcessStartInfo(exe ?? Environment.ProcessPath!, "--noboot") { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
        Close();
    }

    // ───────────────────────────── Oyun içi menü ve ekran görüntüsü ─────────────────────────────

    void OnReturnCombo()
    {
        // Masaüstü modundan Nova'ya dönerken kol yeniden Nova'yı yönetsin.
        if (_desktop.Active) _desktop.Disable();
        if (IsActive || _quick != null) return;
        // Nova gizliyse ya da masaüstündeysek doğrudan Nova'yı aç; bir oyun öndeyse hızlı menüyü göster.
        var fg = GetForegroundWindow();
        var name = Capture.ForegroundName(fg) ?? "";
        if (!IsVisible && (fg == IntPtr.Zero || name is "explorer" or "ShellExperienceHost")) BringBack();
        else OpenQuickMenu();
    }

    void OpenQuickMenu()
    {
        if (_quick != null) return;
        var target = GetForegroundWindow();
        if (target == _hwnd) return;
        var menu = new QuickMenu(target);
        menu.ReturnToNova += BringBack;
        menu.Notify += (g, t) => Toast(g, t);
        menu.DesktopModeRequested += () => { if (!_desktop.Active) ToggleDesktopMode(); };
        menu.Closed += (_, _) => { _quick = null; CheckTrophies(); };
        _quick = menu;
        Sfx.Confirm();
        menu.Show();
    }

    void TakeScreenshot()
    {
        var path = Capture.Screenshot();
        if (path == null) { Toast(Glyphs.Error, "Ekran görüntüsü alınamadı"); return; }
        Settings.Count("screenshots", 1);
        Settings.Save();
        Sfx.Confirm();
        Toast(Glyphs.Picture, "Ekran görüntüsü kaydedildi: Resimler › Nova");
        CheckTrophies();
    }

    // ───────────────────────────── Kullanıcılar ─────────────────────────────

    async void ApplyProfileVisuals()
    {
        var profile = _store.Profiles.FirstOrDefault(p => p.Id == AppSettings.ProfileId) ?? _store.Profiles[0];
        UserText.Text = profile.Name;
        AvatarText.Text = profile.Name.Length > 0 ? profile.Name[..1].ToUpper(Fmt.Tr) : "?";
        AvatarText.Visibility = Visibility.Visible;
        AvatarPhoto.Visibility = Visibility.Collapsed;
        Sfx.Enabled = Settings.Sounds || DevScript.Active;
        ApplyTheme();
        ApplyWallpaper();
        _music.Configure(Settings.MusicPath, Settings.MusicVolume);
        UpdateMusic();

        if (profile.Id != "main") return;
        var pic = await Task.Run(AccountPicture.Load);
        if (pic == null || AppSettings.ProfileId != "main") return;
        AvatarPhoto.Fill = new ImageBrush(pic) { Stretch = Stretch.UniformToFill };
        AvatarPhoto.Visibility = Visibility.Visible;
        AvatarText.Visibility = Visibility.Collapsed;
    }

    async void ShowProfilePicker(bool initial)
    {
        var photo = await Task.Run(AccountPicture.Load);
        var picker = new ProfilePicker(_store, photo, cancellable: !initial);
        picker.Picked += p =>
        {
            if (p.Id != AppSettings.ProfileId) SwitchProfile(p.Id);
            else { _store.LastId = p.Id; _store.Save(); }
            if (initial) RevealStage();
        };
        picker.AddRequested += () => AskText("Yeni kullanıcı", "", name =>
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            _store.Profiles.Add(new Profile { Id = id, Name = name, Color = _store.Profiles.Count });
            _store.Save();
            picker.Close();
            ShowProfilePicker(initial);
        }, "Bu kişinin adını yaz. Her kullanıcının sabitledikleri, oynama süreleri ve ayarları ayrı tutulur.");
        PushOverlay(picker, picker, fullBleed: true);
    }

    async void SwitchProfile(string id)
    {
        AppSettings.Use(id);
        _store.LastId = id;
        _store.Save();
        ApplyProfileVisuals();
        _remote.Pin = Settings.RemotePin;
        SetRemote(Settings.RemoteEnabled, quiet: true);
        SwitchTab(Math.Clamp(Settings.DefaultTab, 0, _tabs.Count - 1));
        var name = _store.Profiles.FirstOrDefault(p => p.Id == id)?.Name ?? "";
        Toast(Glyphs.Person, $"Hoş geldin, {name}");
        _tv.SetItems(Tv.Items());
        _ = Tv.LoadIconsAsync(_tv.Items);
        _files.ShowRoot();
        await ReloadGames();
    }

    // ───────────────────────────── Kupalar ─────────────────────────────

    void OnLaunched(TileItem item)
    {
        Settings.Count("launches", 1);
        if (item.Kind == TileKind.Game)
        {
            Settings.Count("game_launches", 1);
            if (DateTime.Now.Hour < 5) Settings.Count("night_launches", 1);
        }
        Settings.Save();
        CheckTrophies();
    }

    void CheckTrophies()
    {
        foreach (var t in Trophies.Check(new Trophies.Context(_allGames)))
        {
            Sfx.Confirm();
            Toast(Glyphs.Trophy, $"Kupa kazandın: {t.Title}");
        }
    }

    void OpenTrophies(string? appId, string? title)
    {
        var view = new TrophiesView(appId, title);
        PushOverlay(view, view, fullBleed: true);
    }

    void AddExtraOptions(List<OptionsMenu.Entry> e, TileItem item)
    {
        if (item.Key.StartsWith("steam:"))
            e.Insert(Math.Min(2, e.Count), new(Glyphs.Trophy, "Kupalar", () => OpenTrophies(item.Key[6..], item.Title)));
        if (item.Key.StartsWith("tv:") && item.IsCustom)
            e.Add(new(Glyphs.Delete, "Bu siteyi kaldır", () =>
            {
                Settings.TvServices.RemoveAll(s => s.Url == item.Tag as string);
                Settings.Save();
                _tv.SetItems(Tv.Items());
                _ = Tv.LoadIconsAsync(_tv.Items);
            }));
    }

    // ───────────────────────────── TV ve Video ─────────────────────────────

    void AddTvSite()
    {
        AskText("Site adı", "", name =>
            AskText("Adres", "https://", url =>
            {
                if (!url.StartsWith("http")) url = "https://" + url;
                if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { Toast(Glyphs.Error, "Adres geçersiz"); return; }
                Settings.TvServices.Add(new TvService { Name = name, Url = url });
                Settings.Save();
                _tv.SetItems(Tv.Items(), _tv.Items.Count - 1);
                _ = Tv.LoadIconsAsync(_tv.Items);
                Toast(Glyphs.Tv, $"{name} eklendi");
            }, "Sitenin adresini yaz, örneğin https://www.ornek.com"),
            "Kartın üzerinde görünecek ad");
    }

    // ───────────────────────────── Hava durumu ve indirmeler ─────────────────────────────

    async void RefreshWeather()
    {
        var info = await Weather.GetAsync(Settings.WeatherCity);
        if (info == null)
        {
            WeatherText.Visibility = Visibility.Collapsed;
            _weather = null;
            return;
        }
        var (sym, text) = Weather.Describe(info.Code);
        var temp = $"{Math.Round(info.Temp):0}°";
        WeatherText.Text = $"{sym}  {temp}";
        WeatherText.ToolTip = $"{info.City} · {L.T(text)}";
        WeatherText.Visibility = Visibility.Visible;
        _weather = $"{sym}|{info.City}\n{temp} · {L.T(text)}";
    }

    bool _downloadBusy;

    async void RefreshDownloads()
    {
        if (_downloadBusy || !IsVisible) return;
        var steam = _allGames.Where(g => g.StatusFile != null).ToList();
        if (steam.Count == 0) return;
        _downloadBusy = true;
        try
        {
            var states = await Task.Run(() => steam.Select(g => (g, SteamOnline.DownloadState(g.StatusFile!))).ToList());
            foreach (var (g, s) in states)
            {
                if (s is { } d)
                {
                    g.Progress = d.Progress;
                    g.SetSubtitle($"Steam  ·  İndiriliyor %{(int)(d.Progress * 100)}  ·  {Fmt.Size(d.Done)} / {Fmt.Size(d.Total)}");
                }
                else if (!double.IsNaN(g.Progress))
                {
                    g.Progress = double.NaN;
                    GameLibrary.UpdateSubtitle(g);
                    Toast(Glyphs.Download, $"{g.Title} indirildi, oynamaya hazır");
                }
            }
        }
        finally { _downloadBusy = false; }
    }

    // ───────────────────────────── Telefonla kumanda ─────────────────────────────

    string RemoteStatus() => _remote.Running ? $"{_remote.Url.Replace("http://", "")}\nPIN {_remote.Pin}" : "Kapalı";

    void SetRemote(bool on, bool quiet = false)
    {
        Settings.RemoteEnabled = on;
        Settings.Save();
        if (!on) { _remote.Stop(); if (!quiet) Toast(Glyphs.Phone, "Telefonla kumanda kapatıldı"); return; }
        if (_remote.Start())
        {
            if (!quiet) Toast(Glyphs.Phone, $"Telefonundan {_remote.Url} adresini aç, PIN: {_remote.Pin}");
        }
        else Toast(Glyphs.Error, $"Telefonla kumanda başlatılamadı ({RemoteServer.Port} numaralı bağlantı noktası kullanımda olabilir)");
    }

    // ───────────────────────────── Güncelleme ve kurulum ─────────────────────────────

    async Task CheckUpdatesAsync(bool manual)
    {
        var r = await Updater.CheckAsync();
        if (r != null && Updater.IsNewer(r))
        {
            _pendingUpdate = r;
            Toast(Glyphs.Download, $"Yeni sürüm hazır: {r.Tag} · Ayarlar › Güncelleme");
        }
        else if (manual) Toast(Glyphs.Accept, r == null ? "Güncelleme sunucusuna ulaşılamadı" : "Nova güncel");
        _settingsView?.Refresh();
    }

    async void InstallUpdate()
    {
        if (_pendingUpdate is not { } r) { await CheckUpdatesAsync(manual: true); return; }
        Toast(Glyphs.Download, $"{r.Tag} indiriliyor…");
        try
        {
            int last = -1;
            var exe = await Updater.DownloadAndReplaceAsync(r, new Progress<double>(p =>
            {
                int step = (int)(p * 4);
                if (step != last && step > 0 && step < 4) { last = step; Toast(Glyphs.Download, $"İndiriliyor %{step * 25}"); }
            }));
            Toast(Glyphs.Restart, "Güncelleme kuruldu, Nova yeniden başlatılıyor");
            await Task.Delay(1200);
            Restart(exe);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Toast(Glyphs.Error, "Güncelleme kurulamadı: " + ex.Message);
        }
    }

    void InstallApp()
    {
        try
        {
            var exe = Installer.Install(desktopShortcut: true);
            ShowMenu(new OptionsMenu("Nova kuruldu: Başlat menüsü ve masaüstüne kısayol eklendi", new List<OptionsMenu.Entry>
            {
                new(Glyphs.Play, "Kurulu Nova'yı başlat", () => Restart(exe)),
                new(Glyphs.Accept, "Tamam", () => { }),
            }));
        }
        catch (Exception ex) { Toast(Glyphs.Error, "Kurulum yapılamadı: " + ex.Message); }
    }

    // ───────────────────────────── Ayarlar ─────────────────────────────

    List<SettingRow> ExtraSettingRows()
    {
        static string OnOff(bool b) => b ? "Açık" : "Kapalı";
        var limits = new[] { 0, 60, 120, 180, 240, 360 };
        var profile = _store.Profiles.FirstOrDefault(p => p.Id == AppSettings.ProfileId);

        return new List<SettingRow>
        {
            new()
            {
                Section = "Arka plan", Glyph = Glyphs.Minimize, Label = "Arka plan modu",
                Help = "Açıkken Nova kapatılınca tamamen kapanmaz, saatin yanındaki simge alanında bekler. " +
                       "Ctrl+Alt+N ya da kolda Start+View ile anında açılır. Windows açılışında başlat da açıksa Nova görünmeden başlar.",
                Value = () => OnOff(Settings.BackgroundMode),
                Change = _ => { Settings.BackgroundMode = !Settings.BackgroundMode; Settings.Save(); _tray.Visible = Settings.BackgroundMode; },
            },
            new()
            {
                Glyph = Glyphs.Power, Label = "Kapalıyken de aç",
                Help = "Açıkken Nova kapatıldığında arka planda penceresiz, çok küçük bir dinleyici kalır. " +
                       "Ctrl+Alt+N'ye ya da kolda Start+View (PS kolunda Options+Create) kombinasyonuna basınca Nova açılır. " +
                       "Simge alanındaki Nova simgesinden \"Tamamen kapat\" dersen dinleyici de kapanır.",
                Value = () => OnOff(Settings.WakeWhenClosed),
                Change = _ => { Settings.WakeWhenClosed = !Settings.WakeWhenClosed; Settings.Save(); },
            },
            new()
            {
                Glyph = Glyphs.Language, Label = "Dil / Language",
                Help = "Arayüz dili. Değişiklik Nova yeniden başlatılınca uygulanır.\n\nInterface language. Applied after restarting Nova.",
                Value = () => Settings.Language == "en" ? "English" : "Türkçe",
                Change = _ =>
                {
                    Settings.Language = Settings.Language == "en" ? "tr" : "en";
                    Settings.Save();
                    ShowMenu(new OptionsMenu(Settings.Language == "en" ? "Restart Nova to apply" : "Uygulamak için Nova'yı yeniden başlat", new List<OptionsMenu.Entry>
                    {
                        new(Glyphs.Restart, Settings.Language == "en" ? "Restart now" : "Şimdi yeniden başlat", () => Restart()),
                        new(Glyphs.Accept, Settings.Language == "en" ? "Later" : "Sonra", () => { }),
                    }));
                },
            },
            new()
            {
                Glyph = Glyphs.Globe, Label = "Hava durumu",
                Help = "Üst çubukta hava durumu. Otomatik seçilirse konum IP adresinden yaklaşık olarak bulunur (ip-api.com); " +
                       "istersen şehir adını kendin yazabilirsin. Veriler Open-Meteo'dan gelir.",
                Value = () => Settings.WeatherCity switch { null => "Otomatik", "off" => "Kapalı", var c => c },
                Activate = () => ShowMenu(new OptionsMenu("Hava durumu", new List<OptionsMenu.Entry>
                {
                    new(Glyphs.Globe, "Konuma göre otomatik", () => { Settings.WeatherCity = null; Settings.Save(); RefreshWeather(); _settingsView?.Refresh(); }),
                    new(Glyphs.Rename, "Şehir yaz…", () => AskText("Şehir", Settings.WeatherCity is { } c && c != "off" ? c : "", city =>
                    {
                        Settings.WeatherCity = city; Settings.Save(); RefreshWeather(); _settingsView?.Refresh();
                    })),
                    new(Glyphs.Close, "Kapat", () => { Settings.WeatherCity = "off"; Settings.Save(); RefreshWeather(); _settingsView?.Refresh(); }),
                })),
            },
            new()
            {
                Section = "Oyun süresi", Glyph = Glyphs.Timer, Label = "Günlük oyun süresi sınırı",
                Help = "Nova'dan başlatılan oyunlarda bugünkü toplam süre bu sınıra yaklaşınca ve dolunca oyunun üstünde uyarı çıkar. Oyun kapatılmaz.",
                Value = () => Settings.PlayLimitMinutes == 0 ? "Kapalı" : $"{Settings.PlayLimitMinutes / 60} saat  ·  bugün {(int)Settings.PlayedToday()} dk",
                Change = d =>
                {
                    int i = Array.IndexOf(limits, Settings.PlayLimitMinutes);
                    Settings.PlayLimitMinutes = limits[((i < 0 ? 0 : i) + d + limits.Length) % limits.Length];
                    Settings.Save();
                },
            },
            new()
            {
                Glyph = Glyphs.Sleep, Label = "Uyku zamanlayıcısı",
                Help = "Süre dolunca bilgisayar uyku moduna geçer ya da kapanır. 1 dakika önce uyarı gelir. Kontrol Merkezi'nden de ayarlanır.",
                Value = () => SleepTimer.Describe(),
                Change = _ => SleepTimer.NextStep(),
            },
            new()
            {
                Glyph = Glyphs.Power, Label = "Zamanlayıcı bitince",
                Help = "Uyku zamanlayıcısı dolduğunda ne olsun.",
                Value = () => SleepTimer.Shutdown ? "Bilgisayarı kapat" : "Uyku modu",
                Change = _ => SleepTimer.Shutdown = !SleepTimer.Shutdown,
            },
            new()
            {
                Section = "Telefon", Glyph = Glyphs.Phone, Label = "Telefonla kumanda",
                Help = "Aynı Wi-Fi ağındaki telefonunun tarayıcısından Nova'yı yönet: yön tuşları, A/B/X/Y ve hızlı yazma. " +
                       "İlk açılışta Windows Güvenlik Duvarı izin isteyebilir; özel ağlar için izin ver. Erişim PIN ile korunur.",
                Value = () => _remote.Running ? $"{_remote.Url}  ·  PIN {_remote.Pin}" : "Kapalı",
                Change = _ => SetRemote(!Settings.RemoteEnabled),
            },
            new()
            {
                Glyph = Glyphs.Key, Label = "PIN'i yenile",
                Help = "Telefonla kumanda için yeni bir PIN oluşturur. Daha önce bağlanan telefonlar yeniden PIN girer.",
                Value = () => Settings.RemotePin,
                Activate = () =>
                {
                    Settings.RemotePin = Random.Shared.Next(1000, 10000).ToString();
                    Settings.Save();
                    _remote.Pin = Settings.RemotePin;
                    if (_remote.Running) { _remote.Stop(); _remote.Start(); }
                },
            },
            new()
            {
                Section = "Kullanıcılar", Glyph = Glyphs.Person, Label = "Kullanıcılar",
                Help = "Her kullanıcının sabitledikleri, oynama süreleri, kupaları ve ayarları ayrıdır. Birden fazla kullanıcı varsa Nova açılırken \"Kim oynuyor?\" diye sorar.",
                Value = () => $"{profile?.Name}  ·  {_store.Profiles.Count} kullanıcı",
                Activate = () =>
                {
                    var e = new List<OptionsMenu.Entry>
                    {
                        new(Glyphs.AddFriend, "Kullanıcı ekle…", () => AskText("Yeni kullanıcı", "", name =>
                        {
                            _store.Profiles.Add(new Profile { Id = Guid.NewGuid().ToString("N")[..8], Name = name, Color = _store.Profiles.Count });
                            _store.Save();
                            Toast(Glyphs.Person, $"{name} eklendi");
                            _settingsView?.Refresh();
                        })),
                        new(Glyphs.Rename, "Bu kullanıcıyı yeniden adlandır…", () => AskText("Ad", profile?.Name ?? "", name =>
                        {
                            if (profile == null) return;
                            profile.Name = name;
                            _store.Save();
                            ApplyProfileVisuals();
                            _settingsView?.Refresh();
                        })),
                    };
                    if (_store.Profiles.Count > 1) e.Insert(0, new(Glyphs.Person, "Kullanıcı değiştir", () => ShowProfilePicker(initial: false)));
                    if (profile != null && profile.Id != "main")
                        e.Add(new(Glyphs.Delete, "Bu kullanıcıyı sil", () =>
                        {
                            var id = profile.Id;
                            _store.Profiles.RemoveAll(p => p.Id == id);
                            _store.Save();
                            AppSettings.DeleteProfileData(id);
                            SwitchProfile("main");
                        }));
                    ShowMenu(new OptionsMenu("Kullanıcılar", e));
                },
            },
            new()
            {
                Section = "Steam", Glyph = Glyphs.Trophy, Label = "Steam Web API anahtarı",
                Help = "Oyunların Steam başarımlarını Kupalar ekranında göstermek için. Ücretsiz anahtar: steamcommunity.com/dev/apikey. " +
                       "Steam profilinde \"Oyun ayrıntıları\" herkese açık olmalı.",
                Value = () => Settings.SteamApiKey is { Length: > 4 } k ? "••••" + k[^4..] : "Ayarlanmadı",
                Activate = () => AskText("Steam Web API anahtarı", Settings.SteamApiKey ?? "", key =>
                {
                    Settings.SteamApiKey = key;
                    Settings.Save();
                    _settingsView?.Refresh();
                }, "steamcommunity.com/dev/apikey adresinden aldığın 32 karakterlik anahtar.", secret: true),
            },
            new()
            {
                Section = "Kurulum ve güncelleme", Glyph = Glyphs.Install,
                Label = Installer.IsInstalledCopy ? "Kurulu" : "Nova'yı bu bilgisayara kur",
                Help = Installer.IsInstalledCopy
                    ? "Nova bu bilgisayarda kurulu: " + Installer.InstallDir + "\nKaldırmak için Windows Ayarlar › Uygulamalar › Nova."
                    : "Nova'yı " + Installer.InstallDir + " klasörüne kopyalar, Başlat menüsüne ve masaüstüne kısayol ekler. Windows Ayarlar › Uygulamalar'dan kaldırılabilir.",
                Value = () => Installer.IsInstalledCopy ? Installer.InstallDir : Installer.IsInstalled ? "Eski kurulumu güncelle" : "",
                Activate = Installer.IsInstalledCopy ? null : InstallApp,
            },
            new()
            {
                Glyph = Glyphs.Download, Label = "Güncelleme",
                Help = "Yeni sürüm GitHub'dan (github.com/" + Updater.Repo + ") indirilir ve Nova kendini günceller. Günde bir kez otomatik denetlenir.",
                Value = () => _pendingUpdate is { } r ? $"{r.Tag} hazır · kurmak için seç" : $"v{Updater.Current.ToString(3)} · denetlemek için seç",
                Activate = InstallUpdate,
            },
        };
    }
}
