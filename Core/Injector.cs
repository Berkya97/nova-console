using System.Runtime.InteropServices;

namespace Nova.Core;

/// <summary>
/// SendInput ile öndeki pencereye sahte fare ve klavye girdisi gönderir (masaüstü modu, sistem klavyesi,
/// telefonla yazma). Yönetici olarak çalışan pencerelere ve UAC ekranına Windows izin vermez.
/// </summary>
public static class Injector
{
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_WHEEL = 0x800,
        MOUSEEVENTF_HWHEEL = 0x1000, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    public const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_MENU = 0x12,
        VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;

    static readonly int Size = Marshal.SizeOf<INPUT>();

    static void Send(params INPUT[] inputs)
    {
        if (inputs.Length > 0) SendInput((uint)inputs.Length, inputs, Size);
    }

    static INPUT Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) =>
        new() { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };

    static INPUT Key(ushort vk, ushort scan, uint flags) =>
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };

    // ───── Fare ─────

    /// <summary>Birincil ekranın yüksekliği (fiziksel piksel); imleç hızı buna göre ölçeklenir.</summary>
    public static int ScreenHeight => Math.Max(480, GetSystemMetrics(1));

    /// <summary>İmleci göreli taşır. Windows'un fare hızlandırması karışmasın diye mutlak konumla gönderilir.</summary>
    public static void MoveBy(double dx, double dy)
    {
        if (!GetCursorPos(out var p)) return;
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        if (vw <= 1 || vh <= 1) return;
        int x = Math.Clamp(p.X + (int)Math.Round(dx), vx, vx + vw - 1);
        int y = Math.Clamp(p.Y + (int)Math.Round(dy), vy, vy + vh - 1);
        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny));
    }

    public static void LeftButton(bool down) => Send(Mouse(down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP));
    public static void RightButton(bool down) => Send(Mouse(down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP));
    public static void LeftClick() => Send(Mouse(MOUSEEVENTF_LEFTDOWN), Mouse(MOUSEEVENTF_LEFTUP));
    public static void RightClick() => Send(Mouse(MOUSEEVENTF_RIGHTDOWN), Mouse(MOUSEEVENTF_RIGHTUP));

    /// <summary>Tekerlek: 120 bir çentik. Pozitif = yukarı / sağa.</summary>
    public static void Wheel(int vertical, int horizontal = 0)
    {
        var list = new List<INPUT>(2);
        if (vertical != 0) list.Add(Mouse(MOUSEEVENTF_WHEEL, data: vertical));
        if (horizontal != 0) list.Add(Mouse(MOUSEEVENTF_HWHEEL, data: horizontal));
        Send(list.ToArray());
    }

    // ───── Klavye ─────

    static bool Extended(ushort vk) => vk is >= 0x21 and <= 0x2E; // ok tuşları, Home/End, PgUp/PgDn, Ins/Del

    public static void KeyDown(ushort vk) => Send(Key(vk, 0, Extended(vk) ? KEYEVENTF_EXTENDEDKEY : 0));
    public static void KeyUp(ushort vk) => Send(Key(vk, 0, KEYEVENTF_KEYUP | (Extended(vk) ? KEYEVENTF_EXTENDEDKEY : 0)));

    /// <summary>Tuşa bas ve bırak; isteğe bağlı olarak Shift / Alt gibi değiştiricilerle.</summary>
    public static void Press(ushort vk, params ushort[] modifiers)
    {
        var list = new List<INPUT>();
        uint ext = Extended(vk) ? KEYEVENTF_EXTENDEDKEY : 0;
        foreach (var m in modifiers) list.Add(Key(m, 0, 0));
        list.Add(Key(vk, 0, ext));
        list.Add(Key(vk, 0, ext | KEYEVENTF_KEYUP));
        foreach (var m in modifiers.Reverse()) list.Add(Key(m, 0, KEYEVENTF_KEYUP));
        Send(list.ToArray());
    }

    /// <summary>Metni klavye düzeninden bağımsız olarak Unicode karakter olarak yazar.</summary>
    public static void Type(string text)
    {
        var list = new List<INPUT>(text.Length * 2);
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { list.Add(Key(VK_RETURN, 0, 0)); list.Add(Key(VK_RETURN, 0, KEYEVENTF_KEYUP)); continue; }
            if (ch == '\t') { list.Add(Key(VK_TAB, 0, 0)); list.Add(Key(VK_TAB, 0, KEYEVENTF_KEYUP)); continue; }
            list.Add(Key(0, ch, KEYEVENTF_UNICODE));
            list.Add(Key(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        Send(list.ToArray());
    }
}
