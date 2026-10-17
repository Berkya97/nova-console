using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nova.Core;

/// <summary>
/// Geliştirici test / tanıtım modu: NOVA_SCRIPT ortam değişkeni ayarlıysa verilen eylem dizisini
/// uygulama içinde oynatır ve NOVA_SHOTS klasörüne ekran görüntüleri kaydeder.
/// Örnek: "wait:3000 TabNext Right Confirm shot:apps quit"
///
/// Tanıtım videosu için ek komutlar:
///   mark:Başlık_metni   zaman damgalı alt yazı işareti (events.txt)
///   slowtype:metin      harf harf yazar
///   diğerleri (type:, goto:, pick:, pad, kb) MainWindow'a iletilir.
/// Çıkışta (quit) çalınan arayüz sesleri sfx.wav olarak dışa aktarılır.
/// </summary>
public static class DevScript
{
    public static bool Active => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOVA_SCRIPT"));

    public static async void RunIfRequested(FrameworkElement root, Action<NavAction> dispatch, Action<string> command, Action quit)
    {
        var script = Environment.GetEnvironmentVariable("NOVA_SCRIPT");
        if (string.IsNullOrWhiteSpace(script)) return;
        var dir = Environment.GetEnvironmentVariable("NOVA_SHOTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        var events = new List<string> { $"t0 {Sfx.RecordingStartUnixUs}" };

        foreach (var token in script.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("wait:")) { await Task.Delay(int.Parse(token[5..])); continue; }
            if (token.StartsWith("shot:")) { Save(root, Path.Combine(dir, token[5..] + ".png")); continue; }
            if (token.StartsWith("mark:"))
            {
                events.Add($"mark {Sfx.Elapsed.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} {token[5..].Replace('_', ' ')}");
                continue;
            }
            if (token.StartsWith("slowtype:"))
            {
                foreach (var ch in token[9..].Replace('_', ' '))
                {
                    command("type:" + ch);
                    await Task.Delay(170);
                }
                continue;
            }
            if (token == "quit")
            {
                events.Add($"end {Sfx.Elapsed.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}");
                File.WriteAllLines(Path.Combine(dir, "events.txt"), events);
                Sfx.ExportRecording(Path.Combine(dir, "sfx.wav"));
                quit();
                return;
            }
            if (Enum.TryParse<NavAction>(token, true, out var a)) dispatch(a);
            else command(token);
            await Task.Delay(350);
        }
    }

    static void Save(FrameworkElement root, string path)
    {
        const double width = 1600;
        double scale = width / root.ActualWidth;
        var rtb = new RenderTargetBitmap((int)width, (int)(root.ActualHeight * scale), 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
