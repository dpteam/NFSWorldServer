using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Dpt.NfsWorld.Core;
using Dpt.NfsWorld.Engine;

namespace Dpt.NfsWorld.Net
{
    /// <summary>
    /// EA-Chat (XMPP) endpoint. The client opens a TCP connection, asks for STARTTLS,
    /// then does legacy jabber:iq:auth with the persona id. This is not a general
    /// XMPP server - it answers exactly the stanzas NFS World sends, keeps a list of
    /// authenticated personas for announcements and never exposes more than that.
    /// </summary>
    public sealed class XmppServer
    {
        private sealed class Client
        {
            public TcpClient Tcp;
            public NetworkStream Raw;
            public SslStream Ssl;
            public string PersonaId;
            public string Ip;
            public DateTime ConnectedUtc = DateTime.UtcNow;
            public DateTime LastSeenUtc = DateTime.UtcNow;
            public readonly object WriteLock = new object();
            public readonly StringBuilder Buffer = new StringBuilder(1024);
            public bool StreamOpened;
            public bool LoggedIn;
        }

        private readonly ServerApp _app;
        private readonly Config _cfg;
        private readonly object _clientsLock = new object();
        private readonly List<Client> _clients = new List<Client>();
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _port;
        private X509Certificate2 _cert;
        private bool _certWarned;

        public event EventHandler Failed;

        public XmppServer(ServerApp app)
        {
            _app = app;
            _cfg = app.Cfg;
        }

        public int Port { get { return _port; } }
        public int Clients { get { lock (_clientsLock) return _clients.Count; } }
        public bool IsRunning { get { return _running && _acceptThread != null && _acceptThread.IsAlive; } }

        public string StatusLine()
        {
            return "xmpp:" + _port + " clients=" + Clients;
        }

        // ------------------------------------------------------------- lifecycle

        public bool Start()
        {
            if (_running) return true;
            if (!LoadCertificate()) return false;

            int[] ports = _cfg.PortsWithFallbacks(_cfg.XmppPort, _cfg.XmppPortFallbacks);
            IPAddress bind = IPAddress.Any;
            if (!string.IsNullOrEmpty(_cfg.Bind) && _cfg.Bind != "0.0.0.0" && _cfg.Bind != "*" && _cfg.Bind != "+")
            {
                IPAddress parsed;
                if (IPAddress.TryParse(_cfg.Bind, out parsed)) bind = parsed;
            }

            Exception last = null;
            for (int i = 0; i < ports.Length; i++)
            {
                try
                {
                    TcpListener l = new TcpListener(bind, ports[i]);
                    l.Start(128);
                    _listener = l;
                    _port = ports[i];
                    _running = true;
                    _acceptThread = new Thread(AcceptLoop);
                    _acceptThread.IsBackground = true;
                    _acceptThread.Name = "xmpp-accept";
                    _acceptThread.Start();
                    Log.Info("XMPP", "listening on " + bind + ":" + _port + " (TLS 1.0, EA-Chat)");
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { if (_listener != null) _listener.Stop(); } catch { }
                    _listener = null;
                    Log.Warn("XMPP", "cannot bind " + bind + ":" + ports[i] + ": " + ex.Message);
                }
            }
            Log.Error("XMPP", "failed to start: " + (last == null ? "?" : last.Message));
            return false;
        }

        private bool LoadCertificate()
        {
            if (_cert != null) return true;
            string path = _cfg.CertPath;
            if (!File.Exists(path))
            {
                if (!_certWarned)
                {
                    _certWarned = true;
                    Log.Error("XMPP", "certificate not found: " + path);
                    Log.Error("XMPP", "run: powershell -File tools\\make-cert.ps1   (creates data\\certs\\xmpp.pfx)");
                    Log.Error("XMPP", "the game endpoint keeps working, EA-Chat is disabled until a certificate exists");
                }
                return false;
            }
            try
            {
                _cert = new X509Certificate2(path, _cfg.CertPassword);
                Log.Info("XMPP", "TLS certificate loaded: " + _cert.Subject + " (expires " +
                                 _cert.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("XMPP", "cannot load certificate " + path + ": " + ex.Message);
                return false;
            }
        }

        public bool Restart()
        {
            Log.Warn("XMPP", "restarting listener");
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
            List<Client> copy;
            lock (_clientsLock) { copy = new List<Client>(_clients); _clients.Clear(); }
            for (int i = 0; i < copy.Count; i++) CloseQuietly(copy[i]);
            try { if (_acceptThread != null && _acceptThread.IsAlive) _acceptThread.Join(2000); } catch { }
            _acceptThread = null;
            if (was) Log.Info("XMPP", "stopped");
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
                    Log.Warn("XMPP", "accept failed (" + sex.SocketErrorCode + "): " + sex.Message);
                    if (failures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(Math.Min(2000, 50 * failures));
                    continue;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    failures++;
                    Log.Error("XMPP", "accept error: " + ex.Message);
                    if (failures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(100);
                    continue;
                }
                if (client == null) continue;
                try { ThreadPool.QueueUserWorkItem(HandleClient, client); }
                catch { try { client.Close(); } catch { } }
            }
        }

        private void RaiseFailed()
        {
            try { EventHandler h = Failed; if (h != null) h(this, EventArgs.Empty); } catch { }
        }

        // ---------------------------------------------------------------- session

        private void HandleClient(object state)
        {
            TcpClient tcp = (TcpClient)state;
            Client c = new Client();
            c.Tcp = tcp;
            try
            {
                c.Ip = Util.Ip(tcp.Client.RemoteEndPoint);
                tcp.NoDelay = true;
                tcp.ReceiveTimeout = 30000;
                tcp.SendTimeout = 15000;
                c.Raw = tcp.GetStream();

                if (_cfg.LogLevel >= LogLevel.Debug) Log.Debug("XMPP", "connection from " + c.Ip);

                if (!Handshake(c)) return;
                Serve(c);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log.Debug("XMPP", "client error (" + c.Ip + "): " + ex.Message);
            }
            finally
            {
                lock (_clientsLock) _clients.Remove(c);
                if (c.LoggedIn) MarkSession(c, false);
                CloseQuietly(c);
                Log.Debug("XMPP", "connection closed (" + c.Ip + ")");
            }
        }

        /// <summary>Plain TCP part: stream open and STARTTLS upgrade.</summary>
        private bool Handshake(Client c)
        {
            string first = ReadSome(c.Raw);
            if (first == null) return false;
            if (first.IndexOf("<stream:stream", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Log.Warn("XMPP", "client " + c.Ip + " did not open an XMPP stream: " + Util.Truncate(first, 120));
                return false;
            }
            Log.Proto("XMPP", "< " + c.Ip + " " + Util.Truncate(first, 300));

            Write(c, "<stream:stream xmlns='jabber:client' xml:lang='en' " +
                     "xmlns:stream='http://etherx.jabber.org/streams' from='" + HostName(c) + "' id='" +
                     NewStreamId() + "' version='1.0'>" +
                     "<stream:features><starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>" +
                     "<session xmlns='urn:ietf:params:xml:ns:xmpp-session'/></stream:features>");

            string next = ReadSome(c.Raw);
            if (next == null) return false;
            if (next.IndexOf("starttls", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Log.Warn("XMPP", "client " + c.Ip + " skipped STARTTLS, continuing in plain text");
                c.Buffer.Append(next);
                return true;
            }

            Write(c, "<proceed xmlns='urn:ietf:params:xml:ns:xmpp-tls'/>");

            try
            {
                SslStream ssl = new SslStream(c.Raw, false);
                // .NET 4.0 only knows SSL3/TLS1.0 here; the 2011 client speaks TLS 1.0.
                ssl.AuthenticateAsServer(_cert, false, System.Security.Authentication.SslProtocols.Tls, false);
                c.Ssl = ssl;
                Log.Proto("XMPP", "TLS established with " + c.Ip + " (" + ssl.SslProtocol + ")");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("XMPP", "TLS handshake failed for " + c.Ip + ": " + ex.Message);
                Log.Warn("XMPP", "TLS 1.0 must be enabled in Windows; run tools\\enable-tls10.ps1 (admin) if the client cannot connect");
                return false;
            }
        }

        /// <summary>Stanza loop. Works on a buffer, so split TCP reads are safe.</summary>
        private void Serve(Client c)
        {
            while (_running)
            {
                string stanza;
                while ((stanza = TakeStanza(c)) != null)
                {
                    c.LastSeenUtc = DateTime.UtcNow;
                    Process(c, stanza);
                }

                string more;
                try { more = ReadSome(In(c)); }
                catch (IOException) { return; }
                catch (ObjectDisposedException) { return; }
                if (more == null) { Close(c); return; }
                if (more.Length == 0) continue;
                c.Buffer.Append(more);
            }
        }

        /// <summary>Pops one complete XML fragment out of the client buffer.</summary>
        private string TakeStanza(Client c)
        {
            string buf = c.Buffer.ToString();
            if (buf.Length == 0) return null;

            if (!c.StreamOpened && buf.IndexOf("<stream:stream", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int end = buf.IndexOf('>');
                if (end < 0) return null;
                c.Buffer.Remove(0, end + 1);
                c.StreamOpened = true;
                Write(c, "<stream:stream xmlns='jabber:client' xml:lang='en' " +
                         "xmlns:stream='http://etherx.jabber.org/streams' from='" + HostName(c) + "' id='" +
                         NewStreamId() + "' version='1.0'><stream:features/>");
                return TakeStanza(c);
            }

            if (buf.IndexOf("</stream:stream>", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                c.Buffer.Remove(0, buf.IndexOf("</stream:stream>", StringComparison.OrdinalIgnoreCase) + 16);
                Write(c, "</stream:stream>");
                return null;
            }

            // <iq ...>, <presence ...>, <message ...>, <auth ...>
            string[] tags = new string[] { "iq", "presence", "message", "auth", "response" };
            for (int t = 0; t < tags.Length; t++)
            {
                string tag = tags[t];
                int start = buf.IndexOf("<" + tag, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                int open = buf.IndexOf('>', start);
                if (open < 0) return null;
                if (buf[open - 1] == '/')
                {
                    c.Buffer.Remove(0, open + 1);
                    return buf.Substring(start, open + 1 - start);
                }
                string closeTag = "</" + tag + ">";
                int end = buf.IndexOf(closeTag, open, StringComparison.OrdinalIgnoreCase);
                if (end < 0) return null;
                c.Buffer.Remove(0, end + closeTag.Length);
                return buf.Substring(start, end + closeTag.Length - start);
            }

            return null;
        }

        private void Process(Client c, string stanza)
        {
            Log.Proto("XMPP", "<[" + c.Ip + "/" + (c.PersonaId ?? "?") + "] " + Util.Truncate(stanza, 400));

            if (stanza.StartsWith("<iq", StringComparison.OrdinalIgnoreCase))
            {
                HandleIq(c, stanza);
                return;
            }
            if (stanza.StartsWith("<presence", StringComparison.OrdinalIgnoreCase))
            {
                HandlePresence(c, stanza);
                return;
            }
            if (stanza.StartsWith("<message", StringComparison.OrdinalIgnoreCase))
            {
                string body = Util.XmlValue(stanza, "body");
                if (!string.IsNullOrEmpty(body))
                {
                    Log.Info("CHAT", c.PersonaId + ": " + Util.Truncate(body, 200));
                    _app.Events.Add("chat", Util.Truncate(body, 120), c.PersonaId ?? "");
                }
                return;
            }
        }

        private void HandleIq(Client c, string stanza)
        {
            string id = Attribute(stanza, "id");
            string username = Util.XmlValue(stanza, "username");
            if (string.IsNullOrEmpty(id)) id = "EA-Chat-1";

            if (!string.IsNullOrEmpty(username))
            {
                string personaId = ExtractPersonaId(username);
                if (string.IsNullOrEmpty(personaId))
                {
                    Log.Warn("XMPP", "auth with an unusable username '" + username + "' from " + c.Ip);
                    return;
                }

                Write(c, "<iq id='" + id + "' type='result' xml:lang='en'><query xmlns='jabber:iq:auth'>" +
                         "<username>nfsw." + personaId + "</username><password/><digest/><resource/>" +
                         "<clientlock xmlns='http://www.jabber.com/schemas/clientlocking.xsd'/></query></iq>");

                if (!c.LoggedIn)
                {
                    c.PersonaId = personaId;
                    c.LoggedIn = true;
                    lock (_clientsLock) _clients.Add(c);
                    MarkSession(c, true);
                    Log.Info("XMPP", "persona " + personaId + " authenticated from " + c.Ip + " (" + Clients + " online)");
                    Welcome(c);
                }
                return;
            }

            Write(c, "<iq id='" + id + "' type='result' xml:lang='en'/>");
        }

        private void HandlePresence(Client c, string stanza)
        {
            string to = Attribute(stanza, "to");
            if (string.IsNullOrEmpty(to)) return;

            if (to.IndexOf("@conference", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Write(c, "<presence from='" + Util.EscapeXml(to) + "/nfsw." + c.PersonaId + "' to='nfsw." +
                         c.PersonaId + "@" + HostName(c) + "/EA-Chat'><x xmlns='http://jabber.org/protocol/muc#user'>" +
                         "<item affiliation='member' role='participant'/></x></presence>");
            }
            else
            {
                Write(c, "<presence from='nfsw." + c.PersonaId + "@" + HostName(c) + "/EA-Chat' to='" +
                         Util.EscapeXml(to) + "'/>");
            }
        }

        private static string ExtractPersonaId(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            string id = username;
            int dot = id.IndexOf('.');
            if (dot >= 0) id = id.Substring(dot + 1);
            StringBuilder digits = new StringBuilder();
            for (int i = 0; i < id.Length; i++) if (char.IsDigit(id[i])) digits.Append(id[i]);
            return digits.Length > 0 ? digits.ToString() : id.Trim();
        }

        // ------------------------------------------------------------- outbound

        private void Welcome(Client c)
        {
            SendChat(c, "channel.en__1@conference." + HostName(c),
                     "Welcome to " + _cfg.ServerName + "! Online races and the garage are handled by this server.");
        }

        private void SendChat(Client c, string from, string text)
        {
            Write(c, "<message from='" + Util.EscapeXml(from) + "' to='nfsw." + c.PersonaId + "@" + HostName(c) +
                     "/EA-Chat' type='groupchat'><body>" + Util.EscapeXml(text) + "</body></message>");
        }

        /// <summary>Announcement from the admin board / console to every connected client.</summary>
        public int Broadcast(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            List<Client> copy;
            lock (_clientsLock) copy = new List<Client>(_clients);
            int sent = 0;
            for (int i = 0; i < copy.Count; i++)
            {
                try { SendChat(copy[i], "channel.en__1@conference." + HostName(copy[i]), text); sent++; }
                catch { }
            }
            Log.Info("CHAT", "broadcast to " + sent + " client(s): " + Util.Truncate(text, 120));
            return sent;
        }

        public void Kick(long personaId, string reason)
        {
            string wanted = personaId.ToString(CultureInfo.InvariantCulture);
            List<Client> copy;
            lock (_clientsLock) copy = new List<Client>(_clients);
            for (int i = 0; i < copy.Count; i++)
            {
                Client c = copy[i];
                if (!string.Equals(c.PersonaId, wanted, StringComparison.Ordinal)) continue;
                try
                {
                    SendChat(c, "channel.en__1@conference." + HostName(c), "Kicked: " + reason);
                    Write(c, "</stream:stream>");
                }
                catch { }
                CloseQuietly(c);
            }
        }

        private void MarkSession(Client c, bool online)
        {
            try
            {
                long id = Util.ParseLong(c.PersonaId, 0);
                if (id <= 0) return;
                PlayerSession s = _app.Sessions.FindByPersona(id);
                if (s != null) s.Xmpp = online;
            }
            catch { }
        }

        // ------------------------------------------------------------- plumbing

        private string HostName(Client c)
        {
            if (!string.IsNullOrEmpty(_cfg.XmppHost)) return _cfg.XmppHost;
            try
            {
                IPEndPoint ep = c.Tcp.Client.LocalEndPoint as IPEndPoint;
                return ep == null ? "localhost" : ep.Address.ToString();
            }
            catch { return "localhost"; }
        }

        private static string NewStreamId()
        {
            return Util.ShortToken(6).ToUpperInvariant();
        }

        private static string Attribute(string xml, string name)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            int i = xml.IndexOf(name + "='", StringComparison.OrdinalIgnoreCase);
            char quote = '\'';
            if (i < 0) { i = xml.IndexOf(name + "=\"", StringComparison.OrdinalIgnoreCase); quote = '"'; }
            if (i < 0) return null;
            int start = i + name.Length + 2;
            int end = xml.IndexOf(quote, start);
            if (start <= 0 || end < start) return null;
            return xml.Substring(start, end - start);
        }

        private static Stream In(Client c)
        {
            return c.Ssl != null ? (Stream)c.Ssl : (Stream)c.Raw;
        }

        private static string ReadSome(Stream stream)
        {
            byte[] buf = new byte[8192];
            int n = stream.Read(buf, 0, buf.Length);
            if (n <= 0) return null;
            return Encoding.UTF8.GetString(buf, 0, n);
        }

        private void Write(Client c, string data)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(data);
            lock (c.WriteLock)
            {
                try
                {
                    Stream s = In(c);
                    if (s == null) return;
                    s.Write(bytes, 0, bytes.Length);
                    s.Flush();
                    if (_cfg.LogLevel >= LogLevel.Verbose)
                        Log.Verbose("XMPP", ">[" + (c.PersonaId ?? "?") + "] " + Util.Truncate(data, 300));
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private static void Close(Client c)
        {
            try { if (c.Ssl != null) c.Ssl.Close(); } catch { }
            try { if (c.Raw != null) c.Raw.Close(); } catch { }
            try { if (c.Tcp != null) c.Tcp.Close(); } catch { }
        }

        private void CloseQuietly(Client c)
        {
            Close(c);
        }

        /// <summary>Drops clients that stopped talking. Called by the supervisor.</summary>
        public int Sweep()
        {
            if (_cfg.XmppIdleSeconds <= 0) return 0;
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-_cfg.XmppIdleSeconds);
            List<Client> dead = new List<Client>();
            lock (_clientsLock)
            {
                for (int i = 0; i < _clients.Count; i++)
                    if (_clients[i].LastSeenUtc < cutoff) dead.Add(_clients[i]);
                for (int i = 0; i < dead.Count; i++) _clients.Remove(dead[i]);
            }
            for (int i = 0; i < dead.Count; i++)
            {
                Log.Info("XMPP", "idle timeout for persona " + dead[i].PersonaId);
                if (dead[i].LoggedIn) MarkSession(dead[i], false);
                Close(dead[i]);
            }
            return dead.Count;
        }
    }
}