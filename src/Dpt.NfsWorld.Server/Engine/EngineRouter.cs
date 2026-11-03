using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Dpt.NfsWorld.Core;
using Dpt.NfsWorld.Web;

namespace Dpt.NfsWorld.Engine
{
    /// <summary>
    /// The Engine.svc game API. Stateful endpoints are handled here; everything else
    /// is served from the active client profile, which is how the original SoapBox
    /// servers worked and how builds differ.
    /// </summary>
    public sealed class EngineRouter
    {
        private readonly ServerApp _app;
        private readonly Config _cfg;
        private readonly DataStore _db;
        private readonly SessionManager _sessions;
        private readonly ProfileStore _profiles;
        private readonly Dictionary<string, long> _pendingPuchase = new Dictionary<string, long>();

        public EngineRouter(ServerApp app)
        {
            _app = app;
            _cfg = app.Cfg;
            _db = app.Db;
            _sessions = app.Sessions;
            _profiles = app.Profiles;
        }

        public void Route(HttpRequest req, HttpResponse res)
        {
            string rel = StripEnginePrefix(req.Path);
            string lower = rel.ToLowerInvariant();
            ClientProfile profile = _profiles.Resolve(req.Path, req.Header("User-Agent"));

            try
            {
                // -------------------------------------------------- launcher probe
                // Only the engine port is forwarded through the router, so the
                // launcher asks *this* port whether the server is alive. Same
                // shape as the public board, without a single private field.
                if (lower == "/api/health" || lower == "/api/status")
                {
                    SendJson(res, ProbeJson(lower == "/api/status"));
                    return;
                }

                // ------------------------------------------ protocol oracle mode
                // engine.staticOnly=true: answer exactly like the reference pack,
                // straight from the client profile. Used to find out what a build
                // really wants before the endpoint is ported into code.
                if (_cfg.EngineStaticOnly)
                {
                    if (ServeStatic(req, res, profile, rel)) return;
                    Log.Warn("ENGINE", "staticOnly: no file for " + req.Method + " " + rel +
                                        " (profile " + (profile == null ? "none" : profile.Id) + ")");
                    SendEmpty(res, 200);
                    return;
                }

                // ------------------------------------------------------- account
                if (lower == "/user/authenticateuser" || lower == "/user/authenticate")
                {
                    HandleAuthenticate(req, res);
                    return;
                }
                if (lower == "/user/modernauth")
                {
                    HandleModernAuth(req, res);
                    return;
                }
                if (lower == "/user/createuser" || lower == "/user/modernregister")
                {
                    HandleRegister(req, res, lower);
                    return;
                }
                if (lower == "/user/getpermanentsession")
                {
                    HandlePermanentSession(req, res, profile);
                    return;
                }
                if (lower == "/user/secureloginpersona")
                {
                    HandleSecureLoginPersona(req, res);
                    return;
                }
                if (lower == "/user/authenticateuser2" || lower == "/user/authenticateusertoken")
                {
                    HandleAuthenticate(req, res);
                    return;
                }
                if (lower == "/user/securelogin")
                {
                    HandleSecureLogin(req, res);
                    return;
                }
                if (lower == "/user/securelogoutpersona")
                {
                    HandleLogout(req, res);
                    return;
                }
                if (lower == "/user/setregion")
                {
                    SendEmpty(res, 200);
                    return;
                }
                if (lower == "/user/getuserinfofromsecuritytoken")
                {
                    HandleUserInfoFromToken(req, res, profile);
                    return;
                }
                if (lower == "/user/clientconfig" || lower == "/user/logging")
                {
                    if (req.Method == "POST") Log.Debug("AUTH", "client " + lower + ": " + Util.Truncate(Text(req.Body), 200));
                    SendEmpty(res, 200);
                    return;
                }
                if (lower.StartsWith("/user/securelogout", StringComparison.Ordinal) || lower == "/user/logout")
                {
                    HandleLogout(req, res);
                    return;
                }

                // ------------------------------------------------- crypto/relay
                if (lower == "/crypto/cryptoticket")
                {
                    HandleCryptoTicket(req, res);
                    return;
                }
                if (lower.StartsWith("/crypto/relaycryptoticket", StringComparison.Ordinal))
                {
                    HandleRelayCryptoTicket(req, res, rel);
                    return;
                }

                // ---------------------------------------------------- matchmaking
                if (lower.StartsWith("/matchmaking/", StringComparison.Ordinal))
                {
                    HandleMatchmaking(req, res, profile, rel);
                    return;
                }

                // ------------------------------------------------------ personas
                if (lower.StartsWith("/driverpersona/", StringComparison.Ordinal))
                {
                    HandleDriverPersona(req, res, rel, profile);
                    return;
                }
                if (lower.StartsWith("/personas/", StringComparison.Ordinal))
                {
                    HandlePersonas(req, res, rel, profile);
                    return;
                }

                // ------------------------------------------------------- catalog
                if (lower.StartsWith("/catalog/", StringComparison.Ordinal) || lower.StartsWith("/catalogs/", StringComparison.Ordinal))
                {
                    if (!ServeStatic(req, res, profile, rel))
                    {
                        Log.Proto("ENGINE", "catalog endpoint without data: " + rel);
                        SendEmpty(res, 200);
                    }
                    return;
                }

                // -------------------------------------------------------- events
                if (lower.StartsWith("/event/", StringComparison.Ordinal) || lower.StartsWith("/events/", StringComparison.Ordinal) ||
                    lower.StartsWith("/matchmaking/", StringComparison.Ordinal))
                {
                    HandleEvents(req, res, lower);
                    return;
                }

                // -------------------------------------------------- achievements
                if (lower.StartsWith("/achievements/", StringComparison.Ordinal))
                {
                    if (lower == "/achievements/loadall")
                        SendXml(res, EngineXml.Achievements(CurrentPersona(req)));
                    else
                        SendEmpty(res, 200);
                    return;
                }
                if (lower == "/badges/set")
                {
                    SendEmpty(res, 200);
                    return;
                }

                // ------------------------------------------------------ powerups
                if (lower.StartsWith("/powerups/activated/", StringComparison.Ordinal))
                {
                    Log.Info("ENGINE", "powerup activated: " + rel.Substring("/powerups/activated/".Length) +
                                       " by " + Describe(req));
                    SendEmpty(res, 200);
                    return;
                }

                // ---------------------------------------------------------- misc
                if (lower == "/heartbeat")
                {
                    SendXml(res, "<HeartBeat>OK</HeartBeat>");
                    return;
                }
                if (lower == "/getserverinformation")
                {
                    string host = string.IsNullOrEmpty(_cfg.XmppHost) ? RequestHost(req) : _cfg.XmppHost;
                    SendJson(res, EngineXml.ServerInformation(host, _cfg.XmppPort, profile == null ? "" : profile.Id));
                    return;
                }
                if (lower == "/loginannouncements" || lower == "/news")
                {
                    SendXml(res, EngineXml.NewsArticles(PublicBase(req), profile == null ? "" : profile.Id));
                    return;
                }
                if (lower.StartsWith("/news/", StringComparison.Ordinal))
                {
                    if (!ServeStatic(req, res, profile, rel)) SendText(res, 404, "not found: " + rel);
                    return;
                }
                if (lower == "/session/getchatinfo")
                {
                    if (!ServeStatic(req, res, profile, rel))
                        SendXml(res, "<ChatInfo><Channel>channel.en__1</Channel><Room>1</Room></ChatInfo>");
                    return;
                }
                if (lower == "/getrebroadcasters")
                {
                    if (!ServeStatic(req, res, profile, rel)) SendXml(res, "<ArrayOfstring/>");
                    return;
                }
                if (lower == "/getfriendlistfromuserid")
                {
                    if (!ServeStatic(req, res, profile, rel)) SendXml(res, "<ArrayOfPersonaBase/>");
                    return;
                }
                if (lower == "/getblockeduserlist" || lower == "/getblockersbyusers")
                {
                    SendXml(res, "<ArrayOflong/>");
                    return;
                }
                if (lower == "/getusersettings" || lower == "/getsocialsettings" || lower == "/systeminfo")
                {
                    if (!ServeStatic(req, res, profile, rel)) SendText(res, 404, "not found: " + rel);
                    return;
                }
                if (lower == "/modding/getmodinfo")
                {
                    SendJson(res, "{\"basePath\":\"" + Json.Quote(PublicBase(req) + "/mods") + "\",\"serverID\":\"DPT_NFSW\",\"features\":[]}");
                    return;
                }
                if (lower == "/mods/index.json")
                {
                    SendJson(res, "{\"built_at\":\"" + Util.Iso(DateTime.UtcNow) + "\",\"entries\":[]}");
                    return;
                }
                if (lower == "/webkit/guide" || lower == "/gameguide/index.html")
                {
                    SendHtml(res, "<!doctype html><html><head><meta charset=\"utf-8\"><title>Game guide</title></head>" +
                                  "<body style=\"font-family:Segoe UI,Arial;background:#1b1f27;color:#e6e8ee;padding:24px\">" +
                                  "<h1>DPT.Server</h1><p>Need For Speed World game guide is not installed in this profile.</p></body></html>");
                    return;
                }
                if (lower.StartsWith("/reporting/", StringComparison.Ordinal) || lower.StartsWith("/logging/", StringComparison.Ordinal))
                {
                    if (lower == "/logging/client" && req.Method == "POST")
                        Log.Debug("ENGINE", "client log: " + Util.Truncate(Text(req.Body), 512));
                    SendEmpty(res, 200);
                    return;
                }
                if (lower.StartsWith("/adsrv/", StringComparison.Ordinal) || lower.StartsWith("/impsrv/", StringComparison.Ordinal))
                {
                    SendEmpty(res, 200);
                    return;
                }
                if (lower == "/servertime")
                {
                    SendXml(res, "<long>" + Util.UnixNow.ToString(CultureInfo.InvariantCulture) + "</long>");
                    return;
                }
                if (lower == "/powerups")
                {
                    Persona pu = CurrentPersona(req);
                    if (pu != null) { SendXml(res, EngineXml.Inventory(pu)); return; }
                    if (!ServeStatic(req, res, profile, rel)) SendXml(res, "<ArrayOfProductTrans/>");
                    return;
                }
                if (lower.StartsWith("/settings/", StringComparison.Ordinal))
                {
                    if (!ServeStatic(req, res, profile, rel)) SendEmpty(res, 200);
                    return;
                }
                if (IsOptionalEndpoint(lower))
                {
                    if (!ServeStatic(req, res, profile, rel))
                    {
                        Log.Proto("ENGINE", "optional endpoint without data: " + rel);
                        SendEmpty(res, 200);
                    }
                    return;
                }

                // --------------------------------------------------- static tail
                if (req.Method == "GET" || req.Method == "HEAD")
                {
                    if (ServeStatic(req, res, profile, rel)) return;
                    Log.Warn("ENGINE", "unhandled GET " + rel + " (profile " + (profile == null ? "none" : profile.Id) + ")");
                    if (_cfg.LenientUnhandled) SendEmpty(res, 200);
                    else SendText(res, 404, "not found: " + rel);
                    return;
                }

                Log.Warn("ENGINE", "unhandled " + req.Method + " " + rel + " body=" + Util.Truncate(Text(req.Body), 300));
                if (_cfg.LenientUnhandled) SendEmpty(res, 200);
                else SendText(res, 404, "not found: " + rel);
            }
            catch (Exception ex)
            {
                Log.Error("ENGINE", "route " + req.Method + " " + req.Path + " failed: " + ex);
                SendText(res, 500, "internal error");
            }
        }

        // ============================================================== account

        private void HandleAuthenticate(HttpRequest req, HttpResponse res)
        {
            string body = Text(req.Body);
            string username = FirstNonEmpty(
                Util.XmlValue(body, "userName"), Util.XmlValue(body, "username"),
                Util.XmlValue(body, "email"), Util.XmlValue(body, "Email"),
                req.QueryString("userName"), req.QueryString("email"));
            string password = FirstNonEmpty(Util.XmlValue(body, "password"), Util.XmlValue(body, "Password"), req.QueryString("password"));

            if (string.IsNullOrEmpty(username))
            {
                Log.Warn("AUTH", "authenticateUser without a user name, body=" + Util.Truncate(body, 200));
                SendXml(res, "<LoginStatusVO><UserId>0</UserId><LoginToken/><Description>INVALID_USER</Description></LoginStatusVO>");
                return;
            }

            Account account = _db.Authenticate(username, password, _cfg.AutoCreateAccounts, _cfg.RequirePassword);
            if (account == null)
            {
                Log.Warn("AUTH", "login rejected for '" + username + "' ip=" + req.RemoteIp);
                SendXml(res, "<LoginStatusVO><UserId>0</UserId><LoginToken/><Description>INVALID_CREDENTIALS</Description></LoginStatusVO>");
                return;
            }

            PlayerSession s = _sessions.Create(account, req.RemoteIp, req.Header("User-Agent"), "");
            Log.Info("AUTH", "login ok: " + account.Username + " (#" + account.Id + ") ip=" + req.RemoteIp);
            SendXml(res, "<LoginStatusVO><UserId>" + account.Id.ToString(CultureInfo.InvariantCulture) +
                         "</UserId><LoginToken>" + Util.EscapeXml(s.Token) + "</LoginToken><Description/></LoginStatusVO>");
        }

        private void HandleModernAuth(HttpRequest req, HttpResponse res)
        {
            string body = Text(req.Body);
            Dictionary<string, object> j = Json.TryParseObject(body);
            string username = j != null ? FirstNonEmpty(Json.GetString(j, "email", null), Json.GetString(j, "username", null)) : null;
            string password = j != null ? Json.GetString(j, "password", "") : "";
            if (string.IsNullOrEmpty(username))
            {
                username = FirstNonEmpty(Util.XmlValue(body, "email"), Util.XmlValue(body, "userName"));
                password = Util.XmlValue(body, "password");
            }
            if (string.IsNullOrEmpty(username))
            {
                SendJson(res, "{\"token\":\"\",\"userId\":0,\"message\":\"missing user name\"}");
                return;
            }
            Account account = _db.Authenticate(username, password, _cfg.AutoCreateAccounts, _cfg.RequirePassword);
            if (account == null)
            {
                SendJson(res, "{\"token\":\"\",\"userId\":0,\"message\":\"invalid credentials\"}");
                return;
            }
            PlayerSession s = _sessions.Create(account, req.RemoteIp, req.Header("User-Agent"), "");
            JsonBuilder jb = new JsonBuilder(128);
            jb.BeginObject().Prop("token", s.Token).Prop("userId", account.Id).Prop("message", "").EndObject();
            SendJson(res, jb.ToString());
        }

        private void HandleRegister(HttpRequest req, HttpResponse res, string lower)
        {
            string body = Text(req.Body);
            Dictionary<string, object> j = Json.TryParseObject(body);
            string username = j != null ? FirstNonEmpty(Json.GetString(j, "email", null), Json.GetString(j, "username", null)) : null;
            string password = j != null ? Json.GetString(j, "password", "") : null;
            if (string.IsNullOrEmpty(username)) username = FirstNonEmpty(Util.XmlValue(body, "email"), Util.XmlValue(body, "userName"));
            if (string.IsNullOrEmpty(password)) password = Util.XmlValue(body, "password");
            if (string.IsNullOrEmpty(username))
            {
                if (lower.Contains("modern")) SendJson(res, "{\"message\":\"missing user name\"}");
                else SendXml(res, "<LoginStatusVO><UserId>0</UserId><LoginToken/><Description>INVALID_USER</Description></LoginStatusVO>");
                return;
            }
            Account account = _db.FindAccountByName(username);
            if (account == null) account = _db.CreateAccount(username, password);
            PlayerSession s = _sessions.Create(account, req.RemoteIp, req.Header("User-Agent"), "");
            if (lower.Contains("modern"))
            {
                JsonBuilder jb = new JsonBuilder(128);
                jb.BeginObject().Prop("token", s.Token).Prop("userId", account.Id)
                  .Prop("message", "Account created, welcome to DPT.Server").EndObject();
                SendJson(res, jb.ToString());
            }
            else
            {
                SendXml(res, "<LoginStatusVO><UserId>" + account.Id.ToString(CultureInfo.InvariantCulture) +
                             "</UserId><LoginToken>" + Util.EscapeXml(s.Token) + "</LoginToken><Description/></LoginStatusVO>");
            }
        }

        private void HandlePermanentSession(HttpRequest req, HttpResponse res, ClientProfile profile)
        {
            string body = Text(req.Body);
            string machineId = FirstNonEmpty(Util.XmlValue(body, "machineID"), Util.XmlValue(body, "machineId"),
                                             req.QueryString("machineID"));

            PlayerSession s = _sessions.Resolve(req);
            if (s == null)
            {
                // no token: either a guest build (machineID) or an expired login
                Account guest = _db.Authenticate("driver_" + SafeMachine(machineId), "", true, false);
                s = _sessions.Create(guest, req.RemoteIp, req.Header("User-Agent"), machineId);
            }
            if (s == null)
            {
                SendText(res, 401, "no session");
                return;
            }
            s.MachineId = machineId ?? s.MachineId;
            if (profile != null) s.ProfileId = profile.Id;

            Account account = _db.FindAccountById(s.AccountId);
            List<Persona> personas = _db.PersonasOf(account);
            SendXml(res, EngineXml.UserInfo(personas, s.AccountId, s.Token, profile));
        }

        private static string SafeMachine(string machineId)
        {
            if (string.IsNullOrEmpty(machineId)) return "guest";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < machineId.Length && sb.Length < 24; i++)
            {
                char c = machineId[i];
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.Length == 0 ? "guest" : sb.ToString();
        }

        private void HandleSecureLoginPersona(HttpRequest req, HttpResponse res)
        {
            long personaId = Util.ParseLong(req.QueryString("personaId"), 0);
            PlayerSession s = _sessions.Resolve(req);
            if (personaId <= 0)
            {
                Log.Warn("SESSION", "SecureLoginPersona without personaId (token " + (s == null ? "none" : SessionManager.Short(s.Token)) + ")");
                SendEmpty(res, 404);
                return;
            }
            Persona p = _db.FindPersona(personaId);
            if (p == null)
            {
                Log.Warn("SESSION", "SecureLoginPersona: persona " + personaId + " not found");
                SendEmpty(res, 404);
                return;
            }
            if (s == null)
            {
                // token lost (client restarted) - bind persona to a fresh session anyway
                Account owner = _db.FindAccountById(p.AccountId);
                s = _sessions.Create(owner, req.RemoteIp, req.Header("User-Agent"), "");
            }
            if (s.AccountId != p.AccountId)
            {
                Log.Warn("SESSION", "persona " + p.Id + " does not belong to account #" + s.AccountId);
                SendEmpty(res, 403);
                return;
            }
            _sessions.BindPersona(s, p);
            _db.TouchPersona(p);
            SendEmpty(res, 200);
        }

        private void HandleLogout(HttpRequest req, HttpResponse res)
        {
            PlayerSession s = _sessions.Resolve(req);
            if (s != null)
            {
                Log.Info("SESSION", "logout: " + Describe(req));
                _sessions.UnbindPersona(s);
                _sessions.Remove(s.Token);
            }
            SendEmpty(res, 200);
        }

        // ============================================================= personas

        private void HandleDriverPersona(HttpRequest req, HttpResponse res, string rel, ClientProfile profile)
        {
            string lower = rel.ToLowerInvariant();
            PlayerSession s = _sessions.Resolve(req);

            if (lower == "/driverpersona/reservename")
            {
                string name = FirstNonEmpty(req.QueryString("name"), Util.XmlValue(Text(req.Body), "name"));
                bool taken = _db.FindPersonaByName(DataStore.CleanPersonaName(name)) != null;
                SendXml(res, taken ? "<ArrayOfString><string>NONE</string></ArrayOfString>" : "<ArrayOfString/>");
                return;
            }
            if (lower == "/driverpersona/createpersona")
            {
                string name = FirstNonEmpty(req.QueryString("name"), Util.XmlValue(Text(req.Body), "name"));
                int icon = Util.ParseInt(FirstNonEmpty(req.QueryString("iconIndex"), Util.XmlValue(Text(req.Body), "iconIndex")), 0);
                if (s == null)
                {
                    SendText(res, 401, "no session");
                    return;
                }
                Account account = _db.FindAccountById(s.AccountId);
                Persona p = _db.CreatePersona(account, name, icon);
                if (p == null)
                {
                    Log.Warn("PERSONA", "cannot create persona '" + name + "' (taken or invalid)");
                    SendEmpty(res, 409);
                    return;
                }
                SendXml(res, EngineXml.ProfileData(p, profile));
                return;
            }
            if (lower == "/driverpersona/deletepersona")
            {
                long id = Util.ParseLong(FirstNonEmpty(req.QueryString("personaId"), Util.XmlValue(Text(req.Body), "personaId")), 0);
                bool ok = _db.DeletePersona(id);
                SendEmpty(res, ok ? 200 : 404);
                return;
            }
            if (lower == "/driverpersona/updatestatusmessage")
            {
                Persona p = CurrentPersona(req);
                string motto = FirstNonEmpty(req.QueryString("statusMessage"), Util.XmlValue(Text(req.Body), "statusMessage"));
                if (p != null && motto != null)
                {
                    p.Motto = Util.Truncate(motto, 64);
                    _db.MarkDirty();
                }
                SendEmpty(res, 200);
                return;
            }
            if (lower == "/driverpersona/getpersonainfo")
            {
                Persona p = CurrentPersona(req);
                if (p == null)
                {
                    SendEmpty(res, 404);
                    return;
                }
                SendXml(res, EngineXml.ProfileData(p, profile));
                return;
            }
            if (lower == "/driverpersona/getpersonabasefromlist")
            {
                List<Persona> list = new List<Persona>();
                string ids = FirstNonEmpty(req.QueryString("personaIds"), Util.XmlValue(Text(req.Body), "personaIds"));
                if (!string.IsNullOrEmpty(ids))
                    foreach (string part in ids.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        Persona p = _db.FindPersona(Util.ParseLong(part, 0));
                        if (p != null) list.Add(p);
                    }
                SendXml(res, EngineXml.PersonaBaseList(list));
                return;
            }
            if (lower == "/driverpersona/getexplevelpointmap" || lower == "/driverpersona/getexplevelpointsmap")
            {
                if (!ServeStatic(req, res, profile, rel)) SendText(res, 404, "not found: " + rel);
                return;
            }

            if (lower == "/driverpersona/unreservename")
            {
                SendEmpty(res, 200);
                return;
            }
            if (lower == "/driverpersona/getpersonapresencebyname")
            {
                string name = req.QueryString("displayName");
                Persona found = _db.FindPersonaByName(DataStore.CleanPersonaName(name));
                if (found == null) SendXml(res, "<ArrayOfPersonaBase/>");
                else SendXml(res, EngineXml.PersonaBaseList(new List<Persona>(new Persona[] { found })));
                return;
            }
            if (lower == "/driverpersona/updatepersonapresence")
            {
                Persona pp = CurrentPersona(req);
                if (pp != null) _db.TouchPersona(pp);
                SendEmpty(res, 200);
                return;
            }

            Log.Warn("PERSONA", "unhandled DriverPersona call " + rel);
            SendEmpty(res, 200);
        }

        private void HandlePersonas(HttpRequest req, HttpResponse res, string rel, ClientProfile profile)
        {
            string[] seg = rel.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            // /personas/{id}/{rest} or /personas/inventory/objects
            if (seg.Length >= 3 && string.Equals(seg[1], "inventory", StringComparison.OrdinalIgnoreCase))
            {
                Persona p = CurrentPersona(req);
                if (p == null) { SendEmpty(res, 404); return; }
                SendXml(res, EngineXml.Inventory(p));
                return;
            }
            if (seg.Length >= 2 && string.Equals(seg[1], "defaultcars", StringComparison.OrdinalIgnoreCase))
            {
                Persona dp = CurrentPersona(req);
                Car dc = dp == null ? null : dp.CurrentCar;
                if (dc != null) SendXml(res, EngineXml.OwnedCar(dc));
                else SendXml(res, "<ArrayOfOwnedCarTrans/>");
                return;
            }
            if (seg.Length < 3)
            {
                SendEmpty(res, 404);
                return;
            }

            long personaId = Util.ParseLong(seg[1], 0);
            Persona persona = _db.FindPersona(personaId);
            string rest = string.Join("/", seg, 2, seg.Length - 2).ToLowerInvariant();
            if (rest.StartsWith("powerup", StringComparison.Ordinal))
            {
                SendEmpty(res, 200);
                return;
            }
            if (persona == null)
            {
                Log.Warn("PERSONA", "unknown persona id " + personaId + " in " + rel);
                SendEmpty(res, 404);
                return;
            }

            if (rest == "objects" || rest.EndsWith("/objects", StringComparison.Ordinal))
            {
                SendXml(res, EngineXml.Inventory(persona));
                return;
            }
            if (rest == "carslots")
            {
                SendXml(res, EngineXml.CarSlotInfo(persona));
                return;
            }
            if (rest == "cars" && (req.Method == "GET" || req.Method == "PUT"))
            {
                StringBuilder sb = new StringBuilder(2048);
                sb.Append("<ArrayOfOwnedCarTrans>");
                for (int i = 0; i < persona.Cars.Count; i++) sb.Append(EngineXml.OwnedCar(persona.Cars[i]));
                sb.Append("</ArrayOfOwnedCarTrans>");
                SendXml(res, sb.ToString());
                return;
            }
            if (rest == "cars" && req.Method == "POST")
            {
                long carId = Util.ParseLong(req.QueryString("serialNumber"), 0);
                bool ok = _db.RemoveCar(persona, carId);
                SendXml(res, ok ? "<SellCarTrans><Status>Success</Status></SellCarTrans>" : "<SellCarTrans><Status>Fail</Status></SellCarTrans>");
                return;
            }
            if (rest.StartsWith("defaultcar/", StringComparison.Ordinal))
            {
                long carId = Util.ParseLong(rest.Substring("defaultcar/".Length), 0);
                _db.SetCurrentCar(persona, carId);
                SendEmpty(res, 200);
                return;
            }
            if (rest.StartsWith("cars/", StringComparison.Ordinal))
            {
                long carId = Util.ParseLong(rest.Substring("cars/".Length), 0);
                Car one = _db.FindCar(persona, carId);
                if (one == null) { SendEmpty(res, 404); return; }
                SendXml(res, EngineXml.OwnedCar(one));
                return;
            }
            if (rest.StartsWith("inventory/sell", StringComparison.Ordinal))
            {
                Log.Info("PERSONA", "inventory sell from " + persona.Name + ": " + rel);
                SendEmpty(res, 200);
                return;
            }
            if (rest == "mydefaultcar")
            {
                Car cur = persona.CurrentCar;
                if (cur == null) { SendEmpty(res, 404); return; }
                SendXml(res, EngineXml.OwnedCar(cur));
                return;
            }
            if (rest == "defaultcar")
            {
                Car c = persona.CurrentCar;
                if (c == null) { SendEmpty(res, 404); return; }
                SendXml(res, EngineXml.OwnedCar(c));
                return;
            }
            if (rest == "commerce" && req.Method == "POST")
            {
                HandleCustomize(req, res, persona);
                return;
            }
            if (rest == "baskets" && req.Method == "POST")
            {
                HandlePurchase(req, res, persona, profile);
                return;
            }
            if (rest == "inventory" || rest == "inventoryobject")
            {
                SendXml(res, EngineXml.Inventory(persona));
                return;
            }

            SendEmpty(res, 200);
        }

        private void HandleCustomize(HttpRequest req, HttpResponse res, Persona persona)
        {
            string body = Text(req.Body);
            long carId = Util.ParseLong(FirstNonEmpty(
                Util.XmlValue(body, "Id"), req.QueryString("carId")), 0);
            Car car = carId > 0 ? _db.FindCar(persona, carId) : persona.CurrentCar;
            if (car == null)
            {
                SendEmpty(res, 404);
                return;
            }
            int customStart = IndexOfTag(body, "CustomCar");
            if (customStart >= 0)
            {
                int close = body.LastIndexOf("</CustomCar>", StringComparison.OrdinalIgnoreCase);
                if (close > customStart)
                {
                    car.CustomXml = body.Substring(customStart, close + "</CustomCar>".Length - customStart);
                    _db.MarkDirty();
                    Log.Info("PERSONA", "car " + car.Id + " customized by " + persona.Name);
                }
            }
            SendXml(res, "<CommerceSessionResultTrans><Status>Success</Status><UpdatedCar>" +
                         EngineXml.OwnedCar(car) + "</UpdatedCar></CommerceSessionResultTrans>");
        }

        private void HandlePurchase(HttpRequest req, HttpResponse res, Persona persona, ClientProfile profile)
        {
            string body = Text(req.Body);
            List<string> productIds = ExtractAll(body, "ProductId");
            long price = 0;
            List<string> prices = ExtractAll(body, "Price");
            for (int i = 0; i < prices.Count; i++) price += Util.ParseLong(prices[i].Split('.')[0], 0);

            List<Car> added = new List<Car>();
            bool insufficient = false;
            if (price > 0 && !_db.SpendCash(persona, price)) insufficient = true;

            if (!insufficient)
            {
                for (int i = 0; i < productIds.Count; i++)
                {
                    string pid = productIds[i].Trim();
                    if (!pid.StartsWith("SRV-CAR", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(pid, "SRV-CARSLOT", StringComparison.OrdinalIgnoreCase)) continue;
                    Car c = AddPresetCar(persona, pid, profile);
                    if (c != null) added.Add(c);
                }
                if (productIds.Contains("SRV-GARAGESLOT")) persona.CarSlots++;
            }

            StringBuilder sb = new StringBuilder(1024);
            sb.Append("<CommerceResultTrans xmlns=\"http://schemas.datacontract.org/2004/07/Victory.DataLayer.Serialization\"");
            sb.Append(" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">");
            sb.Append("<CommerceItems/><InvalidBasket i:nil=\"true\"/><InventoryItems><InventoryItemTrans/></InventoryItems>");
            sb.Append("<PurchasedCars>");
            for (int i = 0; i < added.Count; i++) sb.Append(EngineXml.OwnedCar(added[i]));
            sb.Append("</PurchasedCars>");
            sb.Append("<Status>").Append(insufficient ? "Fail_InsufficientFunds" : "Success").Append("</Status>");
            sb.Append("<Wallets><Cash>").Append(persona.Cash.ToString(CultureInfo.InvariantCulture)).Append("</Cash>");
            sb.Append("<Tokens>").Append(persona.Tokens.ToString(CultureInfo.InvariantCulture)).Append("</Tokens></Wallets>");
            sb.Append("</CommerceResultTrans>");
            SendXml(res, sb.ToString());
            Log.Info("PERSONA", "purchase by " + persona.Name + ": " + string.Join(",", productIds.ToArray()) +
                                 " price=" + price + " status=" + (insufficient ? "insufficient" : "ok"));
        }

        private Car AddPresetCar(Persona persona, string productId, ClientProfile profile)
        {
            string custom = null;
            string carName = productId;
            if (profile != null)
            {
                string rel = "catalog/baskets/" + productId + ".xml";
                string file = profile.ResolveFile(rel);
                if (file != null && File.Exists(file))
                {
                    custom = ExtractCustomCar(File.ReadAllText(file, Encoding.UTF8));
                    carName = FirstNonEmpty(Util.XmlValue(File.ReadAllText(file, Encoding.UTF8), "Name"), productId);
                }
            }
            Car c = _db.AddCar(persona, productId, carName, true);
            if (c != null && custom != null) c.CustomXml = custom;
            if (c != null && persona.Cars.Count == 1) persona.CurrentCarId = c.Id;
            _db.MarkDirty();
            return c;
        }

        private static string ExtractCustomCar(string xml)
        {
            int start = IndexOfTag(xml, "CustomCar");
            if (start < 0) return null;
            int close = xml.LastIndexOf("</CustomCar>", StringComparison.OrdinalIgnoreCase);
            if (close < start) return null;
            return xml.Substring(start, close + "</CustomCar>".Length - start);
        }

        private static int IndexOfTag(string xml, string tag)
        {
            if (string.IsNullOrEmpty(xml)) return -1;
            return xml.IndexOf("<" + tag, StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ExtractAll(string xml, string tag)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(xml)) return list;
            string open = "<" + tag + ">";
            string close = "</" + tag + ">";
            int pos = 0;
            while (true)
            {
                int a = xml.IndexOf(open, pos, StringComparison.OrdinalIgnoreCase);
                if (a < 0) break;
                int b = xml.IndexOf(close, a + open.Length, StringComparison.OrdinalIgnoreCase);
                if (b < 0) break;
                list.Add(xml.Substring(a + open.Length, b - a - open.Length));
                pos = b + close.Length;
            }
            return list;
        }

        // =============================================================== events

        private void HandleEvents(HttpRequest req, HttpResponse res, string lower)
        {
            Persona p = CurrentPersona(req);
            if (lower.Contains("gettreasurehunteventsession"))
            {
                SendXml(res, EngineXml.TreasureHunt());
                return;
            }
            if (lower.Contains("arbitrat"))
            {
                long cash = _cfg.RewardCashPerEvent;
                long rep = _cfg.RewardRepPerEvent;
                if (p != null)
                {
                    if (cash > 0) _db.AddCash(p, cash);
                    if (rep > 0) _db.AddRep(p, rep);
                    _db.TouchPersona(p);
                }
                Log.Info("EVENT", "arbitration from " + Describe(req) + " (cash +" + cash + ", rep +" + rep + ")");
                SendXml(res, "<ArbitrationTrans><Status>Success</Status><SessionId>1</SessionId></ArbitrationTrans>");
                return;
            }
            if (lower.Contains("bust"))
            {
                SendXml(res, "<PursuitEventResult><Accolades><FinalRewards><Rep>0</Rep><Tokens>0</Tokens></FinalRewards>" +
                             "<HasLeveledUp>false</HasLeveledUp></Accolades><Durability>100</Durability>" +
                             "<EventId>0</EventId><EventSessionId>0</EventSessionId><ExitPath>ExitToFreeroam</ExitPath>" +
                             "<InviteLifetimeInMilliseconds>0</InviteLifetimeInMilliseconds><LobbyInviteId>0</LobbyInviteId>" +
                             "<PersonaId>" + (p == null ? "0" : p.Id.ToString(CultureInfo.InvariantCulture)) + "</PersonaId><Heat>1</Heat>" +
                             "</PursuitEventResult>");
                return;
            }
            if (lower.Contains("availableatlevel"))
            {
                if (!ServeStatic(req, res, CurrentProfile(req), "/events/availableatlevel")) SendXml(res, "<ArrayOfEventDefinition/>");
                return;
            }
            if (lower.Contains("accolade"))
            {
                SendXml(res, "<ArrayOfAccoladeDefinition/>");
                return;
            }
            if (lower.Contains("launched") || lower.Contains("notifycoincollected") || lower.Contains("abort") || lower.Contains("pausedetected"))
            {
                SendEmpty(res, 200);
                return;
            }
            Send30Empty(res);
        }

        // ==================================================== auth / nat / crypto

        /// <summary>
        /// User/SecureLogin: the client reports its NAT picture here. We keep it on the
        /// session so the admin board can show who is behind CGNAT/hairpin before the
        /// relay ticket is even requested.
        /// </summary>
        private void HandleSecureLogin(HttpRequest req, HttpResponse res)
        {
            PlayerSession s = _sessions.Resolve(req);
            if (s == null)
            {
                // some builds call SecureLogin before the persona is bound
                Account acc = _db.Authenticate("driver_" + SafeMachine(req.QueryString("userId")), "", true, false);
                s = _sessions.Create(acc, req.RemoteIp, req.Header("User-Agent"), "");
            }
            if (s != null)
            {
                _sessions.SetNatInfo(s,
                    req.QueryString("localIp"), Util.ParseInt(req.QueryString("localPort"), 0),
                    req.QueryString("publicIp"), Util.ParseInt(req.QueryString("publicPort"), 0),
                    Util.ParseInt(req.QueryString("natType"), 0),
                    Util.ParseInt(req.QueryString("natBehavior"), 0),
                    Util.ParseInt(req.QueryString("natIncrement"), 0));
                s.LastSeenUtc = DateTime.UtcNow;
            }
            Log.Info("SESSION", "SecureLogin from " + Describe(req) +
                                 " local=" + req.QueryString("localIp") + ":" + req.QueryString("localPort") +
                                 " public=" + req.QueryString("publicIp") + ":" + req.QueryString("publicPort") +
                                 " nat=" + req.QueryString("natType"));
            SendEmpty(res, 200);
        }

        private void HandleUserInfoFromToken(HttpRequest req, HttpResponse res, ClientProfile profile)
        {
            PlayerSession s = _sessions.Resolve(req);
            if (s == null) { SendEmpty(res, 401); return; }
            Account account = _db.FindAccountById(s.AccountId);
            SendXml(res, EngineXml.UserInfo(_db.PersonasOf(account), s.AccountId, s.Token, profile));
        }

        /// <summary>
        /// crypto/cryptoticket: client/server session key. The 2010/2011 engine only
        /// uses it for the relay handshake, the game itself is plain HTTP, so a stable
        /// random ticket is enough to keep the client happy.
        /// </summary>
        private void HandleCryptoTicket(HttpRequest req, HttpResponse res)
        {
            byte[] ticket = new byte[16];
            byte[] iv = new byte[16];
            using (System.Security.Cryptography.RNGCryptoServiceProvider rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
            {
                rng.GetBytes(ticket);
                rng.GetBytes(iv);
            }
            StringBuilder sb = new StringBuilder(256);
            sb.Append("<CryptoTicket><Ticket>").Append(Convert.ToBase64String(ticket));
            sb.Append("</Ticket><TicketIv>").Append(Convert.ToBase64String(iv));
            sb.Append("</TicketIv></CryptoTicket>");
            SendXml(res, sb.ToString());
            Log.Proto("CRYPTO", "cryptoticket issued to " + Describe(req));
        }

        /// <summary>
        /// crypto/relaycryptoticket/{personaId}: tells the client which UDP relay to use.
        /// This is the hook the relay needed all along - proven by the client strings
        /// "Relay::SetServer(): UDP Relay Server=%s:%d" and /crypto/relaycryptoticket/.
        /// </summary>
        private void HandleRelayCryptoTicket(HttpRequest req, HttpResponse res, string rel)
        {
            PlayerSession s = _sessions.Resolve(req);
            string personaId = rel.Substring(rel.LastIndexOf('/') + 1);
            long pid = Util.ParseLong(personaId, 0);
            if (pid <= 0 && s != null) pid = s.PersonaId;

            string host = RelayHost(req);
            int port = _app.Relay == null || !_app.Relay.IsRunning ? _cfg.RelayPort : _app.Relay.Port;
            string ticket = _app.Relay == null ? Util.ShortToken(16) : _app.Relay.CreateTicket(pid);
            if (s != null) { s.RelayTicket = ticket; s.Relay = true; s.RelayEndpoint = host + ":" + port; }

            StringBuilder sb = new StringBuilder(512);
            sb.Append("<ArrayOfUdpRelayInfo><UdpRelayInfo>");
            sb.Append("<UdpRelayHost>").Append(Util.EscapeXml(host)).Append("</UdpRelayHost>");
            sb.Append("<UdpRelayPort>").Append(port.ToString(CultureInfo.InvariantCulture)).Append("</UdpRelayPort>");
            sb.Append("<UdpRelayCryptoTicket>").Append(Util.EscapeXml(ticket)).Append("</UdpRelayCryptoTicket>");
            sb.Append("<UdpRelayBandwidthInBps>").Append(_cfg.RelayBandwidthBps.ToString(CultureInfo.InvariantCulture)).Append("</UdpRelayBandwidthInBps>");
            sb.Append("<UdpRelayTimeoutInMilliseconds>").Append((_cfg.RelayIdleSeconds * 1000L).ToString(CultureInfo.InvariantCulture)).Append("</UdpRelayTimeoutInMilliseconds>");
            sb.Append("</UdpRelayInfo></ArrayOfUdpRelayInfo>");
            SendXml(res, sb.ToString());
            Log.Info("RELAY", "relay ticket for persona " + pid + " -> " + host + ":" + port);
        }

        private string RelayHost(HttpRequest req)
        {
            if (!string.IsNullOrEmpty(_cfg.RelayPublicHost)) return _cfg.RelayPublicHost;
            string host = null;
            if (!string.IsNullOrEmpty(_cfg.PublicUrl))
            {
                try { host = new Uri(_cfg.PublicUrl).Host; } catch { host = null; }
            }
            if (string.IsNullOrEmpty(host)) host = RequestHost(req);
            return host;
        }

        private void HandleMatchmaking(HttpRequest req, HttpResponse res, ClientProfile profile, string rel)
        {
            PlayerSession s = _sessions.Resolve(req);
            string lower = rel.ToLowerInvariant();
            if (lower.StartsWith("/matchmaking/launchevent", StringComparison.Ordinal) ||
                lower.StartsWith("/matchmaking/joinqueue", StringComparison.Ordinal) ||
                lower.StartsWith("/matchmaking/makeprivatelobby", StringComparison.Ordinal))
            {
                if (ServeStatic(req, res, profile, rel)) return;
                // tell the client "no race yet" instead of a malformed object
                SendEmpty(res, 200);
                Log.Info("EVENT", "matchmaking " + rel + " from " + Describe(req) + " (no race configured)");
                return;
            }
            if (lower.StartsWith("/matchmaking/leave", StringComparison.Ordinal) ||
                lower.StartsWith("/matchmaking/acceptinvite", StringComparison.Ordinal) ||
                lower.StartsWith("/matchmaking/declineinvite", StringComparison.Ordinal) ||
                lower.StartsWith("/matchmaking/invitetoprivatelobby", StringComparison.Ordinal))
            {
                SendEmpty(res, 200);
                return;
            }
            if (ServeStatic(req, res, profile, rel)) return;
            SendXml(res, "<ArrayOfEventDefinition/>");
        }

        private static readonly string[] OptionalEndpoints = new string[]
        {
            "/description", "/getshardinfo", "/getregioninfo", "/launcherinfo", "/heartbeatlauncher",
            "/carclasses", "/loginannouncements/imagespath", "/newsarticles", "/getsocialnetworkinfo",
            "/setusersettings", "/setsocialsettings", "/getpersonaidsfromuserids", "/addfriendrequest",
            "/removefriend", "/resolvefriendsrequest", "/blockplayer", "/unblockplayer", "/social/petition",
            "/security/", "/commerce/reportstorefailure", "/progressionamplifiers/reapply", "/gifts/",
            "/catalog/catalogversion", "/catalog/categories", "/catalog/personalizedproducts",
            "/catalog/products", "/catalog/productsinCategory"
        };

        private static bool IsOptionalEndpoint(string lower)
        {
            for (int i = 0; i < OptionalEndpoints.Length; i++)
                if (lower.StartsWith(OptionalEndpoints[i], StringComparison.Ordinal)) return true;
            return false;
        }

        private ClientProfile CurrentProfile(HttpRequest req)
        {
            return _profiles.Resolve(req.Path, req.Header("User-Agent"));
        }

        // ============================================================== helpers

        public static string StripEnginePrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            string p = path;
            int idx = p.IndexOf("/Engine.svc", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) p = p.Substring(idx + "/Engine.svc".Length);
            if (p.Length == 0) p = "/";
            if (p[0] != '/') p = "/" + p;
            return p;
        }

        private Persona CurrentPersona(HttpRequest req)
        {
            PlayerSession s = _sessions.Resolve(req);
            if (s == null) return null;
            if (s.PersonaId > 0) return _db.FindPersona(s.PersonaId);
            long q = Util.ParseLong(req.QueryString("personaId"), 0);
            if (q > 0) return _db.FindPersona(q);
            return null;
        }

        private string Describe(HttpRequest req)
        {
            PlayerSession s = _sessions.ResolveByToken(req.Header("securityToken"));
            if (s == null) return req.RemoteIp;
            return (string.IsNullOrEmpty(s.PersonaName) ? "account#" + s.AccountId : s.PersonaName) + "@" + req.RemoteIp;
        }

        private string RequestHost(HttpRequest req)
        {
            string host = req.Header("Host");
            if (!string.IsNullOrEmpty(host))
            {
                int colon = host.LastIndexOf(':');
                if (colon > 0) host = host.Substring(0, colon);
                return host;
            }
            return "127.0.0.1";
        }

        private string PublicBase(HttpRequest req)
        {
            if (!string.IsNullOrEmpty(_cfg.PublicUrl)) return _cfg.PublicUrl.TrimEnd('/');
            string host = req.Header("Host");
            if (!string.IsNullOrEmpty(host)) return "http://" + host;
            return "http://" + RequestHost(req) + ":" + _cfg.EnginePort.ToString(CultureInfo.InvariantCulture);
        }

        private string ResolveAnyFile(ClientProfile profile, string rel)
        {
            string clean = (rel ?? "").TrimStart('/');
            string file = profile == null ? null : profile.ResolveFile(clean);
            if (file == null)
            {
                ClientProfile alt = _profiles.FindWithFile(clean);
                if (alt != null) file = alt.ResolveFile(clean);
            }
            if (file == null)
            {
                string defaults = Path.Combine(_cfg.DataDir, "defaults");
                file = TryFile(Path.Combine(defaults, "Engine.svc", clean.Replace('/', '\\')));
                if (file == null) file = TryFile(Path.Combine(defaults, clean.Replace('/', '\\')));
            }
            if (file == null && clean.IndexOf('.') < 0)
                return ResolveAnyFile(profile, clean + ".xml");
            return file;
        }

        private static string TryFile(string path)
        {
            try { if (File.Exists(path)) return path; }
            catch { }
            return null;
        }

        private bool ServeStatic(HttpRequest req, HttpResponse res, ClientProfile profile, string rel)
        {
            if (!_cfg.StaticFallback) return false;
            string file = ResolveAnyFile(profile, rel);
            if (file == null) return false;

            try
            {
                byte[] data = File.ReadAllBytes(file);
                string ext = Path.GetExtension(file).ToLowerInvariant();
                res.Status = 200;
                res.ContentType = Mime(ext);
                res.Body = data;
                res.Set("Cache-Control", "public, max-age=60");
                res.Set("X-Content-Type-Options", "nosniff");
                if (_cfg.Compress && IsText(ext) && data.Length > 1024 &&
                    (req.Header("Accept-Encoding") ?? "").IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    byte[] gz = HttpCompression.Gzip(data);
                    if (gz != null && gz.Length < data.Length)
                    {
                        res.Body = gz;
                        res.Set("Content-Encoding", "gzip");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("ENGINE", "cannot serve " + rel + ": " + ex.Message);
                return false;
            }
        }

        private static bool IsText(string ext)
        {
            return ext == ".xml" || ext == ".json" || ext == ".txt" || ext == ".html" || ext == ".css" || ext == ".js";
        }

        public static string Mime(string ext)
        {
            switch (ext)
            {
                case ".xml": return "application/xml; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".ico": return "image/x-icon";
                case ".txt": return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        private static string Text(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            return Encoding.UTF8.GetString(body);
        }

        /// <summary>
        /// Answer for the launcher on the engine port. Deliberately the same
        /// document the public board returns: status counters and service flags,
        /// nothing that could identify a player or a machine.
        /// </summary>
        private string ProbeJson(bool full)
        {
            JsonBuilder j = new JsonBuilder(512);
            j.BeginObject().Prop("ok", true).Prop("name", ServerApp.Name).Prop("version", ServerApp.Version)
             .Prop("buildDate", ServerApp.BuildDate).Prop("uptime", _app.UptimeText)
             .Prop("time", Util.Iso(DateTime.UtcNow)).Prop("port", (long)_cfg.EnginePort);
            if (!full)
            {
                j.EndObject();
                return j.ToString();
            }
            j.Prop("description", _cfg.ServerDescription).Prop("location", _cfg.ServerLocation)
             .Prop("role", "engine").Prop("publicUrl", _cfg.PublicUrl);

            j.Name("services").BeginObject();
            j.Prop("engine", _app.Engine != null && _app.Engine.IsRunning);
            j.Prop("xmpp", _app.Xmpp != null && _app.Xmpp.IsRunning);
            j.Prop("xmppPort", _app.Xmpp == null ? 0 : (long)_app.Xmpp.Port);
            j.Prop("relay", _app.Relay != null && _app.Relay.IsRunning);
            j.Prop("relayPort", _app.Relay == null ? 0 : (long)_app.Relay.Port);
            j.EndObject();

            j.Name("counts").BeginObject();
            j.Prop("online", (long)_sessions.Online().Count);
            j.Prop("sessions", (long)_sessions.Count);
            j.Prop("personas", (long)_db.AllPersonas().Count);
            j.Prop("rooms", _app.Relay == null ? 0 : (long)_app.Relay.RoomCount);
            j.Prop("xmppClients", _app.Xmpp == null ? 0 : (long)_app.Xmpp.Clients);
            j.EndObject();

            j.EndObject();
            return j.ToString();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
                if (!string.IsNullOrEmpty(values[i])) return values[i];
            return null;
        }

        private static void SendXml(HttpResponse res, string xml)
        {
            res.Status = 200;
            res.ContentType = "application/xml; charset=utf-8";
            res.Body = Encoding.UTF8.GetBytes(xml ?? "");
        }

        private static void SendJson(HttpResponse res, string json)
        {
            res.Status = 200;
            res.ContentType = "application/json; charset=utf-8";
            res.Body = Encoding.UTF8.GetBytes(json ?? "");
        }

        private static void SendHtml(HttpResponse res, string html)
        {
            res.Status = 200;
            res.ContentType = "text/html; charset=utf-8";
            res.Body = Encoding.UTF8.GetBytes(html ?? "");
        }

        private static void SendText(HttpResponse res, int status, string text)
        {
            res.Status = status;
            res.ContentType = "text/plain; charset=utf-8";
            res.Body = Encoding.UTF8.GetBytes(text ?? "");
        }

        private static void SendEmpty(HttpResponse res, int status)
        {
            res.Status = status;
            res.ContentType = "application/xml; charset=utf-8";
            res.Body = new byte[0];
        }

        private static void Send30Empty(HttpResponse res)
        {
            res.Status = 200;
            res.ContentType = "application/xml; charset=utf-8";
            res.Body = Encoding.UTF8.GetBytes("<ArrayOfstring/>");
        }
    }
}