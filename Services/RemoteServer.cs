using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using Nova.Core;

namespace Nova.Services;

/// <summary>
/// Telefonla kumanda: yerel ağda küçük bir web sunucusu. Telefonun tarayıcısında açılan sayfa
/// yön tuşları, A/B/X/Y, yazı kutusu ve fare için dokunmatik alan sunar. Nova öndeyken yazılar Nova'ya,
/// başka bir uygulama öndeyken o uygulamaya gider. Erişim, Nova ekranında görünen PIN ile korunur.
/// </summary>
public sealed class RemoteServer
{
    public const int Port = 8642;

    TcpListener? _listener;
    CancellationTokenSource? _cts;
    readonly HashSet<string> _tokens = new();
    int _failedLogins;
    DateTime _lockedUntil;

    public event Action<NavAction>? Action;
    public event Action<string>? Text;
    public event Action? Backspace;
    public event Action? Submit;
    public event Action? Connected;
    /// <summary>Özel tuşlar: "tab", "esc", "desktop" (masaüstü modunu aç / kapat).</summary>
    public event Action<string>? Key;
    /// <summary>Dokunmatik alandan imleç hareketi (dx, dy) ve tekerlek.</summary>
    public event Action<int, int, int>? Mouse;
    /// <summary>Dokunmatik alanda dokunuş: false sol, true sağ tık.</summary>
    public event Action<bool>? Click;

    public string Pin { get; set; } = "0000";
    public bool Running => _listener != null;

    public static string? LocalAddress()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var s = ua.Address.ToString();
                if (s.StartsWith("192.168.") || s.StartsWith("10.") || s.StartsWith("172.")) return s;
            }
        }
        return null;
    }

    public string Url => $"http://{LocalAddress() ?? "localhost"}:{Port}";

    public bool Start()
    {
        if (_listener != null) return true;
        try
        {
            // NOVA_REMOTE_LOOPBACK=1: test için yalnızca bu bilgisayardan erişim (güvenlik duvarı sormaz).
            var address = Environment.GetEnvironmentVariable("NOVA_REMOTE_LOOPBACK") == "1" ? IPAddress.Loopback : IPAddress.Any;
            _listener = new TcpListener(address, Port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = AcceptLoop(_listener, _cts.Token);
            return true;
        }
        catch (Exception e)
        {
            App.Log(e);
            _listener = null;
            return false;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _tokens.Clear();
    }

    async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Handle(client), ct);
        }
    }

    void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                var stream = client.GetStream();
                var (method, path, headers, body) = await ReadRequest(stream);
                if (method == null) return;
                var cookie = headers.GetValueOrDefault("cookie") ?? "";
                var token = cookie.Split(';').Select(c => c.Trim()).FirstOrDefault(c => c.StartsWith("nova="))?[5..];
                bool authed;
                lock (_tokens) authed = token != null && _tokens.Contains(token);
                var route = path.Split('?')[0];
                var query = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "";

                if (method == "POST" && route == "/login")
                {
                    var pin = WebUtility.UrlDecode(body.Split('=').ElementAtOrDefault(1) ?? "");
                    // Telefon artık başka uygulamalara da yazabildiği için PIN denemeleri sınırlanır.
                    bool locked;
                    lock (_tokens) locked = DateTime.UtcNow < _lockedUntil;
                    if (!locked && pin == Pin)
                    {
                        lock (_tokens) _failedLogins = 0;
                        var t = Guid.NewGuid().ToString("N");
                        lock (_tokens) _tokens.Add(t);
                        OnUi(() => Connected?.Invoke());
                        await Write(stream, "303 See Other", "text/plain", "", $"Location: /\r\nSet-Cookie: nova={t}; Path=/; Max-Age=2592000\r\n");
                    }
                    else
                    {
                        lock (_tokens)
                        {
                            if (!locked && ++_failedLogins >= 5) { _failedLogins = 0; _lockedUntil = DateTime.UtcNow.AddMinutes(1); locked = true; }
                        }
                        await Write(stream, "200 OK", "text/html; charset=utf-8", Page.Login(true, locked));
                    }
                    return;
                }
                if (!authed)
                {
                    if (method == "GET") await Write(stream, "200 OK", "text/html; charset=utf-8", Page.Login(false));
                    else await Write(stream, "401 Unauthorized", "text/plain", "");
                    return;
                }
                switch (route)
                {
                    case "/":
                        await Write(stream, "200 OK", "text/html; charset=utf-8", Page.Remote());
                        return;
                    case "/a" when method == "POST":
                        var k = query.StartsWith("k=") ? query[2..] : "";
                        if (Enum.TryParse<NavAction>(k, true, out var a)) OnUi(() => Action?.Invoke(a));
                        else if (k == "backspace") OnUi(() => Backspace?.Invoke());
                        else if (k == "enter") OnUi(() => Submit?.Invoke());
                        else if (k is "tab" or "esc" or "desktop") OnUi(() => Key?.Invoke(k));
                        break;
                    case "/m" when method == "POST":
                        var q = query.Split('&').Select(p => p.Split('=')).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
                        int Num(string key) => q.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? Math.Clamp(n, -4000, 4000) : 0;
                        if (q.TryGetValue("c", out var c)) OnUi(() => Click?.Invoke(c == "r"));
                        else
                        {
                            int dx = Num("dx"), dy = Num("dy"), w = Num("w");
                            if (dx != 0 || dy != 0 || w != 0) OnUi(() => Mouse?.Invoke(dx, dy, w));
                        }
                        break;
                    case "/t" when method == "POST":
                        var text = body;
                        if (text.Length > 0) OnUi(() => Text?.Invoke(text));
                        break;
                }
                await Write(stream, "204 No Content", "text/plain", "");
            }
            catch { }
        }
    }

    static async Task<(string? Method, string Path, Dictionary<string, string> Headers, string Body)> ReadRequest(NetworkStream s)
    {
        var buf = new byte[16384];
        int len = 0, headerEnd = -1;
        while (len < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(len, buf.Length - len));
            if (n <= 0) break;
            len += n;
            headerEnd = IndexOf(buf, len, "\r\n\r\n"u8);
            if (headerEnd >= 0) break;
        }
        if (headerEnd < 0) return (null, "", new(), "");
        var head = Encoding.ASCII.GetString(buf, 0, headerEnd).Split("\r\n");
        var first = head[0].Split(' ');
        if (first.Length < 2) return (null, "", new(), "");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in head.Skip(1))
        {
            int i = h.IndexOf(':');
            if (i > 0) headers[h[..i].Trim()] = h[(i + 1)..].Trim();
        }
        int contentLength = int.TryParse(headers.GetValueOrDefault("content-length"), out var cl) ? Math.Min(cl, 4096) : 0;
        int bodyStart = headerEnd + 4;
        while (len - bodyStart < contentLength && len < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(len, buf.Length - len));
            if (n <= 0) break;
            len += n;
        }
        var body = Encoding.UTF8.GetString(buf, bodyStart, Math.Max(0, Math.Min(contentLength, len - bodyStart)));
        return (first[0], first[1], headers, body);
    }

    static int IndexOf(byte[] buf, int len, ReadOnlySpan<byte> pattern) => buf.AsSpan(0, len).IndexOf(pattern);

    static async Task Write(NetworkStream s, string status, string type, string body, string extraHeaders = "")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n{extraHeaders}\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(bytes);
    }

    static class Page
    {
        const string Style = """
            <meta name="viewport" content="width=device-width,initial-scale=1,user-scalable=no">
            <style>
            *{box-sizing:border-box;-webkit-tap-highlight-color:transparent;user-select:none}
            body{margin:0;font-family:system-ui,-apple-system,Segoe UI,sans-serif;color:#fff;
              background:radial-gradient(circle at 80% 0%,#1d3d86 0,#0b1b40 45%,#040811 100%);min-height:100vh;
              display:flex;flex-direction:column;align-items:center;padding:20px 16px 32px}
            h1{font-weight:300;letter-spacing:.5em;margin:6px 0 18px;font-size:22px}
            button{font:inherit;color:#fff;border:1px solid rgba(255,255,255,.18);background:rgba(255,255,255,.1);
              border-radius:18px;font-size:18px;touch-action:manipulation}
            button:active{background:#fff;color:#0b0f1a}
            .row{display:flex;gap:10px;width:100%;max-width:420px;margin-bottom:12px}
            .row button{flex:1;height:52px}
            .pad{display:grid;grid-template-columns:repeat(3,84px);grid-template-rows:repeat(3,84px);gap:10px;margin:8px 0 18px}
            .pad button{font-size:26px;border-radius:22px}
            .face{display:grid;grid-template-columns:repeat(3,72px);grid-template-rows:repeat(3,72px);gap:6px;margin-bottom:18px}
            .face button{border-radius:50%;font-weight:700;font-size:22px}
            .a{color:#4cc38a;border-color:#4cc38a!important}.b{color:#f0626a;border-color:#f0626a!important}
            .x{color:#5b9bf8;border-color:#5b9bf8!important}.y{color:#f2c14e;border-color:#f2c14e!important}
            input{font:inherit;font-size:18px;width:100%;padding:14px 16px;border-radius:16px;border:1px solid rgba(255,255,255,.2);
              background:rgba(255,255,255,.08);color:#fff;outline:none;user-select:text}
            .muted{opacity:.6;font-size:14px;margin-top:8px;text-align:center}
            #pad{width:100%;max-width:420px;height:220px;border-radius:22px;border:1px dashed rgba(255,255,255,.3);
              background:rgba(255,255,255,.05);display:flex;align-items:center;justify-content:center;text-align:center;
              font-size:14px;opacity:.85;margin-bottom:12px;touch-action:none;padding:0 24px}
            h2{font-weight:400;font-size:15px;letter-spacing:.2em;opacity:.6;margin:16px 0 10px;text-transform:uppercase}
            </style>
            """;

        public static string Login(bool wrong, bool locked = false) => $$"""
            <!doctype html><html lang="tr"><head><meta charset="utf-8"><title>Nova</title>{{Style}}</head><body>
            <h1>NOVA</h1>
            <form method="post" action="/login" style="width:100%;max-width:320px">
              <input name="pin" inputmode="numeric" maxlength="4" autofocus placeholder="{{L.T("Nova ekranındaki PIN")}}">
              <div class="row" style="margin-top:12px"><button type="submit">{{L.T("Bağlan")}}</button></div>
              {{(wrong ? $"<div class=\"muted\" style=\"color:#f0626a;opacity:1\">{L.T(locked ? "Çok fazla yanlış deneme. Bir dakika sonra tekrar dene." : "PIN yanlış")}</div>" : "")}}
              <div class="muted">{{L.T("PIN'i Nova'da Kontrol Merkezi › Telefon kartında görebilirsin.")}}</div>
            </form></body></html>
            """;

        public static string Remote() => $$"""
            <!doctype html><html lang="tr"><head><meta charset="utf-8"><title>Nova</title>{{Style}}</head><body>
            <h1>NOVA</h1>
            <div class="row"><button data-k="TabPrev">LB</button><button data-k="Control">☰ {{L.T("Kontrol")}}</button><button data-k="TabNext">RB</button></div>
            <div class="pad">
              <span></span><button data-k="Up">▲</button><span></span>
              <button data-k="Left">◀</button><button class="a" data-k="Confirm">●</button><button data-k="Right">▶</button>
              <span></span><button data-k="Down">▼</button><span></span>
            </div>
            <div class="face">
              <span></span><button class="y" data-k="Options">Y</button><span></span>
              <button class="x" data-k="Search">X</button><span></span><button class="b" data-k="Back">B</button>
              <span></span><button class="a" data-k="Confirm">A</button><span></span>
            </div>
            <div class="row"><input id="t" placeholder="{{L.T("Yaz ve gönder (arama, ad değiştirme…)")}}" autocomplete="off"></div>
            <div class="row"><button id="send">{{L.T("Gönder")}}</button><button data-k="backspace">⌫</button><button data-k="enter">↵</button></div>
            <div class="row"><button data-k="PageUp">{{L.T("Sayfa ▲")}}</button><button data-k="Menu">⏻ {{L.T("Güç")}}</button><button data-k="PageDown">{{L.T("Sayfa ▼")}}</button></div>
            <h2>{{L.T("Fare")}}</h2>
            <div id="pad">{{L.T("Kaydır: imleç · Dokun: tıkla · İki parmak: kaydır / sağ tık")}}</div>
            <div class="row"><button id="lc">{{L.T("Sol tık")}}</button><button id="rc">{{L.T("Sağ tık")}}</button></div>
            <div class="row"><button data-k="tab">Tab</button><button data-k="esc">Esc</button><button data-k="desktop">{{L.T("Masaüstü modu")}}</button></div>
            <div class="muted">{{L.T("Nova öndeyken yazılar Nova'ya, başka bir uygulama öndeyken o uygulamaya gider.")}}</div>
            <script>
            const post=(u,b)=>fetch(u,{method:'POST',body:b||''});
            document.querySelectorAll('[data-k]').forEach(el=>{
              const go=e=>{e.preventDefault();navigator.vibrate&&navigator.vibrate(12);post('/a?k='+el.dataset.k)};
              el.addEventListener('touchstart',go,{passive:false});el.addEventListener('mousedown',go);
            });
            const t=document.getElementById('t');
            const send=()=>{ if(t.value){ post('/t',t.value); t.value=''; } };
            document.getElementById('send').onclick=send;
            t.addEventListener('keydown',e=>{ if(e.key==='Enter'){ send(); post('/a?k=enter'); } });
            // Dokunmatik alan: hareketler biriktirilip aynı anda tek istek olacak şekilde gönderilir.
            const pad=document.getElementById('pad');
            let dx=0,dy=0,w=0,busy=false,last=null,start=0,moved=false,fingers=0;
            const flush=()=>{ if(busy||(!dx&&!dy&&!w)) return; busy=true;
              const u='/m?dx='+Math.round(dx)+'&dy='+Math.round(dy)+'&w='+Math.round(w); dx=dy=w=0;
              post(u).finally(()=>busy=false); };
            setInterval(flush,30);
            const mid=ts=>{let x=0,y=0;for(const p of ts){x+=p.clientX;y+=p.clientY;}return {x:x/ts.length,y:y/ts.length};};
            pad.addEventListener('touchstart',e=>{e.preventDefault(); if(!fingers){start=Date.now();moved=false;}
              fingers=Math.max(fingers,e.touches.length); last=mid(e.touches);},{passive:false});
            pad.addEventListener('touchmove',e=>{e.preventDefault(); const m=mid(e.touches); const ddx=m.x-last.x, ddy=m.y-last.y; last=m;
              if(Math.abs(ddx)+Math.abs(ddy)>1) moved=true;
              if(e.touches.length>=2) w-=ddy*4; else { const k=1.6+Math.min(2.4,Math.hypot(ddx,ddy)/12); dx+=ddx*k; dy+=ddy*k; } },{passive:false});
            pad.addEventListener('touchend',e=>{e.preventDefault(); if(e.touches.length){last=mid(e.touches);return;}
              if(!moved&&Date.now()-start<300){navigator.vibrate&&navigator.vibrate(12); post('/m?c='+(fingers>=2?'r':'l'));}
              fingers=0;},{passive:false});
            document.getElementById('lc').onclick=()=>post('/m?c=l');
            document.getElementById('rc').onclick=()=>post('/m?c=r');
            </script></body></html>
            """;
    }
}
