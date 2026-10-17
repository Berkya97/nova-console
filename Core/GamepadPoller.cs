using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Windows.Gaming.Input;

namespace Nova.Core;

/// <summary>Bütün kolların birleşik anlık durumu (masaüstü modu için). Çubuklar -1..1, yukarı / sağ pozitif.</summary>
[Flags]
public enum PadButtons
{
    None = 0, Up = 1, Down = 2, Left = 4, Right = 8, A = 16, B = 32, X = 64, Y = 128,
    LB = 256, RB = 512, LT = 1024, RT = 2048, Start = 4096, Back = 8192, L3 = 16384, R3 = 32768,
}

public readonly record struct PadState(PadButtons Buttons, double LX, double LY, double RX, double RY)
{
    public bool Has(PadButtons b) => (Buttons & b) == b;
}

/// <summary>
/// Oyun kollarını 60 Hz'de okur ve NavAction'a çevirir. Xbox / XInput kolları XInput ile,
/// PlayStation kolları (DualShock 4, DualSense) Windows.Gaming.Input ham okumasıyla desteklenir.
/// Yön tuşları basılı tutulunca tekrarlar. Nova arka plandayken yalnızca kombinasyonlar dinlenir
/// (Global açıkken, ör. masaüstü modunda ekran klavyesi açıkken, eylemler her zaman gelir).
/// </summary>
public sealed class GamepadPoller
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    static extern int XInputGetState(int index, out XINPUT_STATE state);

    // Sanal tuşlar: Xbox düzenine göre adlandırıldı (PlayStation'da A=Çarpı, B=Daire, X=Kare, Y=Üçgen).
    enum Vb { Up, Down, Left, Right, A, B, X, Y, LB, RB, LT, RT, Start, Back, Count }

    static readonly NavAction?[] Map =
    {
        NavAction.Up, NavAction.Down, NavAction.Left, NavAction.Right,
        NavAction.Confirm, NavAction.Back, NavAction.Search, NavAction.Options,
        NavAction.TabPrev, NavAction.TabNext, NavAction.PageUp, NavAction.PageDown,
        NavAction.Menu, NavAction.Control,
    };

    static readonly PadButtons[] Bits =
    {
        PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right,
        PadButtons.A, PadButtons.B, PadButtons.X, PadButtons.Y,
        PadButtons.LB, PadButtons.RB, PadButtons.LT, PadButtons.RT,
        PadButtons.Start, PadButtons.Back,
    };

    const double StickThreshold = 0.49;
    const ushort SonyVendor = 0x054C;
    static readonly TimeSpan FirstRepeat = TimeSpan.FromMilliseconds(380);
    static readonly TimeSpan NextRepeat = TimeSpan.FromMilliseconds(95);

    public event Action<NavAction>? Action;
    /// <summary>Start + Back (Options + Create) birlikte basıldı: Nova'yı öne getir.</summary>
    public event Action? ReturnRequested;
    /// <summary>Back + R3 (Create + R3) birlikte basıldı: masaüstü modunu aç / kapat.</summary>
    public event Action? DesktopToggleRequested;
    /// <summary>Her karede bütün kolların birleşik durumu.</summary>
    public event Action<PadState>? Raw;

    /// <summary>Açıkken eylemler Nova arka plandayken de gönderilir.</summary>
    public bool Global { get; set; }

    readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    readonly bool[] _connected = new bool[4];
    readonly bool[] _prev = new bool[(int)Vb.Count];
    readonly DateTime[] _repeatAt = new DateTime[(int)Vb.Count];
    readonly List<RawGameController> _sony = new();
    DateTime _nextScan, _lastTick;
    bool _xinputAvailable = true, _comboHeld, _desktopHeld, _suppressUntilRelease;

    public GamepadPoller()
    {
        _timer.Tick += (_, _) => Tick();
        try
        {
            RawGameController.RawGameControllerAdded += (_, c) => Application.Current?.Dispatcher.BeginInvoke(() => AddRaw(c));
            RawGameController.RawGameControllerRemoved += (_, c) => Application.Current?.Dispatcher.BeginInvoke(() => _sony.Remove(c));
            foreach (var c in RawGameController.RawGameControllers) AddRaw(c);
        }
        catch { /* Windows.Gaming.Input yoksa yalnızca XInput */ }
    }

    void AddRaw(RawGameController c)
    {
        if (c.HardwareVendorId == SonyVendor && !_sony.Contains(c)) _sony.Add(c);
    }

    public void Start() => _timer.Start();

    /// <summary>
    /// Kolları baştan arar (uykudan dönüşte). Bağlı sanılan XInput yuvaları ve PlayStation kol listesi
    /// sıfırlanır; uykuya girerken basılı kalmış görünen tuşlar da bırakılana kadar yok sayılır.
    /// </summary>
    public void Rescan()
    {
        Array.Clear(_connected);
        _nextScan = DateTime.MinValue;
        _xinputAvailable = true;
        _suppressUntilRelease = true;
        try
        {
            _sony.Clear();
            foreach (var c in RawGameController.RawGameControllers) AddRaw(c);
        }
        catch { }
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        // Zamanlayıcı uzun süre çalışmadıysa bilgisayar uykudan dönmüştür (modern bekleme dahil).
        if (_lastTick != default && now - _lastTick > TimeSpan.FromSeconds(5)) Rescan();
        _lastTick = now;
        bool scan = now >= _nextScan;
        if (scan) _nextScan = now.AddSeconds(1.5);

        var pad = new Reading();
        bool anyXinput = false;
        if (_xinputAvailable)
        {
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    // Bağlı olmayan kolları her karede sorgulamak pahalı, ara ara tara.
                    if (!_connected[i] && !scan) continue;
                    if (XInputGetState(i, out var st) != 0) { _connected[i] = false; continue; }
                    _connected[i] = anyXinput = true;
                    Merge(st.Gamepad, ref pad);
                }
            }
            catch (DllNotFoundException) { _xinputAvailable = false; }
        }
        // DS4Windows gibi araçlar PS kolunu zaten XInput'a çeviriyorsa çift girdi olmasın.
        if (!anyXinput) foreach (var c in _sony) MergeSony(c, ref pad);
        var state = pad.ToState();

        var cur = new bool[(int)Vb.Count];
        for (int b = 0; b < cur.Length; b++) cur[b] = state.Has(Bits[b]);
        cur[(int)Vb.Up] |= state.LY > StickThreshold;
        cur[(int)Vb.Down] |= state.LY < -StickThreshold;
        cur[(int)Vb.Left] |= state.LX < -StickThreshold;
        cur[(int)Vb.Right] |= state.LX > StickThreshold;

        // Nova'ya dön kombinasyonu her zaman dinlenir.
        bool combo = state.Has(PadButtons.Start | PadButtons.Back);
        if (combo && !_comboHeld)
        {
            _suppressUntilRelease = true;
            ReturnRequested?.Invoke();
        }
        _comboHeld = combo;

        bool desktop = state.Has(PadButtons.Back | PadButtons.R3);
        if (desktop && !_desktopHeld)
        {
            _suppressUntilRelease = true;
            DesktopToggleRequested?.Invoke();
        }
        _desktopHeld = desktop;

        // Nova'nın herhangi bir penceresi (ana pencere ya da oyun içi menü) öndeyse kol girdisi işlenir.
        bool active = Global || Application.Current?.Windows.OfType<Window>().Any(w => w.IsActive) == true;
        if (!active || _suppressUntilRelease)
        {
            if (state.Buttons == PadButtons.None && !cur.Any(b => b)) _suppressUntilRelease = false;
            Array.Copy(cur, _prev, cur.Length);
        }
        else
        {
            for (int b = 0; b < cur.Length; b++)
            {
                bool directional = b <= (int)Vb.Right;
                if (cur[b] && !_prev[b])
                {
                    Fire(b);
                    _repeatAt[b] = now + FirstRepeat;
                }
                else if (cur[b] && directional && now >= _repeatAt[b])
                {
                    Fire(b);
                    _repeatAt[b] = now + NextRepeat;
                }
                _prev[b] = cur[b];
            }
        }

        // Eylemlerden sonra: masaüstü modu bu karede klavyeyi açarsa aynı tuş bir de eylem olarak gitmesin.
        Raw?.Invoke(state);
    }

    void Fire(int b)
    {
        if (Map[b] is { } a) Action?.Invoke(a);
    }

    /// <summary>Birden fazla kol bağlıysa tuşlar birleşir, çubuklarda en çok itilen kazanır.</summary>
    struct Reading
    {
        public PadButtons Buttons;
        public double LX, LY, RX, RY;

        public void Stick(double lx, double ly, double rx, double ry)
        {
            LX = Pick(LX, lx); LY = Pick(LY, ly); RX = Pick(RX, rx); RY = Pick(RY, ry);
        }

        static double Pick(double a, double b) => Math.Abs(b) > Math.Abs(a) ? Math.Clamp(b, -1, 1) : a;

        public void Set(PadButtons b, bool on) { if (on) Buttons |= b; }

        public PadState ToState() => new(Buttons, LX, LY, RX, RY);
    }

    static void Merge(XINPUT_GAMEPAD g, ref Reading r)
    {
        ushort w = g.wButtons;
        r.Set(PadButtons.Up, (w & 0x0001) != 0);
        r.Set(PadButtons.Down, (w & 0x0002) != 0);
        r.Set(PadButtons.Left, (w & 0x0004) != 0);
        r.Set(PadButtons.Right, (w & 0x0008) != 0);
        r.Set(PadButtons.Start, (w & 0x0010) != 0);
        r.Set(PadButtons.Back, (w & 0x0020) != 0);
        r.Set(PadButtons.L3, (w & 0x0040) != 0);
        r.Set(PadButtons.R3, (w & 0x0080) != 0);
        r.Set(PadButtons.LB, (w & 0x0100) != 0);
        r.Set(PadButtons.RB, (w & 0x0200) != 0);
        r.Set(PadButtons.A, (w & 0x1000) != 0);
        r.Set(PadButtons.B, (w & 0x2000) != 0);
        r.Set(PadButtons.X, (w & 0x4000) != 0);
        r.Set(PadButtons.Y, (w & 0x8000) != 0);
        r.Set(PadButtons.LT, g.bLeftTrigger > 120);
        r.Set(PadButtons.RT, g.bRightTrigger > 120);
        r.Stick(g.sThumbLX / 32767.0, g.sThumbLY / 32767.0, g.sThumbRX / 32767.0, g.sThumbRY / 32767.0);
    }

    /// <summary>
    /// DualShock 4 / DualSense DirectInput düzeni: 0 Kare, 1 Çarpı, 2 Daire, 3 Üçgen, 4 L1, 5 R1, 6 L2, 7 R2,
    /// 8 Share/Create, 9 Options, 10 L3, 11 R3, 12 PS, 13 Dokunmatik yüzey. Eksenler (0..1): 0/1 sol çubuk,
    /// 2 sağ çubuk yatay, 3/4 L2/R2, 5 sağ çubuk dikey.
    /// </summary>
    static void MergeSony(RawGameController c, ref Reading r)
    {
        try
        {
            var buttons = new bool[c.ButtonCount];
            var switches = new GameControllerSwitchPosition[c.SwitchCount];
            var axes = new double[c.AxisCount];
            c.GetCurrentReading(buttons, switches, axes);
            bool B(int i) => i < buttons.Length && buttons[i];

            r.Set(PadButtons.X, B(0));
            r.Set(PadButtons.A, B(1));
            r.Set(PadButtons.B, B(2));
            r.Set(PadButtons.Y, B(3));
            r.Set(PadButtons.LB, B(4));
            r.Set(PadButtons.RB, B(5));
            r.Set(PadButtons.LT, B(6));
            r.Set(PadButtons.RT, B(7));
            r.Set(PadButtons.Back, B(8) || B(12) || B(13));
            r.Set(PadButtons.Start, B(9));
            r.Set(PadButtons.L3, B(10));
            r.Set(PadButtons.R3, B(11));

            if (switches.Length > 0)
            {
                var s = switches[0];
                r.Set(PadButtons.Up, s is GameControllerSwitchPosition.Up or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.UpRight);
                r.Set(PadButtons.Down, s is GameControllerSwitchPosition.Down or GameControllerSwitchPosition.DownLeft or GameControllerSwitchPosition.DownRight);
                r.Set(PadButtons.Left, s is GameControllerSwitchPosition.Left or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.DownLeft);
                r.Set(PadButtons.Right, s is GameControllerSwitchPosition.Right or GameControllerSwitchPosition.UpRight or GameControllerSwitchPosition.DownRight);
            }
            double Axis(int i, bool invert) => i < axes.Length ? (axes[i] - 0.5) * 2 * (invert ? -1 : 1) : 0;
            r.Stick(Axis(0, false), Axis(1, true), axes.Length >= 6 ? Axis(2, false) : 0, axes.Length >= 6 ? Axis(5, true) : 0);
        }
        catch { }
    }
}
