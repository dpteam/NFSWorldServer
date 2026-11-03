using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Dpt.NfsWorld.Core;
using Dpt.NfsWorld.Engine;

namespace Dpt.NfsWorld.Web
{
    public enum WebRole
    {
        Public = 0,
        Admin = 1
    }

    /// <summary>
    /// Both web boards: one TcpListener + hand written HTTP/1.1 per role.
    /// The public board is redacted (no IP, token, machine id, logs); the admin board
    /// is LAN-only by default and requires the admin token for its API.
    /// </summary>
    public sealed class WebServer
    {
        private sealed class RateWindow
        {
            public DateTime StartUtc;
            public int Count;
        }

        private sealed class FailWindow
        {
            public DateTime UntilUtc;
            public int Count;
        }

        private readonly ServerApp _app;
        private readonly Config _cfg;
        private readonly WebRole _role;
        private readonly WebApi _api;
        private readonly object _rateLock = new object();
        private readonly Dictionary<string, RateWindow> _rate = new Dictionary<string, RateWindow>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FailWindow> _fails = new Dictionary<string, FailWindow>(StringComparer.OrdinalIgnoreCase);

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _port;
        private readonly string _root;
        private int _inflight;
        private long _requests;
        private long _errors;
        private readonly DateTime _startedUtc = DateTime.UtcNow;

        public event EventHandler Failed;

        public WebServer(ServerApp app, WebRole role)
        {
            _app = app;
            _cfg = app.Cfg;
            _role = role;
            _api = new WebApi(app, this);
            _root = _cfg.WebRootPath;
        }

        public WebRole Role { get { return _role; } }
        public bool IsAdmin { get { return _role == WebRole.Admin; } }
        public string RoleName { get { return _role == WebRole.Admin ? "admin" : "public"; } }
        public int Port { get { return _port; } }
        public string Root { get { return _root; } }
        public long Requests { get { return Interlocked.Read(ref _requests); } }
        public long Errors { get { return Interlocked.Read(ref _errors); } }
        public int Inflight { get { return Interlocked.CompareExchange(ref _inflight, 0, 0); } }
        public string BasePath { get { return _role == WebRole.Admin ? _cfg.AdminBasePath : _cfg.PublicBasePath; } }

        public bool IsRunning
        {
            get { return _running && _acceptThread != null && _acceptThread.IsAlive; }
        }

        public string BaseUrl
        {
            get
            {
                string host = (_role == WebRole.Admin ? _cfg.AdminBind : _cfg.PublicBind);
                if (string.IsNullOrEmpty(host) || host == "0.0.0.0" || host == "*") host = "localhost";
                return "http://" + host + ":" + _port.ToString(CultureInfo.InvariantCulture) + BasePath + "/";
            }
        }

        public string StatusLine()
        {
            return RoleName + ":" + _port + " req=" + Requests + " err=" + Errors + " inflight=" + Inflight;
        }

        private IPAddress ResolveBind()
        {
            string text = _role == WebRole.Admin ? _cfg.AdminBind : _cfg.PublicBind;
            if (string.IsNullOrEmpty(text) || text == "*" || text == "+" || text == "0.0.0.0") return IPAddress.Any;
            if (string.Equals(text, "localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
            IPAddress ip;
            return IPAddress.TryParse(text, out ip) ? ip : IPAddress.Any;
        }

        public bool Start()
        {
            if (_running) return true;
            HttpConnection.MaxBody = _cfg.MaxRequestBytes;
            HttpConnection.MaxHeaders = _cfg.MaxHeaderBytes;

            int primary = _role == WebRole.Admin ? _cfg.AdminPort : _cfg.PublicPort;
            int[] fallbacks = _role == WebRole.Admin ? _cfg.AdminPortFallbacks : _cfg.PublicPortFallbacks;
            int[] ports = _cfg.PortsWithFallbacks(primary, fallbacks);
            IPAddress bind = ResolveBind();
            Exception last = null;

            for (int i = 0; i < ports.Length; i++)
            {
                try
                {
                    TcpListener l = new TcpListener(bind, ports[i]);
                    l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    l.Start(256);
                    _listener = l;
                    _port = ports[i];
                    _running = true;
                    _acceptThread = new Thread(AcceptLoop);
                    _acceptThread.IsBackground = true;
                    _acceptThread.Name = "web-" + RoleName;
                    _acceptThread.Start();
                    Log.Info("WEB", RoleName + " board on " + bind + ":" + _port +
                                    (BasePath.Length > 0 ? " base=" + BasePath : "") + " root=" + _root);
                    if (ports[i] != primary) Log.Warn("WEB", RoleName + " port " + primary + " busy, using " + ports[i]);
                    if (_role == WebRole.Admin && _cfg.AdminPrivateOnly)
                        Log.Info("WEB", "admin board accepts private/LAN addresses only (web.adminPrivateOnly=true)");
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { if (_listener != null) _listener.Stop(); } catch { }
                    _listener = null;
                    Log.Warn("WEB", RoleName + " cannot bind " + bind + ":" + ports[i] + ": " + ex.Message);
                }
            }
            Log.Error("WEB", RoleName + " failed to start on every port: " + (last == null ? "?" : last.Message));
            return false;
        }

        public bool Restart()
        {
            Log.Warn("WEB", "restarting " + RoleName + " listener");
            try { Stop(); } catch { }
            Thread.Sleep(300);
            return Start();
        }

        public void Stop()
        {
            bool was = _running;
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            try { if (_acceptThread != null && _acceptThread.IsAlive) _acceptThread.Join(2000); } catch { }
            _acceptThread = null;
            if (was) Log.Info("WEB", RoleName + " board stopped");
        }

        private void AcceptLoop()
        {
            int failures = 0;
            while (_running)
            {
                TcpClient client = null;
                try { client = _listener.AcceptTcpClient(); failures = 0; }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                catch (SocketException sex)
                {
                    if (!_running) break;
                    failures++;
                    Log.Warn("WEB", RoleName + " accept failed (" + sex.SocketErrorCode + "): " + sex.Message);
                    if (failures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(Math.Min(2000, 50 * failures));
                    continue;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    failures++;
                    Log.Error("WEB", RoleName + " accept error: " + ex.Message);
                    if (failures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(100);
                    continue;
                }
                if (client == null) continue;

                if (Interlocked.Increment(ref _inflight) > _cfg.MaxConcurrentRequests)
                {
                    Interlocked.Decrement(ref _inflight);
                    try { client.Client.Send(Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); } catch { }
                    try { client.Close(); } catch { }
                    continue;
                }
                try { ThreadPool.QueueUserWorkItem(ProcessConnection, client); }
                catch
                {
                    Interlocked.Decrement(ref _inflight);
                    try { client.Close(); } catch { }
                }
            }
        }

        private void RaiseFailed()
        {
            try { EventHandler h = Failed; if (h != null) h(this, EventArgs.Empty); } catch { }
        }

        private void ProcessConnection(object state)
        {
            TcpClient client = (TcpClient)state;
            HttpConnection conn = null;
            try
            {
                try { client.NoDelay = true; } catch { }
                try { client.ReceiveTimeout = 15000; } catch { }
                try { client.SendTimeout = 20000; } catch { }

                IPEndPoint ep = null;
                try { ep = client.Client.RemoteEndPoint as IPEndPoint; } catch { }
                string peerIp = ep == null ? "0.0.0.0" : Util.Ip(ep.Address);

                if (_role == WebRole.Admin && _cfg.AdminPrivateOnly && !IsPrivate(peerIp))
                {
                    Log.Warn("WEB", "admin request from non private address " + peerIp + " rejected");
                    HttpResponse denied = new HttpResponse();
                    denied.Status = 403;
                    denied.ContentType = "text/plain; charset=utf-8";
                    denied.Body = Encoding.UTF8.GetBytes("Admin board is LAN only (web.adminPrivateOnly).");
                    try
                    {
                        conn = new HttpConnection(client);
                        conn.RemoteIp = peerIp;
                        conn.ServerHeader = "DPT.Server";
                        conn.WriteResponse(denied, false, false);
                    }
                    catch { }
                    return;
                }

                conn = new HttpConnection(client);
                conn.RemoteIp = peerIp;
                conn.RemotePort = ep == null ? 0 : ep.Port;
                conn.ServerHeader = "DPT.Server";

                int served = 0;
                while (_running && served < 100)
                {
                    HttpRequest req;
                    int errorStatus;
                    bool ok;
                    try { ok = conn.TryReadRequest(out req, out errorStatus); }
                    catch (HttpProtocolException hpe)
                    {
                        try { conn.WriteSimple(hpe.Status, HttpResponse.Reason(hpe.Status), true); } catch { }
                        break;
                    }
                    catch (IOException) { break; }
                    catch (SocketException) { break; }

                    if (!ok)
                    {
                        if (errorStatus != 0)
                        {
                            try { conn.WriteSimple(errorStatus, HttpResponse.Reason(errorStatus), true); } catch { }
                        }
                        break;
                    }

                    served++;
                    Interlocked.Increment(ref _requests);
                    Interlocked.Increment(ref _app.TotalRequests);

                    string clientIp = EffectiveClientIp(req);
                    if (!RateAllowed(clientIp))
                    {
                        HttpResponse busy = Error(429, "rate limit");
                        busy.Set("Retry-After", "5");
                        conn.WriteResponse(busy, false, false);
                        break;
                    }

                    HttpResponse resp = new HttpResponse();
                    try { Route(req, resp, clientIp); }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _errors);
                        Log.Error("WEB", RoleName + " " + req.Method + " " + req.Target + " failed: " + ex);
                        resp = Error(500, "internal server error");
                    }

                    bool keep = req.KeepAlive && served < 100;
                    conn.WriteResponse(resp, string.Equals(req.Method, "HEAD", StringComparison.Ordinal), keep);
                    if (!keep) break;
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                Log.Debug("WEB", RoleName + " connection error: " + ex.Message);
            }
            finally
            {
                if (conn != null) conn.Dispose();
                else { try { client.Close(); } catch { } }
                Interlocked.Decrement(ref _inflight);
            }
        }

        // ------------------------------------------------------------- routing

        private void Route(HttpRequest req, HttpResponse resp, string clientIp)
        {
            string path = req.Path ?? "/";
            string basePath = BasePath;
            if (basePath.Length > 0 && path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(basePath.Length);
                if (path.Length == 0) path = "/";
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                _api.Handle(req, resp, path, clientIp);
                resp.Set("Cache-Control", "no-store");
                ApplySecurityHeaders(resp);
                return;
            }

            if (path == "/" || path.Length == 0)
            {
                ServeFile(req, resp, _role == WebRole.Admin ? "/admin.html" : "/index.html");
                return;
            }
            if (path == "/admin" || path == "/admin/")
            {
                ServeFile(req, resp, "/admin.html");
                return;
            }
            if (path == "/health" || string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
            {
                SendText(req, resp, 200, "ok");
                return;
            }
            if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
                path += "index.html";
            ServeStatic(req, resp, path);
        }

        public void ApplySecurityHeaders(HttpResponse resp)
        {
            resp.Set("X-Content-Type-Options", "nosniff");
            resp.Set("X-Frame-Options", "SAMEORIGIN");
            resp.Set("Referrer-Policy", "no-referrer");
            resp.Set("Content-Security-Policy",
                "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
                "script-src 'self'; connect-src 'self'; font-src 'self'; base-uri 'none'; form-action 'self'");
            resp.Set("Cross-Origin-Resource-Policy", "same-origin");
        }

        private void ServeStatic(HttpRequest req, HttpResponse resp, string urlPath)
        {
            string safe = SafePath(urlPath);
            if (safe == null) { SendText(req, resp, 404, "not found"); return; }
            ServeFile(req, resp, safe);
        }

        private void ServeFile(HttpRequest req, HttpResponse resp, string relative)
        {
            string file = Path.GetFullPath(Path.Combine(_root, relative.TrimStart('/').Replace('/', '\\')));
            string rootFull = Path.GetFullPath(_root);
            if (!file.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                SendText(req, resp, 404, "not found");
                return;
            }
            try
            {
                DateTime lastWrite = File.GetLastWriteTimeUtc(file);
                string etag = "\"" + lastWrite.Ticks.ToString("x", CultureInfo.InvariantCulture) + "-" +
                              new FileInfo(file).Length.ToString("x", CultureInfo.InvariantCulture) + "\"";
                string inm = req.Header("If-None-Match");
                if (!string.IsNullOrEmpty(inm) && inm.IndexOf(etag, StringComparison.Ordinal) >= 0)
                {
                    resp.Status = 304;
                    resp.ContentType = null;
                    resp.Body = new byte[0];
                    resp.Set("ETag", etag);
                    ApplySecurityHeaders(resp);
                    return;
                }

                byte[] data = File.ReadAllBytes(file);
                string ext = Path.GetExtension(file).ToLowerInvariant();
                resp.Status = 200;
                resp.ContentType = Mime(ext);
                resp.Set("ETag", etag);
                resp.Set("Cache-Control", ext == ".html" ? "no-cache" : "public, max-age=300");
                ApplySecurityHeaders(resp);

                if (_cfg.Compress && data.Length > 1024 && IsText(ext) &&
                    (req.Header("Accept-Encoding") ?? "").IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    byte[] gz = HttpCompression.Gzip(data);
                    if (gz != null && gz.Length < data.Length)
                    {
                        resp.Body = gz;
                        resp.Set("Content-Encoding", "gzip");
                        resp.Set("Vary", "Accept-Encoding");
                        return;
                    }
                }
                resp.Body = data;
            }
            catch (Exception ex)
            {
                Log.Warn("WEB", "cannot serve " + relative + ": " + ex.Message);
                SendText(req, resp, 500, "cannot read file");
            }
        }

        private static string SafePath(string urlPath)
        {
            string p = (urlPath ?? "/").Replace('\\', '/');
            if (p.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
            if (p.IndexOf('\0') >= 0) return null;
            return p;
        }

        private static bool IsText(string ext)
        {
            return ext == ".html" || ext == ".css" || ext == ".js" || ext == ".json" || ext == ".svg" || ext == ".txt";
        }

        public static string Mime(string ext)
        {
            switch (ext)
            {
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".ico": return "image/x-icon";
                case ".woff2": return "font/woff2";
                case ".woff": return "font/woff";
                case ".ttf": return "font/ttf";
                case ".txt": return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        public void SendText(HttpRequest req, HttpResponse resp, int status, string text)
        {
            resp.Status = status;
            resp.ContentType = "text/plain; charset=utf-8";
            resp.Body = Encoding.UTF8.GetBytes(text ?? "");
            ApplySecurityHeaders(resp);
        }

        public void SendJson(HttpRequest req, HttpResponse resp, int status, string json)
        {
            resp.Status = status;
            resp.ContentType = "application/json; charset=utf-8";
            resp.Body = Encoding.UTF8.GetBytes(json ?? "{}");
            ApplySecurityHeaders(resp);
        }

        private HttpResponse Error(int status, string message)
        {
            JsonBuilder jb = new JsonBuilder(64);
            jb.BeginObject().Prop("ok", false).Prop("error", message).EndObject();
            HttpResponse resp = new HttpResponse();
            resp.Status = status;
            resp.ContentType = "application/json; charset=utf-8";
            resp.Body = Encoding.UTF8.GetBytes(jb.ToString());
            ApplySecurityHeaders(resp);
            return resp;
        }

        // --------------------------------------------------------------- proxy

        public string EffectiveClientIp(HttpRequest req)
        {
            string peer = req.RemoteIp ?? "0.0.0.0";
            if (!_cfg.TrustProxyHeaders) return peer;
            if (!IsTrustedProxy(peer)) return peer;

            string chain = req.Header("X-Forwarded-For");
            if (string.IsNullOrEmpty(chain)) chain = req.Header("X-Real-IP");
            if (string.IsNullOrEmpty(chain)) return peer;

            string[] parts = chain.Split(',');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string candidate = parts[i].Trim();
                if (candidate.Length == 0) continue;
                if (!IsTrustedProxy(candidate)) return candidate;
            }
            return peer;
        }

        public static bool IsPrivate(string ip)
        {
            return Util.IsPrivateIpv4(ip);
        }

        private bool IsTrustedProxy(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            for (int i = 0; i < _cfg.TrustedProxies.Length; i++)
            {
                string entry = _cfg.TrustedProxies[i];
                if (string.IsNullOrEmpty(entry)) continue;
                if (entry.IndexOf('/') >= 0)
                {
                    if (CidrContains(entry, ip)) return true;
                }
                else if (string.Equals(entry, ip, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool CidrContains(string cidr, string ip)
        {
            try
            {
                int slash = cidr.IndexOf('/');
                string net = cidr.Substring(0, slash);
                int bits = Util.ParseInt(cidr.Substring(slash + 1), 0);
                IPAddress netAddr, ipAddr;
                if (!IPAddress.TryParse(net, out netAddr) || !IPAddress.TryParse(ip, out ipAddr)) return false;
                byte[] a = netAddr.GetAddressBytes();
                byte[] b = ipAddr.GetAddressBytes();
                if (a.Length != b.Length) return false;
                int fullBytes = bits / 8;
                int rest = bits % 8;
                for (int i = 0; i < fullBytes; i++) if (a[i] != b[i]) return false;
                if (rest > 0)
                {
                    int mask = 0xFF << (8 - rest);
                    if ((a[fullBytes] & mask) != (b[fullBytes] & mask)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- rate

        private bool RateAllowed(string ip)
        {
            int limit = _cfg.RequestsPerMinute;
            if (limit <= 0) return true;
            DateTime now = DateTime.UtcNow;
            lock (_rateLock)
            {
                RateWindow w;
                if (!_rate.TryGetValue(ip, out w) || (now - w.StartUtc).TotalSeconds >= 60)
                {
                    w = new RateWindow();
                    w.StartUtc = now;
                    w.Count = 0;
                    _rate[ip] = w;
                }
                w.Count++;
                if (_rate.Count > 4000) PruneRate(now);
                return w.Count <= limit;
            }
        }

        private void PruneRate(DateTime now)
        {
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, RateWindow> kv in _rate)
                if ((now - kv.Value.StartUtc).TotalSeconds >= 120) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++) _rate.Remove(dead[i]);
        }

        // ---------------------------------------------------------------- admin

        /// <summary>Returns null when the token is valid, otherwise an error response.</summary>
        public HttpResponse CheckAdminToken(HttpRequest req, string clientIp)
        {
            string ip = clientIp ?? req.RemoteIp;
            FailWindow fw;
            lock (_rateLock)
            {
                if (_fails.TryGetValue(ip, out fw) && fw.UntilUtc > DateTime.UtcNow)
                    return Error(429, "too many failed logins, retry later");
            }

            string token = req.Header("X-Admin-Token");
            if (string.IsNullOrEmpty(token))
            {
                string auth = req.Header("Authorization");
                if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    token = auth.Substring(7).Trim();
            }
            if (string.IsNullOrEmpty(token) && _cfg.AdminAllowQueryToken) token = req.QueryString("token");

            if (!string.IsNullOrEmpty(token) && Util.FixedEquals(token, _cfg.AdminToken))
            {
                lock (_rateLock) _fails.Remove(ip);
                return null;
            }

            lock (_rateLock)
            {
                if (!_fails.TryGetValue(ip, out fw)) { fw = new FailWindow(); _fails[ip] = fw; }
                fw.Count++;
                if (fw.Count >= _cfg.AdminMaxFailures)
                {
                    fw.UntilUtc = DateTime.UtcNow.AddSeconds(_cfg.AdminBanSeconds);
                    fw.Count = 0;
                    Log.Warn("WEB", "admin auth failures from " + ip + ": banned for " + _cfg.AdminBanSeconds + "s");
                }
            }
            Log.Warn("WEB", "admin API unauthorized request from " + ip + " (" + req.Method + " " + req.Path + ")");
            HttpResponse resp = Error(401, "admin token required");
            resp.Set("WWW-Authenticate", "Token realm=\"DPT.Server admin\"");
            return resp;
        }

        public string UptimeText
        {
            get
            {
                TimeSpan t = DateTime.UtcNow - _startedUtc;
                if (t.TotalHours >= 1) return ((int)t.TotalHours) + "h " + t.Minutes + "m";
                return ((int)t.TotalMinutes) + "m " + t.Seconds + "s";
            }
        }
    }
}