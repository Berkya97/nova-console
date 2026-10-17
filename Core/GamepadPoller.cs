using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Windows.Gaming.Input;

namespace Nova.Core;

/// <summary>
/// Oyun kollarını 60 Hz'de okur ve NavAction'a çevirir. Xbox / XInput kolları XInput ile,
/// PlayStation kolları (DualShock 4, DualSense) Windows.Gaming.Input ham okumasıyla desteklenir.
/// Yön tuşları basılı tutulunca tekrarlar. Nova arka plandayken yalnızca "Nova'ya dön" kombinasyonu dinlenir.
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

    const int StickThreshold = 16000;
    const ushort SonyVendor = 0x054C;
    static readonly TimeSpan FirstRepeat = TimeSpan.FromMilliseconds(380);
    static readonly TimeSpan NextRepeat = TimeSpan.FromMilliseconds(95);

    public event Action<NavAction>? Action;
    /// <summary>Start + Back (Options + Create) birlikte basıldı: Nova'yı öne getir.</summary>
    public event Action? ReturnRequested;

    readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    readonly bool[] _connected = new bool[4];
    readonly bool[] _prev = new bool[(int)Vb.Count];
    readonly DateTime[] _repeatAt = new DateTime[(int)Vb.Count];
    readonly List<RawGameController> _sony = new();
    DateTime _nextScan;
    bool _xinputAvailable = true, _comboHeld, _suppressUntilRelease;

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

    void Tick()
    {
        var now = DateTime.UtcNow;
        bool scan = now >= _nextScan;
        if (scan) _nextScan = now.AddSeconds(1.5);

        var cur = new bool[(int)Vb.Count];
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
                    Merge(st.Gamepad, cur);
                }
            }
            catch (DllNotFoundException) { _xinputAvailable = false; }
        }
        // DS4Windows gibi araçlar PS kolunu zaten XInput'a çeviriyorsa çift girdi olmasın.
        if (!anyXinput) foreach (var c in _sony) MergeSony(c, cur);

        // Nova'ya dön kombinasyonu her zaman dinlenir.
        bool combo = cur[(int)Vb.Start] && cur[(int)Vb.Back];
        if (combo && !_comboHeld)
        {
            _suppressUntilRelease = true;
            ReturnRequested?.Invoke();
        }
        _comboHeld = combo;

        // Nova'nın herhangi bir penceresi (ana pencere ya da oyun içi menü) öndeyse kol girdisi işlenir.
        bool active = Application.Current?.Windows.OfType<Window>().Any(w => w.IsActive) == true;
        if (!active || _suppressUntilRelease)
        {
            if (!cur.Any(b => b)) _suppressUntilRelease = false;
            Array.Copy(cur, _prev, cur.Length);
            return;
        }

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

    void Fire(int b)
    {
        if (Map[b] is { } a) Action?.Invoke(a);
    }

    static void Merge(XINPUT_GAMEPAD g, bool[] v)
    {
        ushort w = g.wButtons;
        v[(int)Vb.Up] |= (w & 0x0001) != 0 || g.sThumbLY > StickThreshold;
        v[(int)Vb.Down] |= (w & 0x0002) != 0 || g.sThumbLY < -StickThreshold;
        v[(int)Vb.Left] |= (w & 0x0004) != 0 || g.sThumbLX < -StickThreshold;
        v[(int)Vb.Right] |= (w & 0x0008) != 0 || g.sThumbLX > StickThreshold;
        v[(int)Vb.Start] |= (w & 0x0010) != 0;
        v[(int)Vb.Back] |= (w & 0x0020) != 0;
        v[(int)Vb.LB] |= (w & 0x0100) != 0;
        v[(int)Vb.RB] |= (w & 0x0200) != 0;
        v[(int)Vb.A] |= (w & 0x1000) != 0;
        v[(int)Vb.B] |= (w & 0x2000) != 0;
        v[(int)Vb.X] |= (w & 0x4000) != 0;
        v[(int)Vb.Y] |= (w & 0x8000) != 0;
        v[(int)Vb.LT] |= g.bLeftTrigger > 120;
        v[(int)Vb.RT] |= g.bRightTrigger > 120;
    }

    /// <summary>
    /// DualShock 4 / DualSense DirectInput düzeni: 0 Kare, 1 Çarpı, 2 Daire, 3 Üçgen, 4 L1, 5 R1, 6 L2, 7 R2,
    /// 8 Share/Create, 9 Options, 10 L3, 11 R3, 12 PS, 13 Dokunmatik yüzey. Eksen 0/1 sol çubuk (0..1).
    /// </summary>
    static void MergeSony(RawGameController c, bool[] v)
    {
        try
        {
            var buttons = new bool[c.ButtonCount];
            var switches = new GameControllerSwitchPosition[c.SwitchCount];
            var axes = new double[c.AxisCount];
            c.GetCurrentReading(buttons, switches, axes);
            bool B(int i) => i < buttons.Length && buttons[i];

            v[(int)Vb.X] |= B(0);
            v[(int)Vb.A] |= B(1);
            v[(int)Vb.B] |= B(2);
            v[(int)Vb.Y] |= B(3);
            v[(int)Vb.LB] |= B(4);
            v[(int)Vb.RB] |= B(5);
            v[(int)Vb.LT] |= B(6);
            v[(int)Vb.RT] |= B(7);
            v[(int)Vb.Back] |= B(8) || B(12) || B(13);
            v[(int)Vb.Start] |= B(9);

            if (switches.Length > 0)
            {
                var s = switches[0];
                v[(int)Vb.Up] |= s is GameControllerSwitchPosition.Up or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.UpRight;
                v[(int)Vb.Down] |= s is GameControllerSwitchPosition.Down or GameControllerSwitchPosition.DownLeft or GameControllerSwitchPosition.DownRight;
                v[(int)Vb.Left] |= s is GameControllerSwitchPosition.Left or GameControllerSwitchPosition.UpLeft or GameControllerSwitchPosition.DownLeft;
                v[(int)Vb.Right] |= s is GameControllerSwitchPosition.Right or GameControllerSwitchPosition.UpRight or GameControllerSwitchPosition.DownRight;
            }
            if (axes.Length >= 2)
            {
                v[(int)Vb.Left] |= axes[0] < 0.25;
                v[(int)Vb.Right] |= axes[0] > 0.75;
                v[(int)Vb.Up] |= axes[1] < 0.25;
                v[(int)Vb.Down] |= axes[1] > 0.75;
            }
        }
        catch { }
    }
}
