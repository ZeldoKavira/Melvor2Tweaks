using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Melvor2Tweaks.Remote;

/// <summary>A parsed HTTP request (just what the phone app needs).</summary>
internal sealed class Request
{
    public string Method = "GET";
    public string Path = "/";
    public Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body = Array.Empty<byte>();

    public string Q(string name, string fallback = "") => Query.TryGetValue(name, out var v) ? v : fallback;

    public JsonElement Json()
    {
        if (Body.Length == 0) return JsonDocument.Parse("{}").RootElement;
        return JsonDocument.Parse(Body).RootElement;
    }

    public string? Cookie(string name)
    {
        if (!Headers.TryGetValue("Cookie", out var all)) return null;
        foreach (var part in all.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim() == name) return part[(eq + 1)..].Trim();
        }
        return null;
    }
}

internal sealed class Response
{
    public int Status = 200;
    public string ContentType = "application/json; charset=utf-8";
    public byte[] Body = Array.Empty<byte>();
    public List<string> ExtraHeaders = new();

    public static Response Json(object? value, int status = 200) => new()
    {
        Status = status,
        Body = JsonSerializer.SerializeToUtf8Bytes(value, RemoteServer.JsonOptions),
    };

    public static Response Error(string message, int status = 400) => Json(new { error = message }, status);

    public static Response Text(string text, string type, int status = 200) => new()
    {
        Status = status, ContentType = type, Body = Encoding.UTF8.GetBytes(text),
    };
}

/// <summary>
/// The phone companion's web server: a tiny HTTP/1.1 server on 127.0.0.1 only (Cloudflare Tunnel, or a
/// browser on this PC, connects to it - nothing else can). A random key, sent once as ?key=... and then kept
/// in a cookie, is needed for everything but the login page. Game calls run on the game's main thread
/// (<see cref="OnMain{T}"/>); the server threads only parse and write.
/// </summary>
internal static class RemoteServer
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private const string CookieName = "m2t_key";
    private static TcpListener? s_listener;
    private static CancellationTokenSource? s_cts;
    internal static int Port { get; private set; }
    internal static bool Running => s_listener != null;
    internal static string LastError { get; private set; } = "";

    internal static string Key
    {
        get
        {
            if (string.IsNullOrEmpty(Settings.Current.RemoteKey))
            {
                Settings.Current.RemoteKey = NewKey();
                Settings.Save();
            }
            return Settings.Current.RemoteKey!;
        }
    }

    internal static string NewKey() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    internal static void ResetKey()
    {
        Settings.Current.RemoteKey = NewKey();
        Settings.Save();
        Plugin.Log("Phone access: new key made; old links stop working");
    }

    /// <summary>The link that logs a phone in: the public address if set, else this PC's local one.</summary>
    internal static string LoginLink()
    {
        string host = (Settings.Current.RemotePublicUrl ?? "").Trim().TrimEnd('/');
        if (host.Length == 0) host = $"http://localhost:{Settings.Current.RemotePort}";
        return $"{host}/?key={Key}";
    }

    /// <summary>Set while this copy of the mod is loaded (settings saves before that don't start anything).</summary>
    internal static bool Live { get; set; }

    /// <summary>After a settings save: start, stop or move the server to match.</summary>
    internal static void Sync()
    {
        if (!Live) return;
        bool want = Settings.Current.RemoteEnabled;
        if (want != Running || (want && (Port != Settings.Current.RemotePort || s_lan != Settings.Current.RemoteLan))) Start();
        s_allowed = ParseAllowed();
    }

    internal static void Start()
    {
        Stop();
        if (!Settings.Current.RemoteEnabled) return;
        int port = Settings.Current.RemotePort;
        try
        {
            bool lan = Settings.Current.RemoteLan;
            var listener = new TcpListener(lan ? IPAddress.Any : IPAddress.Loopback, port);
            listener.Start();
            s_listener = listener;
            s_lan = lan;
            s_allowed = ParseAllowed();
            Port = port;
            s_cts = new CancellationTokenSource();
            var token = s_cts.Token;
            _ = Task.Run(() => AcceptLoop(listener, token));
            LastError = "";
            Plugin.Log($"Phone access: listening on http://localhost:{port}/" +
                       (lan ? $" and the network (accepting {(s_allowed.Count == 0 ? "no other address yet" : string.Join(", ", s_allowed))})" : ""));
        }
        catch (Exception e)
        {
            s_listener = null;
            LastError = e.Message;
            Plugin.Log($"Phone access: couldn't listen on port {port}: {e.Message}");
        }
    }

    internal static void Stop()
    {
        s_cts?.Cancel();
        try { s_listener?.Stop(); } catch { }
        s_listener = null;
        s_cts = null;
    }

    private static async Task AcceptLoop(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch { return; } // stopped
            _ = Task.Run(() => Serve(client, token));
        }
    }

    private static bool s_lan;
    private static HashSet<IPAddress> s_allowed = new();

    private static HashSet<IPAddress> ParseAllowed()
    {
        var set = new HashSet<IPAddress>();
        foreach (var part in (Settings.Current.RemoteAllowedClients ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (IPAddress.TryParse(part, out var ip)) set.Add(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
        return set;
    }

    /// <summary>This PC always; other machines only when listed (the reverse proxy).</summary>
    private static bool AllowedClient(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint ep) return false;
        var ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        return IPAddress.IsLoopback(ip) || s_allowed.Contains(ip);
    }

    private static async Task Serve(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            if (!AllowedClient(client))
            {
                Plugin.Log($"Phone access: refused a connection from {client.Client.RemoteEndPoint}");
                return;
            }
            try
            {
                client.ReceiveTimeout = 10_000;
                client.SendTimeout = 10_000;
                var stream = client.GetStream();
                var request = await Read(stream, token);
                if (request == null) return;
                Response response;
                try { response = await Handle(request); }
                catch (Exception e)
                {
                    Plugin.Log($"Phone access: {request.Method} {request.Path} failed: {e.Message}");
                    response = Response.Error(e.Message, 500);
                }
                await Write(stream, response, token);
            }
            catch { /* client went away */ }
        }
    }

    private static async Task<Request?> Read(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        int filled = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (filled == buffer.Length) return null; // headers too big
            int n = await stream.ReadAsync(buffer.AsMemory(filled), token);
            if (n == 0) return null;
            filled += n;
            headerEnd = IndexOf(buffer, filled, "\r\n\r\n"u8);
        }
        string head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var request = new Request { Method = first[0].ToUpperInvariant() };
        string target = first[1];
        int qm = target.IndexOf('?');
        request.Path = Uri.UnescapeDataString(qm < 0 ? target : target[..qm]);
        if (qm >= 0)
            foreach (var pair in target[(qm + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string k = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                string v = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                request.Query[k] = v;
            }
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0) request.Headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        int length = request.Headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out int len) ? len : 0;
        if (length > 256 * 1024) return null;
        var body = new byte[length];
        int already = Math.Min(length, filled - (headerEnd + 4));
        Array.Copy(buffer, headerEnd + 4, body, 0, already);
        while (already < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(already), token);
            if (n == 0) return null;
            already += n;
        }
        request.Body = body;
        return request;
    }

    private static int IndexOf(byte[] data, int count, ReadOnlySpan<byte> pattern) =>
        data.AsSpan(0, count).IndexOf(pattern);

    private static async Task Write(NetworkStream stream, Response r, CancellationToken token)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {r.Status} {(r.Status switch { 200 => "OK", 302 => "Found", 401 => "Unauthorized", 404 => "Not Found", 500 => "Server Error", _ => "Status" })}\r\n");
        sb.Append($"Content-Type: {r.ContentType}\r\nContent-Length: {r.Body.Length}\r\nConnection: close\r\n");
        if (!r.ExtraHeaders.Exists(h => h.StartsWith("Cache-Control"))) sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n");
        foreach (var h in r.ExtraHeaders) sb.Append(h).Append("\r\n");
        sb.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), token);
        await stream.WriteAsync(r.Body, token);
    }

    private static bool Authorized(Request r)
    {
        string given = r.Cookie(CookieName) ?? (r.Headers.TryGetValue("X-Key", out var h) ? h : "");
        var a = Encoding.UTF8.GetBytes(given);
        var b = Encoding.UTF8.GetBytes(Key);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static async Task<Response> Handle(Request r)
    {
        // Login: /?key=... sets the cookie and drops the key from the address bar.
        if (r.Path == "/" && r.Query.TryGetValue("key", out var key))
        {
            var a = Encoding.UTF8.GetBytes(key);
            var b = Encoding.UTF8.GetBytes(Key);
            if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
                return Response.Text(Page("That link is out of date. Copy a new one from the game (Settings → Melvor2Tweaks → Phone access)."), "text/html; charset=utf-8", 401);
            var redirect = new Response { Status = 302, ContentType = "text/plain" };
            redirect.ExtraHeaders.Add($"Set-Cookie: {CookieName}={Key}; Path=/; Max-Age=31536000; HttpOnly; SameSite=Strict" +
                                      (BehindHttps(r) ? "; Secure" : ""));
            redirect.ExtraHeaders.Add("Location: /");
            return redirect;
        }

        if (r.Path == "/manifest.webmanifest") return Response.Text(Manifest, "application/manifest+json");
        if (!Authorized(r))
        {
            if (r.Path.StartsWith("/api/") || r.Path.StartsWith("/icon/")) return Response.Error("Not logged in", 401);
            return Response.Text(Page("Open the phone link from the game to log in (Settings → Melvor2Tweaks → Phone access → Copy link)."), "text/html; charset=utf-8", 401);
        }

        if (r.Path == "/" || r.Path == "/index.html") return Response.Text(App(), "text/html; charset=utf-8");
        if (r.Path.StartsWith("/icon/")) return await Icons.Serve(r.Path["/icon/".Length..]);
        if (r.Path.StartsWith("/api/")) return await RemoteApi.Handle(r);
        return Response.Error("Not found", 404);
    }

    /// <summary>Reached through the proxy (https outside): anything but a localhost address.</summary>
    private static bool BehindHttps(Request r) =>
        (r.Headers.TryGetValue("X-Forwarded-Proto", out var proto) && proto == "https") ||
        (r.Headers.TryGetValue("Host", out var host) && !host.StartsWith("localhost") && !host.StartsWith("127.0.0.1"));

    private static string Page(string message) =>
        "<!doctype html><meta name=viewport content='width=device-width,initial-scale=1'><title>Melvor Idle 2</title>" +
        "<body style='background:#17140f;color:#e8dcc0;font:16px system-ui;padding:24px'><h2 style='color:#f5c84c'>Melvor Idle 2</h2><p>" +
        WebUtility.HtmlEncode(message) + "</p></body>";

    private const string Manifest =
        "{\"name\":\"Melvor Idle 2\",\"short_name\":\"Melvor 2\",\"start_url\":\"/\",\"display\":\"standalone\"," +
        "\"background_color\":\"#17140f\",\"theme_color\":\"#17140f\"}";

    private static string? s_app;

    /// <summary>The single-page app, embedded in the mod (Remote/web/index.html).</summary>
    private static string App()
    {
        if (s_app != null) return s_app;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Melvor2Tweaks.Remote.web.index.html");
        if (stream == null) return "<p>App missing from the mod build.</p>";
        using var reader = new StreamReader(stream);
        return s_app = reader.ReadToEnd();
    }

    // ---- main thread -------------------------------------------------------------------------------
    /// <summary>Runs <paramref name="f"/> on Godot's main thread (where the game and its UI live) and waits for it.</summary>
    internal static async Task<T> OnMain<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Callable.From(() =>
        {
            try { tcs.TrySetResult(f()); }
            catch (Exception e) { tcs.TrySetException(e); }
        }).CallDeferred();
        var done = await Task.WhenAny(tcs.Task, Task.Delay(8000));
        if (done != tcs.Task) throw new TimeoutException("the game didn't answer in time");
        return await tcs.Task;
    }
}
