using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nova.Core;

/// <summary>
/// Kabuk (COM) çağrıları için arka plan STA iş parçacıkları. Son istenen iş önce yapılır (LIFO),
/// böylece hızlı kaydırmada ekranda görünen öğeler önce yüklenir.
/// </summary>
public static class StaWorker
{
    static readonly ConcurrentStack<Action> Work = new();
    static readonly SemaphoreSlim Signal = new(0);

    static StaWorker()
    {
        for (int i = 0; i < 3; i++)
        {
            var t = new Thread(Loop) { IsBackground = true, Name = "Nova STA " + i, Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    static void Loop()
    {
        while (true)
        {
            Signal.Wait();
            if (Work.TryPop(out var a)) a();
        }
    }

    public static Task<T> Run<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Work.Push(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        Signal.Release();
        return tcs.Task;
    }
}

/// <summary>Görsel dosyalarını arka planda çözer ve önbelleğe alır. Yalnızca UI iş parçacığından çağrılır.</summary>
public static class ImageCache
{
    static readonly Dictionary<string, Task<BitmapSource?>> Cache = new();
    static readonly LinkedList<string> LargeOrder = new();

    public static Task<BitmapSource?> LoadAsync(string path, int decodeWidth, bool cache = true)
    {
        var key = path + "|" + decodeWidth;
        if (cache && Cache.TryGetValue(key, out var existing)) return existing;

        var task = Task.Run(() => Decode(path, decodeWidth));
        if (cache)
        {
            Cache[key] = task;
            if (decodeWidth >= 1280)
            {
                // Büyük arka plan görselleri bellekte çok yer kaplar; yalnızca son birkaçını tut.
                LargeOrder.AddLast(key);
                if (LargeOrder.Count > 6)
                {
                    Cache.Remove(LargeOrder.First!.Value);
                    LargeOrder.RemoveFirst();
                }
            }
        }
        return task;
    }

    static BitmapSource? Decode(string path, int decodeWidth)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.DecodePixelWidth = decodeWidth;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    /// <summary>Fotoğraf görüntüleyici için: EXIF yönünü uygular, çok büyük görselleri küçültür.</summary>
    public static Task<BitmapSource?> LoadPhotoAsync(string path, int maxSize) => Task.Run<BitmapSource?>(() =>
    {
        try
        {
            ushort orientation = 1;
            int pw, ph;
            using (var fs = File.OpenRead(path))
            {
                var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None);
                pw = frame.PixelWidth; ph = frame.PixelHeight;
                if (frame.Metadata is BitmapMetadata md)
                {
                    foreach (var q in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}" })
                    {
                        try
                        {
                            if (md.GetQuery(q) is ushort o) { orientation = o; break; }
                        }
                        catch { }
                    }
                }
            }

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (Math.Max(pw, ph) > maxSize)
            {
                if (pw >= ph) bi.DecodePixelWidth = maxSize; else bi.DecodePixelHeight = maxSize;
            }
            bi.EndInit();
            bi.Freeze();

            double angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
            if (angle == 0) return bi;
            var rotated = new TransformedBitmap(bi, new RotateTransform(angle));
            rotated.Freeze();
            return rotated;
        }
        catch { return null; }
    });

    /// <summary>Ortam ışığı için görseli birkaç piksele indirger; büyütülünce yumuşak renk lekesi olur.</summary>
    public static BitmapSource? Tiny(ImageSource? src)
    {
        if (src is not BitmapSource bs || bs.PixelWidth == 0 || bs.PixelHeight == 0) return null;
        try
        {
            var tb = new TransformedBitmap(bs, new ScaleTransform(10.0 / bs.PixelWidth, 10.0 / bs.PixelHeight));
            tb.Freeze();
            return tb;
        }
        catch { return null; }
    }
}
