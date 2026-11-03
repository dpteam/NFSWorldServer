using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dpt.NfsWorld.Core;
using Dpt.NfsWorld.Engine;

namespace Dpt.NfsWorld.Web
{
    /// <summary>
    /// JSON API for both boards. The public role never reads private fields at all,
    /// so there is no "hidden in CSS" leak.
    /// </summary>
    public sealed class WebApi
    {
        private readonly ServerApp _app;
        private readonly Config _cfg;
        private readonly WebServer _web;

        public WebApi(ServerApp app, WebServer web)
        {
            _app = app;
            _cfg = app.Cfg;
            _web = web;
        }

        public void Handle(HttpRequest req, HttpResponse resp, string path, string clientIp)
        {
            string p = path.ToLowerInvariant();
            string method = req.Method ?? "GET";

            // ---------------------------------------------------------- public
            if (p == "/api/health")
            {
                JsonBuilder j = new JsonBuilder(256);
                j.BeginObject().Prop("ok", true).Prop("name", ServerApp.Name).Prop("version", ServerApp.Version)
                 .Prop("uptime", _app.UptimeText).Prop("role", _web.RoleName).Prop("time", Util.Iso(DateTime.UtcNow))
                 .EndObject();
                _web.SendJson(req, resp, 200, j.ToString());
                return;
            }
            if (p == "/api/status")
            {
                _web.SendJson(req, resp, 200, StatusJson(_web.IsAdmin));
                return;
            }
            if (p == "/api/players")
            {
                _web.SendJson(req, resp, 200, PlayersJson(_web.IsAdmin));
                return;
            }
            if (p == "/api/feed")
            {
                long since = Util.ParseLong(req.QueryString("since"), 0);
                _web.SendJson(req, resp, 200, FeedJson(since, 80));
                return;
            }
            if (p == "/api/profiles")
            {
                _web.SendJson(req, resp, 200, ProfilesJson());
                return;
            }
            if (p == "/api/relay")
            {
                _web.SendJson(req, resp, 200, RelayJson(_web.IsAdmin));
                return;
            }
            if (p == "/api/accounts")
            {
                if (!_web.IsAdmin) { _web.SendJson(req, resp, 403, "{\"ok\":false,\"error\":\"admin only\"}"); return; }
            }

            // ----------------------------------------------------------- admin
            if (p.StartsWith("/api/admin/", StringComparison.Ordinal))
            {
                if (!_web.IsAdmin)
                {
                    _web.SendJson(req, resp, 403, "{\"ok\":false,\"error\":\"admin board only\"}");
                    return;
                }
                HttpResponse authError = _web.CheckAdminToken(req, clientIp);
                if (authError != null)
                {
                    resp.Status = authError.Status;
                    resp.ContentType = authError.ContentType;
                    resp.Body = authError.Body;
                    foreach (KeyValuePair<string, string> kv in authError.Headers) resp.Set(kv.Key, kv.Value);
                    return;
                }

                string action = p.Substring("/api/admin/".Length);
                if (action == "session") { _web.SendJson(req, resp, 200, "{\"ok\":true}"); return; }
                if (action == "status") { _web.SendJson(req, resp, 200, StatusJson(true)); return; }
                if (action == "players") { _web.SendJson(req, resp, 200, PlayersJson(true)); return; }
                if (action == "logs") { _web.SendJson(req, resp, 200, LogsJson(req)); return; }
                if (action == "relay") { _web.SendJson(req, resp, 200, RelayJson(true)); return; }
                if (action == "accounts") { _web.SendJson(req, resp, 200, AccountsJson()); return; }
                if (action == "kick") { Kick(req, resp); return; }
                if (action == "announce") { Announce(req, resp); return; }
                if (action == "account") { AccountAction(req, resp); return; }
                if (action == "reload") { ProfilesReload(req, resp); return; }
                if (action == "save") { _app.Db.Save(); _web.SendJson(req, resp, 200, "{\"ok\":true}"); return; }
                if (action == "shutdown")
                {
                    _web.SendJson(req, resp, 200, "{\"ok\":true,\"message\":\"shutting down\"}");
                    Log.Warn("WEB", "shutdown requested from admin board");
                    return;
                }

                _web.SendJson(req, resp, 404, "{\"ok\":false,\"error\":\"unknown admin action\"}");
                return;
            }

            if (p == "/api/accounts")
            {
                _web.SendJson(req, resp, 200, AccountsJson());
                return;
            }

            _web.SendJson(req, resp, 404, "{\"ok\":false,\"error\":\"unknown endpoint\"}");
        }

        // ------------------------------------------------------------ documents

        private string StatusJson(bool admin)
        {
            List<PlayerSession> online = _app.Sessions.Online();
            List<Persona> personas = _app.Db.AllPersonas();
            JsonBuilder j = new JsonBuilder(1024);
            j.BeginObject();
            j.Prop("ok", true);
            j.Prop("name", _cfg.WebServerName);
            j.Prop("description", _cfg.ServerDescription);
            j.Prop("location", _cfg.ServerLocation);
            j.Prop("version", ServerApp.Version);
            j.Prop("buildDate", ServerApp.BuildDate);
            j.Prop("uptime", _app.UptimeText);
            j.Prop("startedUtc", Util.Iso(_app.StartedUtc));
            j.Prop("now", Util.Iso(DateTime.UtcNow));
            j.Prop("role", _web.RoleName);

            j.Name("services").BeginObject();
            j.Prop("engine", _app.Engine != null && _app.Engine.IsRunning);
            j.Prop("enginePort", _app.Engine == null ? 0 : (long)_app.Engine.Port);
            j.Prop("xmpp", _app.Xmpp != null && _app.Xmpp.IsRunning);
            j.Prop("xmppPort", _app.Xmpp == null ? 0 : (long)_app.Xmpp.Port);
            j.Prop("relay", _app.Relay != null && _app.Relay.IsRunning);
            j.Prop("relayPort", _app.Relay == null ? 0 : (long)_app.Relay.Port);
            j.Prop("webPublic", _app.WebPublic != null && _app.WebPublic.IsRunning);
            j.Prop("webAdmin", _app.WebAdmin != null && _app.WebAdmin.IsRunning);
            j.EndObject();

            j.Name("counts").BeginObject();
            j.Prop("online", (long)online.Count);
            j.Prop("sessions", (long)_app.Sessions.Count);
            j.Prop("accounts", (long)_app.Db.AccountCount);
            j.Prop("personas", (long)personas.Count);
            j.Prop("cars", (long)_app.Db.CarCount);
            j.Prop("rooms", _app.Relay == null ? 0 : (long)_app.Relay.RoomCount);
            j.Prop("relayClients", _app.Relay == null ? 0 : (long)_app.Relay.ClientCount);
            j.Prop("xmppClients", _app.Xmpp == null ? 0 : (long)_app.Xmpp.Clients);
            j.Prop("requests", _app.TotalRequests);
            j.EndObject();

            j.Name("traffic").BeginObject();
            if (_app.Relay != null)
            {
                j.Prop("relayIn", _app.Relay.BytesIn);
                j.Prop("relayOut", _app.Relay.BytesOut);
                j.Prop("relayPacketsIn", _app.Relay.PacketsIn);
                j.Prop("relayPacketsOut", _app.Relay.PacketsOut);
                j.Prop("relayDropped", _app.Relay.Dropped);
            }
            else
            {
                j.Prop("relayIn", 0L).Prop("relayOut", 0L).Prop("relayPacketsIn", 0L).Prop("relayPacketsOut", 0L).Prop("relayDropped", 0L);
            }
            j.EndObject();

            j.Name("profiles").Raw(ProfilesJson());
            j.Prop("defaultProfile", _app.Profiles.Fallback == null ? "" : _app.Profiles.Fallback.Id);
            j.Prop("publicUrl", _cfg.PublicUrl);
            j.Prop("refreshMs", (long)_cfg.RefreshMs);
            j.Prop("defaultTheme", _cfg.DefaultTheme);
            j.Prop("defaultSound", _cfg.DefaultSound);
            if (admin)
            {
                j.Name("server").BeginObject();
                j.Prop("path", _cfg.BaseDir);
                j.Prop("logDir", _cfg.LogDir);
                j.Prop("stateDir", _cfg.StatePath);
                j.Prop("workingSet", _app.WorkingSetBytes);
                j.Prop("engineRequests", _app.Engine == null ? 0 : _app.Engine.Requests);
                j.Prop("engineErrors", _app.Engine == null ? 0 : _app.Engine.Errors);
                j.Prop("xmppClients", _app.Xmpp == null ? 0 : (long)_app.Xmpp.Clients);
                j.EndObject();
            }
            j.EndObject();
            return j.ToString();
        }

        private string PlayersJson(bool admin)
        {
            List<PlayerSession> online = _app.Sessions.Online();
            JsonBuilder j = new JsonBuilder(1024);
            j.BeginObject().Prop("ok", true).Name("players").BeginArray();
            for (int i = 0; i < online.Count; i++)
            {
                PlayerSession s = online[i];
                Persona p = _app.Db.FindPersona(s.PersonaId);
                j.BeginObject();
                j.Prop("personaId", s.PersonaId);
                j.Prop("name", s.PersonaName);
                j.Prop("level", p == null ? 0 : (long)p.Level);
                j.Prop("rep", p == null ? 0L : p.Rep);
                j.Prop("cash", p == null ? 0L : p.Cash);
                j.Prop("car", p == null || p.CurrentCar == null ? "" : p.CurrentCar.Name);
                j.Prop("onlineMinutes", (long)Math.Max(0, (DateTime.UtcNow - s.CreatedUtc).TotalMinutes));
                j.Prop("idleSeconds", (long)Math.Max(0, (DateTime.UtcNow - s.LastSeenUtc).TotalSeconds));
                j.Prop("xmpp", s.Xmpp);
                j.Prop("relay", s.Relay);
                j.Prop("profile", s.ProfileId);
                j.Prop("build", s.ClientBuild);
                if (admin)
                {
                    j.Prop("accountId", s.AccountId);
                    j.Prop("ip", s.Ip);
                    j.Prop("machineId", s.MachineId);
                    j.Prop("requests", s.Requests);
                    j.Prop("token", SessionManager.Short(s.Token));
                    j.Prop("relayEndpoint", s.RelayEndpoint);
                    j.Prop("relayTicket", SessionManager.Short(s.RelayTicket));
                    j.Prop("localIp", s.LocalIp);
                    j.Prop("localPort", (long)s.LocalPort);
                    j.Prop("publicIp", s.PublicIp);
                    j.Prop("publicPort", (long)s.PublicPort);
                    j.Prop("natType", (long)s.NatType);
                    j.Prop("natBehavior", (long)s.NatBehavior);
                    j.Prop("userAgent", s.UserAgent);
                }
                j.EndObject();
            }
            j.EndArray().EndObject();
            return j.ToString();
        }

        private string FeedJson(long since, int limit)
        {
            List<FeedItem> items = _app.Events.Tail(since, limit);
            JsonBuilder j = new JsonBuilder(512);
            j.BeginObject().Prop("ok", true).Prop("last", _app.Events.LastId).Name("events").BeginArray();
            for (int i = 0; i < items.Count; i++)
            {
                FeedItem f = items[i];
                j.BeginObject().Prop("id", f.Id).Prop("utc", Util.Iso(f.Utc)).Prop("kind", f.Kind)
                 .Prop("text", f.Text).Prop("persona", f.Persona).EndObject();
            }
            j.EndArray().EndObject();
            return j.ToString();
        }

        private string ProfilesJson()
        {
            List<ClientProfile> all = _app.Profiles.All;
            JsonBuilder j = new JsonBuilder(512);
            j.BeginArray();
            for (int i = 0; i < all.Count; i++)
            {
                ClientProfile p = all[i];
                j.BeginObject().Prop("id", p.Id).Prop("name", p.DisplayName).Prop("build", p.ClientBuild)
                 .Prop("description", p.Description).Prop("personaFormat", p.PersonaFormat)
                 .Prop("default", _app.Profiles.Fallback != null && _app.Profiles.Fallback.Id == p.Id)
                 .EndObject();
            }
            j.EndArray();
            return j.ToString();
        }

        private string RelayJson(bool admin)
        {
            List<Dictionary<string, object>> rooms = _app.Relay == null ? new List<Dictionary<string, object>>() : _app.Relay.RoomSnapshot();
            JsonBuilder j = new JsonBuilder(1024);
            j.BeginObject().Prop("ok", true).Prop("enabled", _app.Relay != null && _app.Relay.IsRunning)
             .Prop("port", _app.Relay == null ? 0 : (long)_app.Relay.Port)
             .Prop("packetsIn", _app.Relay == null ? 0L : _app.Relay.PacketsIn)
             .Prop("packetsOut", _app.Relay == null ? 0L : _app.Relay.PacketsOut)
             .Prop("bytesIn", _app.Relay == null ? 0L : _app.Relay.BytesIn)
             .Prop("bytesOut", _app.Relay == null ? 0L : _app.Relay.BytesOut)
             .Prop("dropped", _app.Relay == null ? 0L : _app.Relay.Dropped)
             .Name("rooms").BeginArray();
            for (int i = 0; i < rooms.Count; i++)
            {
                Dictionary<string, object> d = rooms[i];
                j.BeginObject();
                j.Prop("name", Str(d, "name"));
                j.Prop("clients", Long(d, "clients"));
                j.Prop("packetsIn", Long(d, "packetsIn"));
                j.Prop("packetsOut", Long(d, "packetsOut"));
                j.Prop("bytesIn", Long(d, "bytesIn"));
                j.Prop("bytesOut", Long(d, "bytesOut"));
                if (admin)
                {
                    j.Name("members").BeginArray();
                    List<object> members = d.ContainsKey("members") ? d["members"] as List<object> : null;
                    if (members != null)
                        for (int k = 0; k < members.Count; k++)
                        {
                            Dictionary<string, object> m = members[k] as Dictionary<string, object>;
                            if (m == null) continue;
                            j.BeginObject();
                            j.Prop("personaId", Str(m, "personaId"));
                            j.Prop("endpoint", Str(m, "endpoint"));
                            j.Prop("hairpin", Bool(m, "hairpin"));
                            j.Prop("lastSeenUtc", Str(m, "lastSeenUtc"));
                            j.Prop("inBytes", Long(m, "inBytes"));
                            j.Prop("outBytes", Long(m, "outBytes"));
                            j.EndObject();
                        }
                    j.EndArray();
                }
                j.EndObject();
            }
            j.EndArray().EndObject();
            return j.ToString();
        }

        private string AccountsJson()
        {
            List<Account> accounts = _app.Db.AllAccounts();
            JsonBuilder j = new JsonBuilder(1024);
            j.BeginObject().Prop("ok", true).Name("accounts").BeginArray();
            for (int i = 0; i < accounts.Count; i++)
            {
                Account a = accounts[i];
                List<Persona> personas = _app.Db.PersonasOf(a);
                j.BeginObject().Prop("id", a.Id).Prop("name", a.Username).Prop("banned", a.Banned)
                 .Prop("createdUtc", Util.Iso(a.CreatedUtc)).Prop("lastLoginUtc", Util.Iso(a.LastLoginUtc))
                 .Prop("personaCount", (long)personas.Count).Name("personas").BeginArray();
                for (int k = 0; k < personas.Count; k++)
                {
                    Persona p = personas[k];
                    j.BeginObject().Prop("id", p.Id).Prop("name", p.Name).Prop("level", (long)p.Level)
                     .Prop("rep", p.Rep).Prop("cash", p.Cash).Prop("cars", (long)p.Cars.Count).EndObject();
                }
                j.EndArray().EndObject();
            }
            j.EndArray().EndObject();
            return j.ToString();
        }

        private string LogsJson(HttpRequest req)
        {
            if (!_cfg.ExposeLogs)
                return "{\"ok\":false,\"error\":\"log access disabled (web.exposeLogs=false)\"}";
            long since = Util.ParseLong(req.QueryString("since"), 0);
            int limit = Util.ParseInt(req.QueryString("limit"), 200);
            string level = req.QueryString("level");
            string cat = req.QueryString("cat");
            LogLevel? min = null;
            if (!string.IsNullOrEmpty(level)) min = Config.ParseLevel(level);
            List<LogEntry> entries = Log.Tail(since, limit, min, cat);
            JsonBuilder j = new JsonBuilder(2048);
            j.BeginObject().Prop("ok", true).Prop("last", Log.LastSeq).Name("lines").BeginArray();
            for (int i = 0; i < entries.Count; i++)
            {
                LogEntry e = entries[i];
                j.BeginObject().Prop("seq", e.Seq).Prop("utc", Util.Iso(e.Utc)).Prop("level", e.Level.ToString())
                 .Prop("cat", e.Category).Prop("msg", e.Message).EndObject();
            }
            j.EndArray().EndObject();
            return j.ToString();
        }

        // -------------------------------------------------------------- actions

        private void Kick(HttpRequest req, HttpResponse resp)
        {
            Dictionary<string, object> body = Json.TryParseObject(Text(req.Body));
            long personaId = body == null ? 0 : Json.GetLong(body, "personaId", 0);
            string reason = body == null ? "admin kick" : Json.GetString(body, "reason", "admin kick");
            if (personaId <= 0)
            {
                _web.SendJson(req, resp, 400, "{\"ok\":false,\"error\":\"personaId required\"}");
                return;
            }
            PlayerSession s = _app.Sessions.FindByPersona(personaId);
            if (s == null)
            {
                _web.SendJson(req, resp, 404, "{\"ok\":false,\"error\":\"player not online\"}");
                return;
            }
            _app.Sessions.Kick(s, reason);
            if (_app.Xmpp != null) _app.Xmpp.Kick(personaId, reason);
            _app.Events.Add("kick", "kicked from the server", s.PersonaName);
            JsonBuilder j = new JsonBuilder(128);
            j.BeginObject().Prop("ok", true).Prop("personaId", personaId).Prop("reason", reason).EndObject();
            _web.SendJson(req, resp, 200, j.ToString());
        }

        private void Announce(HttpRequest req, HttpResponse resp)
        {
            Dictionary<string, object> body = Json.TryParseObject(Text(req.Body));
            string text = body == null ? null : Json.GetString(body, "text", null);
            if (string.IsNullOrEmpty(text))
            {
                _web.SendJson(req, resp, 400, "{\"ok\":false,\"error\":\"text required\"}");
                return;
            }
            int sent = _app.Xmpp == null ? 0 : _app.Xmpp.Broadcast(text);
            _app.Events.Add("announce", Util.Truncate(text, 160), "admin");
            JsonBuilder j = new JsonBuilder(128);
            j.BeginObject().Prop("ok", true).Prop("sent", (long)sent).EndObject();
            _web.SendJson(req, resp, 200, j.ToString());
        }

        private void AccountAction(HttpRequest req, HttpResponse resp)
        {
            Dictionary<string, object> body = Json.TryParseObject(Text(req.Body));
            if (body == null)
            {
                _web.SendJson(req, resp, 400, "{\"ok\":false,\"error\":\"json body required\"}");
                return;
            }
            long accountId = Json.GetLong(body, "accountId", 0);
            string action = Json.GetString(body, "action", "");

            if (action == "cash")
            {
                long personaId = Json.GetLong(body, "personaId", 0);
                Persona per = _app.Db.FindPersona(personaId);
                if (per == null)
                {
                    _web.SendJson(req, resp, 404, "{\"ok\":false,\"error\":\"persona not found\"}");
                    return;
                }
                _app.Db.AddCash(per, Json.GetLong(body, "delta", 0));
                _app.Db.Save();
                _app.Events.Add("admin", "cash " + Json.GetLong(body, "delta", 0) + " -> " + per.Name, "admin");
                _web.SendJson(req, resp, 200, "{\"ok\":true,\"cash\":" + per.Cash.ToString(CultureInfo.InvariantCulture) + "}");
                return;
            }

            Account a = _app.Db.FindAccountById(accountId);
            if (a == null)
            {
                _web.SendJson(req, resp, 404, "{\"ok\":false,\"error\":\"account not found\"}");
                return;
            }
            if (action == "ban") _app.Db.SetBanned(a, true);
            else if (action == "unban") _app.Db.SetBanned(a, false);
            else if (action == "password") _app.Db.SetPassword(a, Json.GetString(body, "password", ""));
            else
            {
                _web.SendJson(req, resp, 400, "{\"ok\":false,\"error\":\"unknown action\"}");
                return;
            }
            _app.Db.Save();
            _app.Events.Add("admin", "account '" + a.Username + "' -> " + action, "admin");
            _web.SendJson(req, resp, 200, "{\"ok\":true}");
        }

        private void ProfilesReload(HttpRequest req, HttpResponse resp)
        {
            _app.Profiles.Reload();
            EngineXml.ClearCache();
            _app.Events.Add("admin", "client profiles reloaded", "admin");
            _web.SendJson(req, resp, 200, ProfilesJson().Length == 0 ? "{\"ok\":true}" : "{\"ok\":true,\"profiles\":" + ProfilesJson() + "}");
        }

        // -------------------------------------------------------------- helpers

        private static string Text(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            return Encoding.UTF8.GetString(body);
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static long Long(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            if (v is long) return (long)v;
            long r;
            return long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out r) ? r : 0;
        }

        private static bool Bool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            return false;
        }
    }
}