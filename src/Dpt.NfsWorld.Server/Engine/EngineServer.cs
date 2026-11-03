using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Dpt.NfsWorld.Core;
using Dpt.NfsWorld.Web;

namespace Dpt.NfsWorld.Engine
{
    /// <summary>
    /// The game HTTP endpoint (Engine.svc). Own HTTP/1.1 stack on TcpListener:
    /// no URL ACL, no HttpListener quirks on XP, keep-alive and chunked requests work.
    /// </summary>
    public sealed class EngineServer
    {
        private readonly ServerApp _app;
        private readonly Config _cfg;
        private readonly EngineRouter _router;
        private readonly object _banLock = new object();
        private readonly Dictionary<string, DateTime> _bans = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _errors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _port;
        private int _inflight;
        private long _requests;
        private long _errorsTotal;

        public event EventHandler Failed;

        public EngineServer(ServerApp app)
        {
            _app = app;
            _cfg = app.Cfg;
            _router = new EngineRouter(app);
        }

        public int Port { get { return _port; } }
        public long Requests { get { return Interlocked.Read(ref _requests); } }
        public long Errors { get { return Interlocked.Read(ref _errorsTotal); } }
        public int Inflight { get { return Interlocked.CompareExchange(ref _inflight, 0, 0); } }

        public bool IsRunning
        {
            get { return _running && _acceptThread != null && _acceptThread.IsAlive; }
        }

        public string StatusLine()
        {
            return "engine:" + _port + " req=" + Requests + " err=" + Errors + " inflight=" + Inflight;
        }

        private static IPAddress ResolveBind(string text)
        {
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

            int[] ports = _cfg.PortsWithFallbacks(_cfg.EnginePort, _cfg.EnginePortFallbacks);
            IPAddress bind = ResolveBind(_cfg.Bind);
            Exception last = null;

            for (int i = 0; i < ports.Length; i++)
            {
                try
                {
                    TcpListener l = new TcpListener(bind, ports[i]);
                    l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    l.Start(512);
                    _listener = l;
                    _port = ports[i];
                    _running = true;
                    _acceptThread = new Thread(AcceptLoop);
                    _acceptThread.IsBackground = true;
                    _acceptThread.Name = "engine-accept";
                    _acceptThread.Start();
                    Log.Info("ENGINE", "listening on " + bind + ":" + _port + "  (client: http://<host>:" + _port + "/Engine.svc)");
                    if (ports[i] != _cfg.EnginePort) Log.Warn("ENGINE", "port " + _cfg.EnginePort + " busy, using " + ports[i]);
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { if (_listener != null) _listener.Stop(); } catch { }
                    _listener = null;
                    Log.Warn("ENGINE", "cannot bind " + bind + ":" + ports[i] + ": " + ex.Message);
                }
            }
            Log.Error("ENGINE", "failed to start on every candidate port: " + (last == null ? "?" : last.Message));
            return false;
        }

        public bool Restart()
        {
            Log.Warn("ENGINE", "restarting listener");
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
            if (was) Log.Info("ENGINE", "stopped");
        }

        private void RaiseFailed()
        {
            try { EventHandler h = Failed; if (h != null) h(this, EventArgs.Empty); } catch { }
        }

        private void AcceptLoop()
        {
            int failures = 0;
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    failures = 0;
                }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                catch (SocketException sex)
                {
                    if (!_running) break;
                    failures++;
                    Log.Warn("ENGINE", "accept failed (" + sex.SocketErrorCode + "): " + sex.Message);
                    if (failures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(Math.Min(2000, 50 * failures));
                    continue;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    failures++;
                    Log.Error("ENGINE", "unexpected accept error: " + ex.Message);
                    if (failures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(100);
                    continue;
                }
                if (client == null) continue;

                if (Interlocked.Increment(ref _inflight) > _cfg.MaxConcurrentRequests)
                {
                    Interlocked.Decrement(ref _inflight);
                    try
                    {
                        client.Client.Send(Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                    }
                    catch { }
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

        private void ProcessConnection(object state)
        {
            TcpClient client = (TcpClient)state;
            HttpConnection conn = null;
            string ip = "0.0.0.0";
            int served = 0;
            try
            {
                try { client.NoDelay = true; } catch { }
                try { client.ReceiveTimeout = 30000; } catch { }
                try { client.SendTimeout = 30000; } catch { }

                IPEndPoint ep = null;
                try { ep = client.Client.RemoteEndPoint as IPEndPoint; } catch { }
                ip = ep == null ? "0.0.0.0" : Util.Ip(ep.Address);

                if (IsBanned(ip))
                {
                    try { client.Client.Send(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); } catch { }
                    return;
                }

                conn = new HttpConnection(client);
                conn.RemoteIp = ip;
                conn.RemotePort = ep == null ? 0 : ep.Port;
                conn.ServerHeader = "DPT.Server";

                while (_running && served < 50)
                {
                    HttpRequest req;
                    int errorStatus;
                    bool ok;
                    try { ok = conn.TryReadRequest(out req, out errorStatus); }
                    catch (HttpProtocolException hpe)
                    {
                        RegisterProtocolError(ip, hpe.Message);
                        try { conn.WriteSimple(hpe.Status, HttpResponse.Reason(hpe.Status), true); } catch { }
                        break;
                    }
                    catch (IOException) { break; }
                    catch (SocketException) { break; }

                    if (!ok)
                    {
                        if (errorStatus != 0)
                        {
                            RegisterProtocolError(ip, "http " + errorStatus);
                            try { conn.WriteSimple(errorStatus, HttpResponse.Reason(errorStatus), true); } catch { }
                        }
                        break;
                    }

                    served++;
                    Interlocked.Increment(ref _requests);
                    Interlocked.Increment(ref _app.TotalRequests);

                    DateTime started = DateTime.UtcNow;
                    HttpResponse resp = new HttpResponse();
                    try
                    {
                        _router.Route(req, resp);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _errorsTotal);
                        Log.Error("ENGINE", "handler crashed on " + req.Method + " " + req.Path + ": " + ex);
                        resp = new HttpResponse();
                        resp.Status = 500;
                        resp.ContentType = "text/plain; charset=utf-8";
                        resp.Body = Encoding.UTF8.GetBytes("internal error");
                    }

                    if (Log.ProtocolTrace)
                    {
                        string ua = req.Header("User-Agent") ?? "";
                        Log.Proto("ENGINE", "> " + req.Method + " " + req.Path + (string.IsNullOrEmpty(req.Query) ? "" : "?" + req.Query) +
                                            " | " + ip + " | " + Util.Truncate(resp.ContentType, 40) + " | ua=" + Util.Truncate(ua, 48));
                        if (req.Body != null && req.Body.Length > 0)
                            Log.Proto("ENGINE", "    req  " + req.Body.Length + "B: " + Util.Truncate(Text(req.Body), 400));
                        if (resp.Body != null && resp.Body.Length > 0 && resp.Body.Length <= 4096)
                            Log.Proto("ENGINE", "    resp " + resp.Body.Length + "B: " + Util.Truncate(Text(resp.Body), 400));
                    }

                    // The NFSW client advertises Accept-Encoding: gzip and then always gunzips the
                    // body - a plain body makes it die with "Out of memory". Jetty compresses
                    // every response, down to a couple of hundred bytes, so do the same.
                    if (resp.Body != null && resp.Body.Length > 0 &&
                        !resp.Headers.ContainsKey("Content-Encoding") &&
                        IsCompressible(resp.ContentType) &&
                        (req.Header("Accept-Encoding") ?? "").IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        byte[] gz = HttpCompression.GzipAlways(resp.Body);
                        if (gz != null)
                        {
                            // no size check: even a 187-byte XML is compressed by the
                            // reference server, and this client always gunzips
                            resp.Body = gz;
                            resp.Set("Content-Encoding", "gzip");
                        }
                    }

                    bool keep = req.KeepAlive && served < 50;
                    try { conn.WriteResponse(resp, string.Equals(req.Method, "HEAD", StringComparison.Ordinal), keep); }
                    catch (IOException) { break; }
                    catch (ObjectDisposedException) { break; }
                    if (!keep) break;

                    if ((DateTime.UtcNow - started).TotalMilliseconds > 3000)
                        Log.Warn("ENGINE", "slow request " + req.Method + " " + req.Path + " took " +
                                           (int)(DateTime.UtcNow - started).TotalMilliseconds + "ms");
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorsTotal);
                Log.Debug("ENGINE", "connection error (" + ip + "): " + ex.Message);
            }
            finally
            {
                if (conn != null) conn.Dispose();
                else { try { client.Close(); } catch { } }
                Interlocked.Decrement(ref _inflight);
                // "client hung up after 1 request" is how a crashing game looks from here
                if (served > 0) Log.Proto("ENGINE", "< connection closed after " + served + " request(s) from " + ip);
            }
        }

        private static string Text(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            return Encoding.UTF8.GetString(body);
        }

        /// <summary>
        /// Text payloads the game reads as XML/JSON: exactly the ones the reference
        /// server compresses. Images and the like stay as they are.
        /// </summary>
        private static bool IsCompressible(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return false;
            string t = contentType.ToLowerInvariant();
            return t.IndexOf("xml", StringComparison.Ordinal) >= 0 ||
                   t.IndexOf("json", StringComparison.Ordinal) >= 0 ||
                   t.IndexOf("javascript", StringComparison.Ordinal) >= 0 ||
                   t.IndexOf("text/", StringComparison.Ordinal) >= 0;
        }

        // -------------------------------------------------------------- bans

        private bool IsBanned(string ip)
        {
            lock (_banLock)
            {
                DateTime until;
                if (!_bans.TryGetValue(ip, out until)) return false;
                if (until <= DateTime.UtcNow) { _bans.Remove(ip); return false; }
                return true;
            }
        }

        private void RegisterProtocolError(string ip, string reason)
        {
            bool banned = false;
            lock (_banLock)
            {
                int count;
                _errors.TryGetValue(ip, out count);
                count++;
                _errors[ip] = count;
                if (count >= _cfg.MaxProtocolErrorsPerIp)
                {
                    _bans[ip] = DateTime.UtcNow.AddSeconds(_cfg.BanSeconds);
                    _errors.Remove(ip);
                    banned = true;
                }
            }
            if (banned)
                Log.Warn("ENGINE", "banned " + ip + " for " + _cfg.BanSeconds + "s after protocol errors (last: " + reason + ")");
        }
    }
}