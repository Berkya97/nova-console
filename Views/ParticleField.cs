using System.Windows;
using System.Windows.Media;

namespace Nova.Views;

/// <summary>Arka planda yavaşça yükselen, parıldayan ışık zerrecikleri.</summary>
public sealed class ParticleField : FrameworkElement
{
    struct Particle
    {
        public double X, Y, R, Speed, Phase, Twinkle, Sway, Alpha;
    }

    static readonly Brush Dot = MakeDot();

    readonly Particle[] _ps = new Particle[80];
    readonly Random _rng = new(7);
    TimeSpan _last;
    double _t;
    bool _running;

    public ParticleField()
    {
        IsHitTestVisible = false;
        for (int i = 0; i < _ps.Length; i++) _ps[i] = Spawn(randomY: true);
        Loaded += (_, _) => Resume();
        Unloaded += (_, _) => Pause();
    }

    public void Resume()
    {
        if (_running) return;
        _running = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Pause()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    Particle Spawn(bool randomY)
    {
        double w = ActualWidth > 0 ? ActualWidth : 1920, h = ActualHeight > 0 ? ActualHeight : 1080;
        double depth = _rng.NextDouble();
        return new Particle
        {
            X = _rng.NextDouble() * w,
            Y = randomY ? _rng.NextDouble() * h : h + 10,
            R = 1.2 + depth * 3.2,
            Speed = 6 + depth * 20,
            Phase = _rng.NextDouble() * Math.PI * 2,
            Twinkle = 0.6 + _rng.NextDouble() * 1.6,
            Sway = 6 + _rng.NextDouble() * 18,
            Alpha = 0.12 + depth * 0.45,
        };
    }

    void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _last) return;
        double dt = _last == TimeSpan.Zero ? 0 : Math.Min(0.1, (now - _last).TotalSeconds);
        _last = now;
        _t += dt;
        for (int i = 0; i < _ps.Length; i++)
        {
            _ps[i].Y -= _ps[i].Speed * dt;
            if (_ps[i].Y < -10) _ps[i] = Spawn(randomY: false);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var p in _ps)
        {
            double a = p.Alpha * (0.55 + 0.45 * Math.Sin(_t * p.Twinkle + p.Phase));
            double x = p.X + Math.Sin(_t * 0.3 + p.Phase) * p.Sway;
            dc.PushOpacity(a);
            dc.DrawEllipse(Dot, null, new Point(x, p.Y), p.R * 2.2, p.R * 2.2);
            dc.Pop();
        }
    }

    static Brush MakeDot()
    {
        var b = new RadialGradientBrush();
        b.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(150, 200, 225, 255), 0.35));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 160, 200, 255), 1));
        b.Freeze();
        return b;
    }
}
