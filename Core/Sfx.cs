using System.Media;

namespace Nova.Core;

/// <summary>
/// Arayüz sesleri. Harici ses dosyası yerine açılışta kısa dalga formları sentezlenir.
/// </summary>
public static class Sfx
{
    const int Rate = 44100;
    static SoundPlayer? _move, _confirm, _back, _tab, _boot, _bump;

    public static bool Enabled { get; set; } = true;

    public static void Init()
    {
        try
        {
            _move = Make(0.07, t => Sine(1500, t) * Decay(t, 85) * 0.16 + Sine(3000, t) * Decay(t, 160) * 0.04);
            _tab = Make(0.10, t => Sine(1100 + 300 * t, t) * Decay(t, 45) * 0.14);
            _confirm = Make(0.30, t =>
                Sine(1046.5, t) * Window(t, 0, 0.12) * Decay(t, 22) * 0.18 +
                Sine(1568, t) * Window(t, 0.06, 0.3) * Decay(t - 0.06, 14) * 0.18);
            _back = Make(0.22, t =>
                Sine(880, t) * Window(t, 0, 0.09) * Decay(t, 30) * 0.15 +
                Sine(587.3, t) * Window(t, 0.06, 0.22) * Decay(t - 0.06, 20) * 0.15);
            _bump = Make(0.09, t => Sine(220, t) * Decay(t, 50) * 0.18);
            _boot = Make(2.6, BootTone);
        }
        catch { /* ses aygıtı yoksa sessiz devam */ }
    }

    public static void Move() => Play(_move);
    public static void Tab() => Play(_tab);
    public static void Confirm() => Play(_confirm);
    public static void Back() => Play(_back);
    public static void Bump() => Play(_bump);
    public static void Boot() => Play(_boot);

    static void Play(SoundPlayer? p)
    {
        if (!Enabled || p == null) return;
        try { p.Play(); } catch { }
        if (_recording != null && Samples.TryGetValue(p, out var s)) _recording.Add((Elapsed, s));
    }

    // ───── Tanıtım videosu kaydı: çalınan seslerin zamanlarını tutup tek bir ses izi olarak dışa aktarır ─────

    static readonly Dictionary<SoundPlayer, short[]> Samples = new();
    static List<(double Time, short[] Data)>? _recording;
    static readonly System.Diagnostics.Stopwatch Clock = new();

    /// <summary>Kaydın başladığı an (Unix, mikrosaniye): video karelerinin zaman damgasıyla eşleştirmek için.</summary>
    public static long RecordingStartUnixUs { get; private set; }
    public static double Elapsed => Clock.Elapsed.TotalSeconds;

    public static void StartRecording()
    {
        _recording = new();
        RecordingStartUnixUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        Clock.Restart();
    }

    public static void ExportRecording(string path)
    {
        if (_recording == null) return;
        double end = _recording.Count == 0 ? Elapsed : Math.Max(Elapsed, _recording.Max(r => r.Time + r.Data.Length / (double)Rate));
        var mix = new float[(int)(end * Rate) + Rate];
        foreach (var (time, data) in _recording)
        {
            int start = (int)(time * Rate);
            for (int i = 0; i < data.Length && start + i < mix.Length; i++) mix[start + i] += data[i];
        }
        using var w = new BinaryWriter(File.Create(path));
        int dataLen = mix.Length * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataLen);
        foreach (var v in mix) w.Write((short)Math.Clamp(v, short.MinValue, short.MaxValue));
    }

    static double BootTone(double t)
    {
        // Yavaşça açılan, parıltılı bir akor.
        double[] notes = { 261.63, 392.00, 523.25, 659.25, 783.99, 1046.5 };
        double s = 0;
        for (int i = 0; i < notes.Length; i++)
        {
            double start = i * 0.09;
            if (t < start) continue;
            double lt = t - start;
            double env = Math.Min(1, lt / 0.04) * Math.Exp(-lt * 1.6);
            s += (Sine(notes[i], t) + 0.25 * Sine(notes[i] * 2.003, t)) * env * 0.09;
        }
        s += Sine(130.81, t) * Math.Min(1, t / 0.5) * Math.Exp(-t * 1.1) * 0.12;
        return s;
    }

    static double Sine(double f, double t) => Math.Sin(2 * Math.PI * f * t);
    static double Decay(double t, double k) => t < 0 ? 0 : Math.Exp(-t * k);
    static double Window(double t, double a, double b) => t >= a && t < b ? Math.Min(1, (t - a) / 0.004) : 0;

    static SoundPlayer Make(double seconds, Func<double, double> wave)
    {
        int n = (int)(seconds * Rate);
        var samples = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double fadeOut = Math.Min(1, (n - i) / (Rate * 0.01));
            samples[i] = (short)(Math.Clamp(wave(t) * fadeOut, -1, 1) * short.MaxValue);
        }

        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataLen = n * 2;
            w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(dataLen);
            foreach (var s in samples) w.Write(s);
        }
        ms.Position = 0;
        var player = new SoundPlayer(ms);
        player.Load();
        Samples[player] = samples;
        return player;
    }
}
