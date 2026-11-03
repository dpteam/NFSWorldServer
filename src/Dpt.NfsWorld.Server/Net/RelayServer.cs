using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Dpt.NfsWorld.Core;
using Dpt.NfsWorld.Engine;

namespace Dpt.NfsWorld.Net
{
    /// <summary>
    /// UDP relay for NAT hairpin / CGNAT. Two clients behind the same router cannot
    /// reach each other's public endpoint, so both register with this relay and the
    /// server forwards datagrams inside a room.
    ///
    /// Wire format (deliberately tiny, the real game payload is opaque):
    ///   "NFSR" | type(1) | body
    ///   type 1 REGISTER   body = personaId "|" room
    ///   type 2 DATA       body = raw game datagram (also accepted without header
    ///                     from already registered endpoints)
    ///   type 3 KEEPALIVE  body = empty
    ///   type 4 LEAVE      body = empty
    ///   type 5 PING       body = empty  -> 0x85 PONG
    /// Server -> client: 0x81 ACK (body = room), 0x82 DATA (body = raw datagram), 0x85 PONG.
    /// </summary>
    public sealed class RelayServer
    {
        private const byte TypeRegister = 1;
        private const byte TypeData = 2;
        private const byte TypeKeepAlive = 3;
        private const byte TypeLeave = 4;
        private const byte TypePing = 5;
        private const byte TypeAck = 0x81;
        private const byte TypeDataOut = 0x82;
        private const byte TypePong = 0x85;
        private static readonly byte[] Magic = new byte[] { (byte)'N', (byte)'F', (byte)'S', (byte)'R' };

        private sealed class Member
        {
            public IPEndPoint Endpoint;
            public string PersonaId = "";
            public string Room = "";
            public DateTime JoinedUtc = DateTime.UtcNow;
            public DateTime LastSeenUtc = DateTime.UtcNow;
            public long InBytes;
            public long OutBytes;
            public long Packets;
            public bool HairpinHinted;
        }

        private sealed class Room
        {
            public string Name = "";
            public readonly List<Member> Members = new List<Member>();
            public DateTime CreatedUtc = DateTime.UtcNow;
            public long PacketsIn;
            public long PacketsOut;
            public long BytesIn;
            public long BytesOut;
        }

        private readonly ServerApp _app;
        private readonly Config _cfg;
        private readonly object _sync = new object();
        private readonly Dictionary<string, Member> _byEndpoint = new Dictionary<string, Member>(StringComparer.Ordinal);
        private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _tickets = new Dictionary<string, string>(StringComparer.Ordinal);

        private UdpClient _udp;
        private Thread _loop;
        private volatile bool _running;
        private int _port;
        private long _packetsIn;
        private long _packetsOut;
        private long _bytesIn;
        private long _bytesOut;
        private long _dropped;
        private DateTime _lastDropLog = DateTime.MinValue;

        public event EventHandler Failed;

        public RelayServer(ServerApp app)
        {
            _app = app;
            _cfg = app.Cfg;
        }

        public int Port { get { return _port; } }
        public bool IsRunning { get { return _running && _loop != null && _loop.IsAlive; } }
        public long PacketsIn { get { return Interlocked.Read(ref _packetsIn); } }
        public long PacketsOut { get { return Interlocked.Read(ref _packetsOut); } }
        public long BytesIn { get { return Interlocked.Read(ref _bytesIn); } }
        public long BytesOut { get { return Interlocked.Read(ref _bytesOut); } }
        public long Dropped { get { return Interlocked.Read(ref _dropped); } }

        public string StatusLine()
        {
            return "relay:" + _port + " rooms=" + RoomCount + " clients=" + ClientCount +
                   " in=" + Util.Bytes(BytesIn) + " out=" + Util.Bytes(BytesOut);
        }

        /// <summary>Issues a relay ticket for a persona (crypto/relaycryptoticket).</summary>
        public string CreateTicket(long personaId)
        {
            string ticket = Util.UrlToken(18);
            lock (_sync)
            {
                if (_tickets.Count > 4096) _tickets.Clear();
                _tickets[ticket] = personaId.ToString(CultureInfo.InvariantCulture);
            }
            Log.Proto("RELAY", "ticket issued to persona " + personaId + " (" + Util.Truncate(ticket, 10) + "...)");
            return ticket;
        }

        public string FindTicketOwner(string ticket)
        {
            if (string.IsNullOrEmpty(ticket)) return null;
            lock (_sync)
            {
                string owner;
                return _tickets.TryGetValue(ticket, out owner) ? owner : null;
            }
        }

        public int RoomCount { get { lock (_sync) return _rooms.Count; } }
        public int ClientCount { get { lock (_sync) return _byEndpoint.Count; } }

        public bool Start()
        {
            if (_running) return true;
            int[] ports = _cfg.PortsWithFallbacks(_cfg.RelayPort, _cfg.RelayPortFallbacks);
            IPAddress bind = IPAddress.Any;
            if (!string.IsNullOrEmpty(_cfg.RelayBind) && _cfg.RelayBind != "0.0.0.0" && _cfg.RelayBind != "*")
            {
                IPAddress parsed;
                if (IPAddress.TryParse(_cfg.RelayBind, out parsed)) bind = parsed;
            }

            Exception last = null;
            for (int i = 0; i < ports.Length; i++)
            {
                try
                {
                    UdpClient udp = new UdpClient(new IPEndPoint(bind, ports[i]));
                    try { udp.Client.ReceiveBufferSize = 1024 * 1024; } catch { }
                    try { udp.Client.SendBufferSize = 1024 * 1024; } catch { }
                    _udp = udp;
                    _port = ports[i];
                    _running = true;
                    _loop = new Thread(ReceiveLoop);
                    _loop.IsBackground = true;
                    _loop.Name = "relay-udp";
                    _loop.Start();
                    Log.Info("RELAY", "UDP relay listening on " + bind + ":" + _port +
                                      " (rooms<= " + _cfg.RelayMaxRooms + ", clients<= " + _cfg.RelayMaxClients + ")");
                    WarnAboutGateway();
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { if (_udp != null) _udp.Close(); } catch { }
                    _udp = null;
                    Log.Warn("RELAY", "cannot bind udp " + bind + ":" + ports[i] + ": " + ex.Message);
                }
            }
            Log.Error("RELAY", "failed to start: " + (last == null ? "?" : last.Message));
            return false;
        }

        private void WarnAboutGateway()
        {
            string gw = Util.DefaultGateway();
            if (!string.IsNullOrEmpty(gw))
                Log.Info("RELAY", "default gateway is " + gw + " - clients behind this NAT will be flagged as hairpin");
        }

        public bool Restart()
        {
            Log.Warn("RELAY", "restarting listener");
            try { Stop(); } catch { }
            Thread.Sleep(300);
            return Start();
        }

        public void Stop()
        {
            bool was = _running;
            _running = false;
            try { if (_udp != null) _udp.Close(); } catch { }
            _udp = null;
            try { if (_loop != null && _loop.IsAlive) _loop.Join(2000); } catch { }
            _loop = null;
            lock (_sync) { _rooms.Clear(); _byEndpoint.Clear(); }
            if (was) Log.Info("RELAY", "stopped");
        }

        private void ReceiveLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int failures = 0;
            while (_running)
            {
                byte[] data;
                try
                {
                    data = _udp.Receive(ref remote);
                    failures = 0;
                }
                catch (SocketException sex)
                {
                    if (!_running) break;
                    failures++;
                    Log.Warn("RELAY", "receive failed (" + sex.SocketErrorCode + "): " + sex.Message);
                    if (failures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(Math.Min(2000, 50 * failures));
                    continue;
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (!_running) break;
                    failures++;
                    Log.Warn("RELAY", "receive error: " + ex.Message);
                    if (failures >= 25) { RaiseFailed(); break; }
                    Thread.Sleep(100);
                    continue;
                }

                try { HandlePacket(data, remote); }
                catch (Exception ex)
                {
                    Log.Error("RELAY", "packet from " + remote + " failed: " + ex.Message);
                }
            }
            if (_running) Log.Warn("RELAY", "receive loop stopped unexpectedly");
        }

        private void RaiseFailed()
        {
            try { EventHandler h = Failed; if (h != null) h(this, EventArgs.Empty); } catch { }
        }

        private void HandlePacket(byte[] data, IPEndPoint remote)
        {
            Interlocked.Increment(ref _packetsIn);
            Interlocked.Add(ref _bytesIn, data.Length);

            if (data.Length > _cfg.RelayMaxPacketBytes)
            {
                Drop("packet " + data.Length + "B from " + remote + " exceeds relay.maxPacketBytes", data, remote);
                return;
            }

            bool hasMagic = data.Length >= 5 && data[0] == Magic[0] && data[1] == Magic[1] && data[2] == Magic[2] && data[3] == Magic[3];
            if (hasMagic)
            {
                byte type = data[4];
                byte[] body = new byte[data.Length - 5];
                Buffer.BlockCopy(data, 5, body, 0, body.Length);
                switch (type)
                {
                    case TypeRegister: Register(remote, body); return;
                    case TypeData: Forward(remote, body, "data"); return;
                    case TypeKeepAlive: Touch(remote); return;
                    case TypeLeave: Leave(remote); return;
                    case TypePing: Send(remote, TypePong, new byte[0]); return;
                    default:
                        Drop("unknown relay opcode " + type + " from " + remote, data, remote);
                        return;
                }
            }

            // headerless datagram: only accepted from registered endpoints (lobby traffic)
            Forward(remote, data, "raw");
        }

        private void Register(IPEndPoint remote, byte[] body)
        {
            string text = Encoding.UTF8.GetString(body);
            string personaId = text;
            string room = "freeroam";
            int sep = text.IndexOf('|');
            if (sep >= 0)
            {
                personaId = text.Substring(0, sep);
                if (sep + 1 < text.Length && text.Length - sep - 1 > 0) room = text.Substring(sep + 1);
            }
            personaId = personaId.Trim();
            room = room.Trim();
            if (room.Length == 0) room = "freeroam";
            if (room.Length > 64) room = room.Substring(0, 64);
            if (personaId.Length > 32) personaId = personaId.Substring(0, 32);

            lock (_sync)
            {
                string key = remote.ToString();
                Member existing;
                if (_byEndpoint.TryGetValue(key, out existing))
                {
                    if (!string.Equals(existing.Room, room, StringComparison.OrdinalIgnoreCase))
                    {
                        Room old;
                        if (_rooms.TryGetValue(existing.Room, out old)) old.Members.Remove(existing);
                        existing.Room = room;
                        Room joined = GetOrCreateRoomLocked(room);
                        joined.Members.Add(existing);
                    }
                    existing.LastSeenUtc = DateTime.UtcNow;
                    existing.PersonaId = personaId;
                }
                else
                {
                    if (_byEndpoint.Count >= _cfg.RelayMaxClients)
                    {
                        Log.Warn("RELAY", "client limit reached, rejecting " + remote);
                        return;
                    }
                    Room target = GetOrCreateRoomLocked(room);
                    if (target.Members.Count >= _cfg.RelayMaxClientsPerRoom)
                    {
                        Log.Warn("RELAY", "room '" + room + "' is full, rejecting " + remote);
                        return;
                    }
                    Member m = new Member();
                    m.Endpoint = remote;
                    m.PersonaId = personaId;
                    m.Room = room;
                    _byEndpoint[key] = m;
                    target.Members.Add(m);
                    Log.Info("RELAY", "registered " + EndpointText(m) + " persona=" + personaId + " room='" + room +
                                      "' (" + target.Members.Count + " in room)");
                    CheckHairpinLocked(target);
                }
            }

            PlayerSession s = null;
            long pid = Util.ParseLong(personaId, 0);
            if (pid > 0) s = _app.Sessions.FindByPersona(pid);
            if (s != null)
            {
                s.Relay = true;
                s.RelayEndpoint = remote.ToString();
            }

            byte[] ack = Encoding.UTF8.GetBytes(room);
            Send(remote, TypeAck, ack);
        }

        private Room GetOrCreateRoomLocked(string room)
        {
            Room r;
            if (_rooms.TryGetValue(room, out r)) return r;
            if (_rooms.Count >= _cfg.RelayMaxRooms)
            {
                // recycle the oldest empty room instead of growing forever
                string oldest = null;
                DateTime t = DateTime.MaxValue;
                foreach (KeyValuePair<string, Room> kv in _rooms)
                    if (kv.Value.Members.Count == 0 && kv.Value.CreatedUtc < t) { t = kv.Value.CreatedUtc; oldest = kv.Key; }
                if (oldest != null) _rooms.Remove(oldest);
            }
            r = new Room();
            r.Name = room;
            _rooms[room] = r;
            Log.Debug("RELAY", "room created: '" + room + "'");
            return r;
        }

        /// <summary>Two endpoints behind the same public IP = the classic hairpin case.</summary>
        private void CheckHairpinLocked(Room room)
        {
            for (int i = 0; i < room.Members.Count; i++)
            {
                Member m = room.Members[i];
                if (m.HairpinHinted) continue;
                for (int j = 0; j < room.Members.Count; j++)
                {
                    if (i == j) continue;
                    if (!string.Equals(room.Members[j].Endpoint.Address.ToString(), m.Endpoint.Address.ToString(), StringComparison.Ordinal)) continue;
                    m.HairpinHinted = true;
                    room.Members[j].HairpinHinted = true;
                    Log.Warn("RELAY", "hairpin: " + EndpointText(m) + " and " + EndpointText(room.Members[j]) +
                                      " share public IP " + m.Endpoint.Address + " (same NAT) - relay is doing its job");
                    break;
                }
            }
        }

        private void Touch(IPEndPoint remote)
        {
            lock (_sync)
            {
                Member m;
                if (_byEndpoint.TryGetValue(remote.ToString(), out m)) m.LastSeenUtc = DateTime.UtcNow;
            }
        }

        private void Leave(IPEndPoint remote)
        {
            lock (_sync)
            {
                string key = remote.ToString();
                Member m;
                if (!_byEndpoint.TryGetValue(key, out m)) return;
                _byEndpoint.Remove(key);
                Room r;
                if (_rooms.TryGetValue(m.Room, out r))
                {
                    r.Members.Remove(m);
                    if (r.Members.Count == 0 && !string.Equals(r.Name, "freeroam", StringComparison.OrdinalIgnoreCase))
                        _rooms.Remove(r.Name);
                }
                Log.Info("RELAY", "left " + EndpointText(m) + " persona=" + m.PersonaId);
            }
            ClearSessionFlag(remote);
        }

        private void Forward(IPEndPoint remote, byte[] payload, string kind)
        {
            if (payload == null || payload.Length == 0) return;
            List<IPEndPoint> targets = null;
            Member sender = null;
            lock (_sync)
            {
                string key = remote.ToString();
                if (!_byEndpoint.TryGetValue(key, out sender))
                {
                    // cannot route an unregistered endpoint
                    Drop("datagram from unregistered " + remote + " (" + kind + ", " + payload.Length + "B)", null, remote);
                    return;
                }
                sender.LastSeenUtc = DateTime.UtcNow;
                sender.InBytes += payload.Length;
                sender.Packets++;
                Room room = GetRoomLocked(sender.Room);
                if (room == null) return;
                room.PacketsIn++;
                room.BytesIn += payload.Length;
                targets = new List<IPEndPoint>(room.Members.Count);
                for (int i = 0; i < room.Members.Count; i++)
                {
                    Member m = room.Members[i];
                    if (m == sender) continue;
                    targets.Add(m.Endpoint);
                    m.OutBytes += payload.Length;
                }
                room.PacketsOut += targets.Count;
                room.BytesOut += (long)payload.Length * targets.Count;
            }

            if (_cfg.ProtocolTrace)
                Log.Proto("RELAY", EndpointText(sender) + " -> " + targets.Count + " peer(s) " + payload.Length + "B: " +
                                    Util.Hex(payload, 48));

            for (int i = 0; i < targets.Count; i++)
            {
                Send(targets[i], TypeDataOut, payload);
                Interlocked.Increment(ref _packetsOut);
                Interlocked.Add(ref _bytesOut, payload.Length + 5);
            }
        }

        private Room GetRoomLocked(string name)
        {
            Room r;
            return _rooms.TryGetValue(name, out r) ? r : null;
        }

        private void Send(IPEndPoint to, byte type, byte[] body)
        {
            try
            {
                if (_udp == null) return;
                byte[] packet = new byte[5 + (body == null ? 0 : body.Length)];
                Buffer.BlockCopy(Magic, 0, packet, 0, 4);
                packet[4] = type;
                if (body != null && body.Length > 0) Buffer.BlockCopy(body, 0, packet, 5, body.Length);
                _udp.Send(packet, packet.Length, to);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Log.Debug("RELAY", "send to " + to + " failed: " + ex.Message); }
        }

        private void Drop(string reason, byte[] data, IPEndPoint remote)
        {
            Interlocked.Increment(ref _dropped);
            if ((DateTime.UtcNow - _lastDropLog).TotalSeconds < 60) return;
            _lastDropLog = DateTime.UtcNow;
            Log.Debug("RELAY", "drop: " + reason + (data == null ? "" : " | hex " + Util.Hex(data, 32)));
        }

        private void ClearSessionFlag(IPEndPoint remote)
        {
            string ep = remote.ToString();
            List<PlayerSession> sessions = _app.Sessions.Online();
            for (int i = 0; i < sessions.Count; i++)
                if (string.Equals(sessions[i].RelayEndpoint, ep, StringComparison.Ordinal))
                {
                    sessions[i].Relay = false;
                    sessions[i].RelayEndpoint = "";
                }
        }

        private static string EndpointText(Member m)
        {
            return m == null ? "?" : (m.Endpoint + "(" + (string.IsNullOrEmpty(m.PersonaId) ? "?" : m.PersonaId) + ")");
        }

        /// <summary>Drops quiet endpoints and empty rooms. Called by the supervisor.</summary>
        public int Sweep()
        {
            if (_cfg.RelayIdleSeconds <= 0) return 0;
            DateTime idleCutoff = DateTime.UtcNow.AddSeconds(-_cfg.RelayIdleSeconds);
            DateTime lifeCutoff = DateTime.UtcNow.AddSeconds(-_cfg.RelayLifetimeSeconds);
            List<Member> dead = new List<Member>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, Member> kv in _byEndpoint)
                    if (kv.Value.LastSeenUtc < idleCutoff || kv.Value.JoinedUtc < lifeCutoff) dead.Add(kv.Value);
                for (int i = 0; i < dead.Count; i++)
                {
                    Member m = dead[i];
                    _byEndpoint.Remove(m.Endpoint.ToString());
                    Room r;
                    if (_rooms.TryGetValue(m.Room, out r)) r.Members.Remove(m);
                }
                List<string> empty = new List<string>();
                foreach (KeyValuePair<string, Room> kv in _rooms)
                    if (kv.Value.Members.Count == 0 && kv.Value.CreatedUtc < DateTime.UtcNow.AddMinutes(-5)) empty.Add(kv.Key);
                for (int i = 0; i < empty.Count; i++) _rooms.Remove(empty[i]);
            }
            for (int i = 0; i < dead.Count; i++)
                Log.Info("RELAY", "idle " + EndpointText(dead[i]) + " removed from room '" + dead[i].Room + "'");
            return dead.Count;
        }

        /// <summary>Snapshot for the web boards (no raw payloads).</summary>
        public List<Dictionary<string, object>> RoomSnapshot()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, Room> kv in _rooms)
                {
                    Room r = kv.Value;
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["name"] = r.Name;
                    d["clients"] = (long)r.Members.Count;
                    d["packetsIn"] = r.PacketsIn;
                    d["packetsOut"] = r.PacketsOut;
                    d["bytesIn"] = r.BytesIn;
                    d["bytesOut"] = r.BytesOut;
                    List<object> members = new List<object>();
                    for (int i = 0; i < r.Members.Count; i++)
                    {
                        Member m = r.Members[i];
                        Dictionary<string, object> md = new Dictionary<string, object>();
                        md["personaId"] = m.PersonaId;
                        md["endpoint"] = m.Endpoint.ToString();
                        md["hairpin"] = m.HairpinHinted;
                        md["lastSeenUtc"] = Util.Iso(m.LastSeenUtc);
                        md["inBytes"] = m.InBytes;
                        md["outBytes"] = m.OutBytes;
                        members.Add(md);
                    }
                    d["members"] = members;
                    list.Add(d);
                }
            }
            return list;
        }
    }
}