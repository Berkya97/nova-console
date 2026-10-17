using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.Networking.Connectivity;

namespace Nova.Core;

/// <summary>Varsayılan ses aygıtının ana ses seviyesi (Core Audio).</summary>
public static class SystemVolume
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr p);
        int UnregisterControlChangeNotify(IntPtr p);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float db, ref Guid ctx);
        int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        int GetMasterVolumeLevel(out float db);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        int GetChannelVolumeLevel(uint ch, out float db);
        int GetChannelVolumeLevelScalar(uint ch, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static IAudioEndpointVolume? Endpoint()
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.GetDefaultAudioEndpoint(0, 1, out var dev) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out var o) != 0) return null;
            return (IAudioEndpointVolume)o;
        }
        catch { return null; }
    }

    /// <returns>0–100 arası, ses aygıtı yoksa null.</returns>
    public static int? Get()
    {
        var ep = Endpoint();
        if (ep == null) return null;
        ep.GetMasterVolumeLevelScalar(out var v);
        ep.GetMute(out var muted);
        return muted ? 0 : (int)Math.Round(v * 100);
    }

    public static void Set(int percent)
    {
        var ep = Endpoint();
        if (ep == null) return;
        var ctx = Guid.Empty;
        ep.SetMute(percent <= 0, ref ctx);
        ep.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref ctx);
    }
}

/// <summary>Dizüstü ekran parlaklığı (WMI). Masaüstü monitörlerde desteklenmez.</summary>
public static class Brightness
{
    public static int? Get()
    {
        try
        {
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    public static void Set(int percent)
    {
        try
        {
            using var mc = new ManagementClass("root\\WMI", "WmiMonitorBrightnessMethods", null);
            foreach (ManagementObject o in mc.GetInstances())
                o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(percent, 0, 100) });
        }
        catch { }
    }
}

public static class Network
{
    /// <summary>Bağlantı adı (Wi-Fi ağı veya Ethernet), kablosuz mu, sinyal çubukları.</summary>
    public static (string Name, bool Wireless, int? Bars)? Current()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null) return null;
            return (p.ProfileName, p.IsWlanConnectionProfile, p.GetSignalBars());
        }
        catch { return null; }
    }
}

/// <summary>Windows'ta şu an çalan medya (Spotify, tarayıcı, vb.) ve kontrolü.</summary>
public static class NowPlaying
{
    public sealed record Info(string Title, string Artist, string App, bool Playing, BitmapSource? Art);

    static GlobalSystemMediaTransportControlsSessionManager? _mgr;

    static async Task<GlobalSystemMediaTransportControlsSession?> Session()
    {
        try
        {
            _mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _mgr.GetCurrentSession();
        }
        catch { return null; }
    }

    public static async Task<Info?> GetAsync()
    {
        var s = await Session();
        if (s == null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            BitmapSource? art = null;
            if (props.Thumbnail != null)
            {
                try
                {
                    using var ras = await props.Thumbnail.OpenReadAsync();
                    using var stream = ras.AsStreamForRead();
                    var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    ms.Position = 0;
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.DecodePixelWidth = 256;
                    bi.EndInit();
                    bi.Freeze();
                    art = bi;
                }
                catch { }
            }
            var app = s.SourceAppUserModelId ?? "";
            app = Path.GetFileNameWithoutExtension(app.Split('!')[0].Split('_')[0]);
            return new Info(props.Title ?? "", props.Artist ?? "", app, playing, art);
        }
        catch { return null; }
    }

    public static async Task PlayPause() { if (await Session() is { } s) await s.TryTogglePlayPauseAsync(); }
    public static async Task Next() { if (await Session() is { } s) await s.TrySkipNextAsync(); }
    public static async Task Previous() { if (await Session() is { } s) await s.TrySkipPreviousAsync(); }
}

/// <summary>Windows hesap fotoğrafı.</summary>
public static class AccountPicture
{
    public static BitmapSource? Load()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid != null)
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid);
                foreach (var name in new[] { "Image448", "Image240", "Image208", "Image192" })
                    if (key?.GetValue(name) is string p && File.Exists(p)) return Decode(File.ReadAllBytes(p));
            }
        }
        catch { }

        try
        {
            // .accountpicture-ms dosyalarının içine gömülü JPEG'ler var; en büyüğünü al.
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "AccountPictures");
            var file = Directory.Exists(dir)
                ? new DirectoryInfo(dir).GetFiles("*.accountpicture-ms").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
                : null;
            if (file == null) return null;
            var data = File.ReadAllBytes(file.FullName);
            byte[]? best = null;
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] != 0xFF || data[i + 1] != 0xD8 || data[i + 2] != 0xFF) continue;
                for (int j = data.Length - 2; j > i; j--)
                {
                    if (data[j] == 0xFF && data[j + 1] == 0xD9)
                    {
                        var len = j + 2 - i;
                        if (best == null || len > best.Length) best = data[i..(j + 2)];
                        break;
                    }
                }
                break;
            }
            return best == null ? null : Decode(best);
        }
        catch { return null; }
    }

    static BitmapSource Decode(byte[] bytes)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(bytes);
        bi.DecodePixelWidth = 128;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}

/// <summary>Steam'in VDF (KeyValues) metin biçimi için küçük ayrıştırıcı.</summary>
public static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        int i = 0;
        return ParseObject(text, ref i);
    }

    static Dictionary<string, object> ParseObject(string t, ref int i)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (i < t.Length)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) break;
            if (t[i] == '}') { i++; break; }
            var key = ReadString(t, ref i);
            SkipWs(t, ref i);
            if (i < t.Length && t[i] == '{')
            {
                i++;
                d[key] = ParseObject(t, ref i);
            }
            else d[key] = ReadString(t, ref i);
        }
        return d;
    }

    static void SkipWs(string t, ref int i)
    {
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i])) i++;
            else if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; }
            else break;
        }
    }

    static string ReadString(string t, ref int i)
    {
        if (i < t.Length && t[i] == '"')
        {
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < t.Length && t[i] != '"')
            {
                if (t[i] == '\\' && i + 1 < t.Length) { i++; sb.Append(t[i] == 'n' ? '\n' : t[i]); }
                else sb.Append(t[i]);
                i++;
            }
            i++;
            return sb.ToString();
        }
        int start = i;
        while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != '{' && t[i] != '}') i++;
        return t[start..i];
    }

    public static Dictionary<string, object>? Path(Dictionary<string, object> d, params string[] keys)
    {
        var cur = d;
        foreach (var k in keys)
        {
            if (!cur.TryGetValue(k, out var v) || v is not Dictionary<string, object> next) return null;
            cur = next;
        }
        return cur;
    }
}
