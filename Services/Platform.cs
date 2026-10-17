using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Nova.Core;
using WinForms = System.Windows.Forms;

namespace Nova.Services;

/// <summary>Saatin yanındaki simge alanında Nova simgesi (arka plan modu).</summary>
public sealed class Tray : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    bool _balloonShown;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public Tray()
    {
        _icon = new WinForms.NotifyIcon { Text = "Nova", Visible = false };
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/nova.ico"))!.Stream;
            _icon.Icon = new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
        }
        catch { _icon.Icon = System.Drawing.SystemIcons.Application; }

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(L.T("Nova'yı aç"), null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(L.T("Tamamen kapat"), null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke(); };
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>İlk gizlenişte bir kez "arka planda çalışıyor" balonu gösterir.</summary>
    public void HintOnce()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _icon.ShowBalloonTip(4000, "Nova", L.T("Nova arka planda çalışıyor. Açmak için Ctrl+Alt+N ya da kolda Start+View."), WinForms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Nova'nın tek kopya çalışmasını sağlar; ikinci açılış var olan pencereyi öne getirir.</summary>
public static class SingleInstance
{
    static Mutex? _mutex;
    static EventWaitHandle? _show;
    static readonly string Suffix = Environment.UserName.GetHashCode().ToString("x");

    /// <returns>Bu ilk kopyaysa true.</returns>
    public static bool Claim()
    {
        _mutex = new Mutex(true, "Nova.Instance." + Suffix, out bool created);
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, "Nova.Show." + Suffix);
        if (!created)
        {
            _show.Set();
            return false;
        }
        return true;
    }

    /// <summary>Başka bir kopya "göster" dediğinde çağrılır.</summary>
    public static void Listen(Action onShow)
    {
        var t = new Thread(() =>
        {
            while (_show != null && _show.WaitOne())
                Application.Current?.Dispatcher.BeginInvoke(onShow);
        }) { IsBackground = true, Name = "Nova show listener" };
        t.Start();
    }

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }
}

/// <summary>Ekran görüntüsü ve Xbox Game Bar ile klip kaydı.</summary>
public static class Capture
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static string ScreenshotDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Nova");
    public static string ClipsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");

    /// <summary>Öndeki pencerenin bulunduğu ekranın görüntüsünü PNG olarak kaydeder.</summary>
    public static string? Screenshot(string? label = null)
    {
        try
        {
            var fg = GetForegroundWindow();
            label ??= ForegroundName(fg);
            var bounds = WinForms.Screen.FromHandle(fg).Bounds;
            using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            Directory.CreateDirectory(ScreenshotDir);
            var safe = string.Concat((label ?? "Nova").Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
            if (safe.Length == 0) safe = "Nova";
            var path = Path.Combine(ScreenshotDir, $"{safe}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }
        catch (Exception e) { App.Log(e); return null; }
    }

    /// <summary>Xbox Game Bar'ın "Son 30 saniyeyi kaydet" kısayolu (Win+Alt+G).</summary>
    public static void RecordLastSeconds()
    {
        const byte LWin = 0x5B, Alt = 0x12, G = 0x47;
        const uint Up = 2;
        keybd_event(LWin, 0, 0, UIntPtr.Zero);
        keybd_event(Alt, 0, 0, UIntPtr.Zero);
        keybd_event(G, 0, 0, UIntPtr.Zero);
        keybd_event(G, 0, Up, UIntPtr.Zero);
        keybd_event(Alt, 0, Up, UIntPtr.Zero);
        keybd_event(LWin, 0, Up, UIntPtr.Zero);
    }

    public static string? ForegroundName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }
}

/// <summary>İşlemci, bellek ve ekran kartı kullanımı.</summary>
public static class SystemStats
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    static long _idle, _kernel, _user;

    /// <summary>Son çağrıdan bu yana işlemci kullanımı (%).</summary>
    public static int Cpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
        bool first = _kernel == 0;
        _idle = idle; _kernel = kernel; _user = user;
        long total = dk + du;
        return first || total <= 0 ? 0 : (int)Math.Clamp(100 - di * 100 / total, 0, 100);
    }

    public static (int Percent, double UsedGb, double TotalGb) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        double total = m.ullTotalPhys / 1073741824.0, avail = m.ullAvailPhys / 1073741824.0;
        return ((int)m.dwMemoryLoad, total - avail, total);
    }

    static string? _nvidiaSmi;
    static bool _nvidiaChecked;

    /// <summary>Ekran kartı kullanımı ve sıcaklığı. NVIDIA'da nvidia-smi, diğerlerinde Windows sayaçları.</summary>
    public static (int? Percent, int? Temp) Gpu()
    {
        if (!_nvidiaChecked)
        {
            _nvidiaChecked = true;
            _nvidiaSmi = new[]
            {
                Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
                @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
            }.FirstOrDefault(File.Exists);
        }
        if (_nvidiaSmi != null)
        {
            try
            {
                var psi = new ProcessStartInfo(_nvidiaSmi, "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                var line = p.StandardOutput.ReadLine() ?? "";
                p.WaitForExit(2000);
                var parts = line.Split(',');
                if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), out var u) && int.TryParse(parts[1].Trim(), out var tmp))
                    return (u, tmp);
            }
            catch { }
        }
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("root\\CIMV2",
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            ulong sum = 0;
            foreach (System.Management.ManagementObject o in s.Get())
                if ((o["Name"] as string)?.Contains("engtype_3D") == true) sum += Convert.ToUInt64(o["UtilizationPercentage"]);
            return ((int)Math.Min(100, sum), null);
        }
        catch { return (null, null); }
    }
}

/// <summary>Nova'yı bu bilgisayara kurma / kaldırma (kurulum programı gerektirmeden).</summary>
public static class Installer
{
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Nova");
    static string InstalledExe => Path.Combine(InstallDir, "Nova.exe");
    static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Nova.lnk");
    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Nova.lnk");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nova";

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetDirectoryName(Environment.ProcessPath), InstallDir, StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>Çalışan exe'yi kurulum klasörüne kopyalar, Başlat menüsü ve masaüstü kısayolu oluşturur.</summary>
    public static string Install(bool desktopShortcut)
    {
        Directory.CreateDirectory(InstallDir);
        File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
        CreateShortcut(StartMenuLink, InstalledExe);
        if (desktopShortcut) CreateShortcut(DesktopLink, InstalledExe);

        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "Nova");
        k.SetValue("DisplayIcon", InstalledExe);
        k.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "");
        k.SetValue("Publisher", "Berkay Yavuz");
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        // Windows açılışında başlatma açıksa kurulu kopyayı göstersin.
        if (StartupRegistration.IsEnabled()) StartupRegistration.Set(true, InstalledExe);
        return InstalledExe;
    }

    static void CreateShortcut(string link, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null) return;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic sc = shell.CreateShortcut(link);
        sc.TargetPath = target;
        sc.WorkingDirectory = Path.GetDirectoryName(target);
        sc.IconLocation = target + ",0";
        sc.Description = "Nova";
        sc.Save();
        Marshal.FinalReleaseComObject(sc);
        Marshal.FinalReleaseComObject(shell);
    }

    /// <summary>--uninstall ile çağrılır: kısayolları, kayıtları ve kurulum klasörünü siler.</summary>
    public static void Uninstall()
    {
        var answer = MessageBox.Show(L.T("Nova bu bilgisayardan kaldırılsın mı?"), "Nova", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        var wipe = MessageBox.Show(L.T("Ayarların, sabitlediklerin ve oynama sürelerin de silinsin mi?"), "Nova", MessageBoxButton.YesNo, MessageBoxImage.Question);

        foreach (var f in new[] { StartMenuLink, DesktopLink }) { try { File.Delete(f); } catch { } }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        StartupRegistration.Set(false);
        if (wipe == MessageBoxResult.Yes) { try { Directory.Delete(App.DataDir, true); } catch { } }

        // Çalışan exe kendini silemez: kısa bir gecikmeyle klasörü silecek bir komut bırak.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
        { CreateNoWindow = true, UseShellExecute = false });
        MessageBox.Show(L.T("Nova kaldırıldı."), "Nova");
    }
}

/// <summary>GitHub'daki son sürümü denetler, indirir ve exe'yi değiştirip yeniden başlatır.</summary>
public static class Updater
{
    public const string Repo = "Berkya97/nova-console";
    static readonly HttpClient Http = CreateClient();

    public sealed record Release(Version Version, string Tag, string Notes, string? StandaloneUrl, string? SmallUrl);

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Nova-Updater");
        return c;
    }

    public static async Task<Release?> CheckAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var r = doc.RootElement;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            string? standalone = null, small = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("net9", StringComparison.OrdinalIgnoreCase)) small = url; else standalone = url;
            }
            return new Release(v, tag, r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", standalone, small);
        }
        catch { return null; }
    }

    public static bool IsNewer(Release r) => r.Version > new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));

    /// <summary>Yeni exe'yi indirir, çalışanın yerine koyar. Ardından çağıran uygulamayı yeniden başlatmalı.</summary>
    public static async Task<string> DownloadAndReplaceAsync(Release r, IProgress<double>? progress = null)
    {
        var exe = Environment.ProcessPath!;
        // Tek başına (self-contained) exe büyük olur; aynı türü indir.
        bool standalone = new FileInfo(exe).Length > 40_000_000;
        var url = (standalone ? r.StandaloneUrl : r.SmallUrl) ?? r.StandaloneUrl ?? r.SmallUrl
                  ?? throw new InvalidOperationException("Sürümde indirilebilir dosya yok");

        var tmp = Path.Combine(Path.GetTempPath(), "nova-update-" + r.Tag);
        Directory.CreateDirectory(tmp);
        var zip = Path.Combine(tmp, "nova.zip");
        using (var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;
            await using var src = await res.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zip);
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }
        var extract = Path.Combine(tmp, "x");
        if (Directory.Exists(extract)) Directory.Delete(extract, true);
        ZipFile.ExtractToDirectory(zip, extract);
        var newExe = Directory.EnumerateFiles(extract, "Nova.exe", SearchOption.AllDirectories).First();

        // Çalışan exe silinemez ama yeniden adlandırılabilir.
        var old = Path.Combine(Path.GetDirectoryName(exe)!, "Nova.old.exe");
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        File.Copy(newExe, exe);
        return exe;
    }

    /// <summary>Önceki güncellemeden kalan eski exe'yi temizler.</summary>
    public static void CleanupOld()
    {
        try
        {
            var old = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "Nova.old.exe");
            if (File.Exists(old)) File.Delete(old);
        }
        catch { }
    }
}
