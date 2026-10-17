using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Nova.Core;

/// <summary>Win32 yardımcıları: kısayol tuşu, pencereyi öne getirme, pil, süreç yolları, dosya işlemleri.</summary>
public static class Native
{
    public const int WM_HOTKEY = 0x0312, WM_DEVICECHANGE = 0x0219;
    public const int DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVTYP_VOLUME = 2;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>Başka bir uygulama (ör. oyun) öndeyken Nova'yı öne getirir.</summary>
    public static void ForceForeground(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
        var fg = GetForegroundWindow();
        if (fg == h) return;
        uint me = GetCurrentThreadId(), other = GetWindowThreadProcessId(fg, IntPtr.Zero);
        bool attached = other != me && AttachThreadInput(me, other, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, other, false);
        if (GetForegroundWindow() != h)
        {
            // Son çare: Alt tuşu basılıymış gibi davranmak ön plan kilidini açar.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            SetForegroundWindow(h);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    /// <returns>Pil yoksa null.</returns>
    public static (int Percent, bool Charging)? Battery()
    {
        if (!GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryLifePercent == 255) return null;
        return (s.BatteryLifePercent, s.ACLineStatus == 1);
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);

    /// <summary>Çalışan tüm süreçlerin exe yolları (yetki gerektirmeyen yöntemle).</summary>
    public static List<string> RunningImagePaths()
    {
        var list = new List<string>();
        var sb = new StringBuilder(1024);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, p.Id);
                if (h == IntPtr.Zero) continue;
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) list.Add(sb.ToString(0, size));
                CloseHandle(h);
            }
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public enum FileOp : uint { Move = 1, Copy = 2, Delete = 3 }

    /// <summary>Windows'un kendi ilerleme penceresiyle kopyalama/taşıma/geri dönüşüm kutusuna gönderme.</summary>
    public static bool ShellFileOperation(IntPtr owner, FileOp op, IEnumerable<string> sources, string? target)
    {
        const ushort FOF_RENAMEONCOLLISION = 0x8, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMMKDIR = 0x200;
        var s = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = (uint)op,
            pFrom = string.Join("\0", sources) + "\0\0",
            pTo = target == null ? null : target + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | (op == FileOp.Delete ? FOF_NOCONFIRMATION : FOF_RENAMEONCOLLISION)),
        };
        return SHFileOperation(ref s) == 0 && !s.fAnyOperationsAborted;
    }
}
