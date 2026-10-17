using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Nova.Core;
using Windows.Gaming.Input;

namespace Nova.Services;

/// <summary>
/// Nova kapatıldıktan sonra arka planda kalan, penceresiz küçük dinleyici (Nova.exe --watcher).
/// Ctrl+Alt+N'ye ya da oyun kolunda Start+View (PS: Options+Create) kombinasyonuna basılınca
/// Nova'yı başlatır ve kendini kapatır. Nova açılınca da kendiliğinden kapanır.
/// </summary>
public static class Watcher
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [DllImport("xinput1_4.dll")] static extern int XInputGetState(int index, out XINPUT_STATE state);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    static readonly string Suffix = Environment.UserName.GetHashCode().ToString("x");
    static string QuitEventName => "Nova.WatcherQuit." + Suffix;

    static Mutex? _mutex;
    static HwndSource? _source;
    static bool _launched;

    /// <summary>Nova kapanırken çağrılır: dinleyiciyi ayrı bir süreç olarak başlatır.</summary>
    public static void Spawn()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--watcher") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e) { App.Log(e); }
    }

    /// <summary>Nova açılırken çağrılır: çalışan bir dinleyici varsa kapanmasını söyler.</summary>
    public static void StopRunning()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out var ev))
                using (ev) ev.Set();
        }
        catch { }
    }

    /// <summary>--watcher argümanıyla açılan süreçte çalışır.</summary>
    public static void Run(Application app)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _mutex = new Mutex(true, "Nova.Watcher." + Suffix, out bool first);
        if (!first) { app.Shutdown(); return; }

        // Nova açılınca bu olay tetiklenir ve dinleyici kapanır.
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        new Thread(() =>
        {
            quit.WaitOne();
            app.Dispatcher.BeginInvoke(() => Exit(app));
        }) { IsBackground = true }.Start();

        // Kısayol için yalnızca mesaj alan görünmez pencere.
        _source = new HwndSource(new HwndSourceParameters("NovaWatcher") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        _source.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == Native.WM_HOTKEY) { Launch(app); handled = true; }
            return IntPtr.Zero;
        });
        Native.RegisterHotKey(_source.Handle, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 'N');

        // Oyun kolu: XInput arka planda da okunabilir. PS kolları için Windows.Gaming.Input da denenir.
        var sony = new List<RawGameController>();
        try
        {
            foreach (var c in RawGameController.RawGameControllers) if (c.HardwareVendorId == 0x054C) sony.Add(c);
            RawGameController.RawGameControllerAdded += (_, c) => { if (c.HardwareVendorId == 0x054C) lock (sony) sony.Add(c); };
        }
        catch { }

        var connected = new bool[4];
        var nextScan = DateTime.MinValue;
        bool held = true; // açılırken basılı olan bir kombinasyonu yok say
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            bool combo = false;
            var now = DateTime.UtcNow;
            bool scan = now >= nextScan;
            if (scan) nextScan = now.AddSeconds(2);
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    if (!connected[i] && !scan) continue;
                    if (XInputGetState(i, out var s) != 0) { connected[i] = false; continue; }
                    connected[i] = true;
                    if ((s.wButtons & 0x0010) != 0 && (s.wButtons & 0x0020) != 0) combo = true;
                }
            }
            catch (DllNotFoundException) { }
            lock (sony)
            {
                foreach (var c in sony)
                {
                    try
                    {
                        var b = new bool[c.ButtonCount];
                        c.GetCurrentReading(b, new GameControllerSwitchPosition[c.SwitchCount], new double[c.AxisCount]);
                        bool options = b.Length > 9 && b[9];
                        bool create = (b.Length > 8 && b[8]) || (b.Length > 12 && b[12]) || (b.Length > 13 && b[13]);
                        if (options && create) combo = true;
                    }
                    catch { }
                }
            }
            if (combo && !held) Launch(app);
            held = combo;
        };
        timer.Start();

        // Dinleyici boşta bekler: başlangıçta yüklenen belleği Windows'a geri ver.
        var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        trim.Tick += (_, _) =>
        {
            trim.Stop();
            GC.Collect();
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        };
        trim.Start();
    }

    static void Launch(Application app)
    {
        if (_launched) return;
        _launched = true;
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); }
        catch (Exception e) { App.Log(e); }
        Exit(app);
    }

    static void Exit(Application app)
    {
        if (_source != null) Native.UnregisterHotKey(_source.Handle, 1);
        _source?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        app.Shutdown();
    }
}
