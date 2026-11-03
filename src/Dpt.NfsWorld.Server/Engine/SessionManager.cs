using System;
using System.Collections.Generic;
using System.Text;
using Dpt.NfsWorld.Core;
using Dpt.NfsWorld.Web;

namespace Dpt.NfsWorld.Engine
{
    public sealed class PlayerSession
    {
        public string Token = "";
        public long AccountId;
        public long PersonaId;
        public string PersonaName = "";
        public string MachineId = "";
        public string Ip = "0.0.0.0";
        public string UserAgent = "";
        public string ClientBuild = "";
        public string ProfileId = "";
        public DateTime CreatedUtc = DateTime.UtcNow;
        public DateTime LastSeenUtc = DateTime.UtcNow;
        public long Requests;
        public bool Xmpp;
        public bool Relay;
        public string RelayEndpoint = "";
        public string LocalIp = "";
        public int LocalPort;
        public string PublicIp = "";
        public int PublicPort;
        public int NatType;
        public int NatBehavior;
        public int NatIncrement;
        public string RelayTicket = "";
        public bool Banned;
        public string KickReason = "";

        public string Key
        {
            get { return PersonaId > 0 ? PersonaId.ToString() : ("acct:" + AccountId); }
        }
    }

    /// <summary>
    /// securityToken bookkeeping. The client sends the token in a "securityToken"
    /// header (or a query parameter on old builds), we map it to account + persona.
    /// </summary>
    public sealed class SessionManager
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, PlayerSession> _byToken = new Dictionary<string, PlayerSession>(StringComparer.Ordinal);
        private readonly Dictionary<string, PlayerSession> _byPersona = new Dictionary<string, PlayerSession>(StringComparer.Ordinal);
        private readonly Config _cfg;

        public SessionManager(Config cfg)
        {
            _cfg = cfg;
        }

        public int Count { get { lock (_sync) return _byToken.Count; } }

        public PlayerSession Create(Account account, string ip, string userAgent, string machineId)
        {
            if (account == null) return null;
            PlayerSession s = new PlayerSession();
            s.Token = Guid.NewGuid().ToString(); // reference clients expect a GUID-shaped securityToken
            s.AccountId = account.Id;
            s.Ip = ip ?? "0.0.0.0";
            s.UserAgent = userAgent ?? "";
            s.MachineId = machineId ?? "";
            s.ClientBuild = GuessBuild(s.UserAgent);
            s.CreatedUtc = DateTime.UtcNow;
            s.LastSeenUtc = s.CreatedUtc;
            lock (_sync)
            {
                // same account logging in again replaces the old token
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, PlayerSession> kv in _byToken)
                    if (kv.Value.AccountId == account.Id) stale.Add(kv.Key);
                for (int i = 0; i < stale.Count; i++) RemoveLocked(stale[i]);

                _byToken[s.Token] = s;
            }
            Log.Info("SESSION", "session created for account #" + account.Id + " (" + account.Username + ") ip=" + s.Ip +
                               " build=" + (s.ClientBuild.Length == 0 ? "?" : s.ClientBuild) + " token=" + Short(s.Token));
            return s;
        }

        private static string GuessBuild(string ua)
        {
            if (string.IsNullOrEmpty(ua)) return "";
            string[] known = new string[] { "1930", "1613", "2011", "2010" };
            for (int i = 0; i < known.Length; i++)
                if (ua.IndexOf(known[i], StringComparison.Ordinal) >= 0) return known[i];
            return "";
        }

        public PlayerSession ResolveByToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            PlayerSession s;
            lock (_sync)
            {
                if (!_byToken.TryGetValue(token, out s)) return null;
                if (_cfg.SessionTimeoutSec > 0 &&
                    (DateTime.UtcNow - s.LastSeenUtc).TotalSeconds > _cfg.SessionTimeoutSec)
                {
                    RemoveLocked(token);
                    return null;
                }
                s.LastSeenUtc = DateTime.UtcNow;
                s.Requests++;
                return s;
            }
        }

        /// <summary>Token from the securityToken header, ?securityToken= or the "X-Auth-Token" header.</summary>
        public PlayerSession Resolve(HttpRequest req)
        {
            string token = req.Header("securityToken");
            if (string.IsNullOrEmpty(token)) token = req.Header("SecurityToken");
            if (string.IsNullOrEmpty(token)) token = req.QueryString("securityToken");
            if (string.IsNullOrEmpty(token)) token = req.Header("X-Auth-Token");
            return ResolveByToken(token);
        }

        public bool BindPersona(PlayerSession s, Persona p)
        {
            if (s == null || p == null) return false;
            lock (_sync)
            {
                PlayerSession other;
                if (_byPersona.TryGetValue(p.Id.ToString(), out other) && other != s)
                {
                    // persona logged in from another session: the newer login wins
                    other.PersonaId = 0;
                    other.PersonaName = "";
                }
                s.PersonaId = p.Id;
                s.PersonaName = p.Name;
                s.LastSeenUtc = DateTime.UtcNow;
                _byPersona[p.Id.ToString()] = s;
            }
            Log.Info("SESSION", "persona bound: " + p.Name + " (#" + p.Id + ") token=" + Short(s.Token));
            return true;
        }

        public void SetNatInfo(PlayerSession s, string localIp, int localPort, string publicIp, int publicPort, int natType, int natBehavior, int natIncrement)
        {
            if (s == null) return;
            lock (_sync)
            {
                s.LocalIp = localIp ?? "";
                s.LocalPort = localPort;
                s.PublicIp = publicIp ?? "";
                s.PublicPort = publicPort;
                s.NatType = natType;
                s.NatBehavior = natBehavior;
                s.NatIncrement = natIncrement;
                s.LastSeenUtc = DateTime.UtcNow;
            }
            Log.Info("SESSION", "NAT info: local " + s.LocalIp + ":" + s.LocalPort + " public " + s.PublicIp + ":" + s.PublicPort + " type=" + natType + " behavior=" + natBehavior + " increment=" + natIncrement);
        }

        public void UnbindPersona(PlayerSession s)
        {
            if (s == null) return;
            lock (_sync)
            {
                if (s.PersonaId > 0) _byPersona.Remove(s.PersonaId.ToString());
                s.PersonaId = 0;
                s.PersonaName = "";
            }
        }

        public PlayerSession FindByPersona(long personaId)
        {
            if (personaId <= 0) return null;
            lock (_sync)
            {
                PlayerSession s;
                return _byPersona.TryGetValue(personaId.ToString(), out s) ? s : null;
            }
        }

        public List<PlayerSession> Snapshot()
        {
            List<PlayerSession> list = new List<PlayerSession>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, PlayerSession> kv in _byToken) list.Add(kv.Value);
            }
            return list;
        }

        /// <summary>Live (persona bound, not banned) sessions used by the public board.</summary>
        public List<PlayerSession> Online()
        {
            List<PlayerSession> list = new List<PlayerSession>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, PlayerSession> kv in _byToken)
                {
                    PlayerSession s = kv.Value;
                    if (s.PersonaId <= 0 || s.Banned) continue;
                    if (_cfg.SessionTimeoutSec > 0 &&
                        (DateTime.UtcNow - s.LastSeenUtc).TotalSeconds > _cfg.SessionTimeoutSec) continue;
                    list.Add(s);
                }
            }
            return list;
        }

        public int CountForIp(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return 0;
            int n = 0;
            lock (_sync)
            {
                foreach (KeyValuePair<string, PlayerSession> kv in _byToken)
                    if (string.Equals(kv.Value.Ip, ip, StringComparison.Ordinal)) n++;
            }
            return n;
        }

        public PlayerSession FindByAccount(long accountId)
        {
            lock (_sync)
            {
                foreach (KeyValuePair<string, PlayerSession> kv in _byToken)
                    if (kv.Value.AccountId == accountId) return kv.Value;
                return null;
            }
        }

        public void Kick(PlayerSession s, string reason)
        {
            if (s == null) return;
            lock (_sync)
            {
                s.Banned = true;
                s.KickReason = reason ?? "";
                if (s.PersonaId > 0) _byPersona.Remove(s.PersonaId.ToString());
            }
            Log.Warn("SESSION", "kicked " + (string.IsNullOrEmpty(s.PersonaName) ? ("account #" + s.AccountId) : s.PersonaName) +
                                 " (" + reason + ")");
        }

        public bool Remove(string token)
        {
            lock (_sync) return RemoveLocked(token);
        }

        private bool RemoveLocked(string token)
        {
            PlayerSession s;
            if (!_byToken.TryGetValue(token, out s)) return false;
            _byToken.Remove(token);
            if (s.PersonaId > 0)
            {
                PlayerSession cur;
                if (_byPersona.TryGetValue(s.PersonaId.ToString(), out cur) && cur == s)
                    _byPersona.Remove(s.PersonaId.ToString());
            }
            return true;
        }

        public int Sweep()
        {
            if (_cfg.SessionTimeoutSec <= 0) return 0;
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-_cfg.SessionTimeoutSec);
            List<string> dead = new List<string>();
            lock (_sync)
            {
                foreach (KeyValuePair<string, PlayerSession> kv in _byToken)
                    if (kv.Value.LastSeenUtc < cutoff) dead.Add(kv.Key);
                for (int i = 0; i < dead.Count; i++) RemoveLocked(dead[i]);
            }
            for (int i = 0; i < dead.Count; i++) Log.Info("SESSION", "session expired: " + Short(dead[i]));
            return dead.Count;
        }

        public static string Short(string token)
        {
            if (string.IsNullOrEmpty(token)) return "-";
            return token.Length <= 8 ? token : token.Substring(0, 8) + "...";
        }
    }
}