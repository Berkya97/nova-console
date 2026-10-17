using System.Diagnostics;
using Nova.Core;
using Nova.Views;

namespace Nova.Services;

/// <summary>
/// Masaüstü modu: oyun kolu fare ve klavye olur. Başka bir uygulamanın penceresinde (ör. Steam / EA
/// giriş penceresi) fiziksel klavye ve fare olmadan tıklamak, yazmak ve pencere değiştirmek için.
/// Sol çubuk imleç (ivmeli, LT basılıyken yavaş), sağ çubuk kaydırma, A sol tık (basılı tutunca sürükle),
/// B sağ tık, X ekran klavyesi, Y Enter, D-pad ok tuşları, LB / RB Alt+Tab. View + R3 ile açılıp kapanır.
/// </summary>
public sealed class DesktopMode
{
    readonly GamepadPoller _pad;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly double[] _repeatAt = new double[4];
    DesktopHint? _hint;
    SystemKeyboard? _kb;
    PadButtons _prev;
    double _last, _moving, _fracX, _fracY, _wheelX, _wheelY, _altUntil;
    bool _waitRelease, _left, _right, _alt;

    static readonly (PadButtons Button, ushort Vk)[] Arrows =
    {
        (PadButtons.Up, Injector.VK_UP), (PadButtons.Down, Injector.VK_DOWN),
        (PadButtons.Left, Injector.VK_LEFT), (PadButtons.Right, Injector.VK_RIGHT),
    };

    public bool Active { get; private set; }
    public bool KeyboardOpen => _kb?.IsVisible == true;
    public event Action<bool>? Changed;

    /// <summary>Test betiği için: ayarlıysa gerçek kol yerine bu durum kullanılır.</summary>
    public PadState? Simulated { get; set; }

    public DesktopMode(GamepadPoller pad)
    {
        _pad = pad;
        _pad.Raw += Update;
    }

    double Now => _clock.Elapsed.TotalSeconds;

    public void Toggle()
    {
        if (Active) Disable(); else Enable();
    }

    public void Enable()
    {
        if (Active) return;
        Active = true;
        _waitRelease = true;
        _hint ??= new DesktopHint();
        _hint.SetKeyboard(false);
        _hint.Show();
        Changed?.Invoke(true);
    }

    public void Disable()
    {
        if (!Active) return;
        CloseKeyboard();
        ReleaseHeld();
        _hint?.Hide();
        Active = false;
        Changed?.Invoke(false);
    }

    /// <summary>Basılı tutulan fare tuşlarını ve Alt'ı bırakır (kapanırken, uykuya girerken).</summary>
    public void ReleaseHeld()
    {
        if (_left) { Injector.LeftButton(false); _left = false; }
        if (_right) { Injector.RightButton(false); _right = false; }
        if (_alt) { Injector.KeyUp(Injector.VK_MENU); _alt = false; }
        _waitRelease = true;
    }

    public void ToggleKeyboard()
    {
        if (KeyboardOpen) CloseKeyboard(); else OpenKeyboard();
    }

    void OpenKeyboard()
    {
        if (!Active) Enable();
        if (_kb == null)
        {
            _kb = new SystemKeyboard();
            _kb.Keys.Done += CloseKeyboard;
        }
        ReleaseHeld();
        _kb.Open();
        _hint?.SetKeyboard(true);
        // Klavye açıkken yön tuşları ve A/B/X/Y her zamanki gibi (tekrarlı) eylem olarak gelsin.
        _pad.Global = true;
    }

    void CloseKeyboard()
    {
        if (_kb == null || !_kb.IsVisible) return;
        _kb.Hide();
        _hint?.SetKeyboard(false);
        _pad.Global = false;
        _waitRelease = true;
    }

    /// <summary>Klavye açıkken kolun ve telefonun eylemleri buraya gelir.</summary>
    public void Nav(NavAction a)
    {
        if (!Active || _kb == null || !KeyboardOpen) return;
        if (_kb.Keys.Handle(a)) { Sfx.Move(); return; }
        if (a == NavAction.Back) { Sfx.Back(); CloseKeyboard(); }
    }

    /// <summary>Telefondaki tuşlar kolun masaüstü modundaki karşılıklarıyla aynı işi yapar (dokununca bas-bırak).</summary>
    public void Remote(NavAction a)
    {
        if (!Active) return;
        if (KeyboardOpen) { Nav(a); return; }
        switch (a)
        {
            case NavAction.Confirm: Injector.LeftClick(); break;
            case NavAction.Back: Injector.RightClick(); break;
            case NavAction.Search: OpenKeyboard(); break;
            case NavAction.Options: Injector.Press(Injector.VK_RETURN); break;
            case NavAction.TabPrev: AltTab(back: true); break;
            case NavAction.TabNext: AltTab(back: false); break;
            case NavAction.Up: Injector.Press(Injector.VK_UP); break;
            case NavAction.Down: Injector.Press(Injector.VK_DOWN); break;
            case NavAction.Left: Injector.Press(Injector.VK_LEFT); break;
            case NavAction.Right: Injector.Press(Injector.VK_RIGHT); break;
            case NavAction.PageUp: Injector.Press(0x21); break;
            case NavAction.PageDown: Injector.Press(0x22); break;
        }
    }

    /// <summary>Alt basılı kalır, her basışta Tab gider; kısa bir duraksamadan sonra Alt bırakılır ve seçilen pencere öne gelir.</summary>
    public void AltTab(bool back)
    {
        if (!_alt) { Injector.KeyDown(Injector.VK_MENU); _alt = true; }
        if (back) Injector.Press(Injector.VK_TAB, Injector.VK_SHIFT);
        else Injector.Press(Injector.VK_TAB);
        _altUntil = Now + 0.8;
        Sfx.Move();
    }

    /// <summary>Radyal ölü bölge ve karesel eğri: küçük itişte hassas, tam itişte hızlı.</summary>
    static (double X, double Y) Curve(double x, double y, double dead)
    {
        double mag = Math.Sqrt(x * x + y * y);
        if (mag < dead) return (0, 0);
        double n = Math.Min(1, (mag - dead) / (1 - dead));
        return (x / mag * n * n, y / mag * n * n);
    }

    void Update(PadState real)
    {
        var s = Simulated ?? real;
        double now = Now, dt = Math.Clamp(now - _last, 0, 0.1);
        _last = now;
        if (!Active) { _prev = s.Buttons; return; }

        if (_alt && now >= _altUntil) { Injector.KeyUp(Injector.VK_MENU); _alt = false; }

        // Açılış / klavye kapanışı gibi geçişlerde basılı tuşlar bırakılana kadar yeni bir şey yapma.
        if (_waitRelease)
        {
            if (s.Buttons == PadButtons.None) _waitRelease = false;
            _prev = s.Buttons;
            return;
        }
        if (KeyboardOpen) { _prev = s.Buttons; return; }

        // İmleç: basılı tuttukça hızlanır.
        var (mx, my) = Curve(s.LX, s.LY, 0.2);
        _moving = mx != 0 || my != 0 ? _moving + dt : 0;
        double speed = Injector.ScreenHeight * 1.4 * (0.5 + 0.5 * Math.Min(1, _moving / 0.4)) * (s.Has(PadButtons.LT) ? 0.3 : 1);
        _fracX += mx * speed * dt;
        _fracY -= my * speed * dt;
        int ix = (int)_fracX, iy = (int)_fracY;
        if (ix != 0 || iy != 0)
        {
            Injector.MoveBy(ix, iy);
            _fracX -= ix;
            _fracY -= iy;
        }

        // Kaydırma: tam itişte saniyede ~12 çentik.
        var (sx, sy) = Curve(s.RX, s.RY, 0.25);
        _wheelX = sx == 0 ? 0 : _wheelX + sx * 1440 * dt;
        _wheelY = sy == 0 ? 0 : _wheelY + sy * 1440 * dt;
        int wx = (int)(_wheelX / 30) * 30, wy = (int)(_wheelY / 30) * 30;
        if (wx != 0 || wy != 0)
        {
            Injector.Wheel(wy, wx);
            _wheelX -= wx;
            _wheelY -= wy;
        }

        bool Pressed(PadButtons b) => s.Has(b) && (_prev & b) == 0;
        bool Released(PadButtons b) => !s.Has(b) && (_prev & b) != 0;

        if (Pressed(PadButtons.A)) { Injector.LeftButton(true); _left = true; }
        else if (Released(PadButtons.A) && _left) { Injector.LeftButton(false); _left = false; }
        if (Pressed(PadButtons.B)) { Injector.RightButton(true); _right = true; }
        else if (Released(PadButtons.B) && _right) { Injector.RightButton(false); _right = false; }
        if (Pressed(PadButtons.Y)) Injector.Press(Injector.VK_RETURN);
        if (Pressed(PadButtons.LB)) AltTab(back: true);
        if (Pressed(PadButtons.RB)) AltTab(back: false);

        for (int i = 0; i < Arrows.Length; i++)
        {
            var (button, vk) = Arrows[i];
            if (Pressed(button)) { Injector.Press(vk); _repeatAt[i] = now + 0.38; }
            else if (s.Has(button) && now >= _repeatAt[i]) { Injector.Press(vk); _repeatAt[i] = now + 0.095; }
        }

        // Klavye en son açılır: aynı karede X bir de eylem olarak gitmesin diye poller Raw'ı eylemlerden sonra çağırır.
        bool keyboard = Pressed(PadButtons.X);
        _prev = s.Buttons;
        if (keyboard) { Sfx.Confirm(); OpenKeyboard(); }
    }
}
