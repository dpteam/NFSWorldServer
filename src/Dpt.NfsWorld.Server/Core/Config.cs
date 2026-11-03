using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Dpt.NfsWorld.Core
{
    /// <summary>
    /// config.ini reader/writer. Flat "section.key" access, tolerant parser,
    /// creates the file with defaults (and a generated admin token) on first run.
    /// </summary>
    public sealed class Config
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new List<string>();
        private readonly string _path;

        public string BaseDir { get; private set; }

        // ---------------------------------------------------------------- [server]
        public string ServerName = "DPT.Server NFS World";
        public string PublicUrl = "";
        public string Bind = "0.0.0.0";
        public string Region = "US";
        public bool RequireAllServices;

        // ---------------------------------------------------------------- [engine]
        public bool EngineEnabled = true;
        public int EnginePort = 44600;
        public int[] EnginePortFallbacks = new int[] { 44604, 44608 };
        public string Profile = "auto";
        public string StateDir = "data/state";
        public int SessionTimeoutSec = 43200;
        public int MaxSessions = 512;
        public int MaxSessionsPerIp = 32;
        public bool StaticFallback = true;
        public bool LenientUnhandled = true;
        /// <summary>
        /// Protocol oracle: answer Engine.svc only from the client profile tree, so a
        /// reference pack can be replayed byte for byte while endpoints are ported.
        /// </summary>
        public bool EngineStaticOnly = false;
        /// <summary>Point dead absolute urls from the reference data at this server.</summary>
        public bool RewriteUrls = true;
        public long RewardCashPerEvent;
        public long RewardRepPerEvent;
        public long StartingCash = 100000;
        public long StartingRep;
        public int StartingLevel = 1;
        public string StartingCar = "SRV-CAR0";
        public bool AutoCreateAccounts = true;
        public bool RequirePassword = false;

        // ---------------------------------------------------------------- [xmpp]
        public bool XmppEnabled = true;
        public int XmppPort = 44603;
        public int[] XmppPortFallbacks = new int[] { 44607 };
        public string XmppHost = "127.0.0.1";
        /// <summary>Host we tell clients to use for EA-Chat. Empty = whatever Host they used (+1).</summary>
        public string XmppAdvertise = "";
        public string CertFile = "data/certs/xmpp.pfx";
        public string CertPassword = "nfsw";
        public int XmppIdleSeconds = 900;

        // ---------------------------------------------------------------- [relay]
        public bool RelayEnabled = true;
        public int RelayPort = 44610;
        public int[] RelayPortFallbacks = new int[] { 44611, 44612 };
        public string RelayBind = "0.0.0.0";
        public int RelayMaxRooms = 256;
        public int RelayMaxClientsPerRoom = 16;
        public int RelayMaxClients = 1024;
        public int RelayIdleSeconds = 300;
        public int RelayLifetimeSeconds = 43200;
        public int RelayMaxPacketBytes = 2048;
        public string RelayPublicHost = "";
        public int RelayBandwidthBps = 9600;

        // ---------------------------------------------------------------- [web]
        public int PublicPort = 44601;
        public int[] PublicPortFallbacks = new int[] { 44605, 44609 };
        public string PublicBind = "0.0.0.0";
        public int AdminPort = 44602;
        public int[] AdminPortFallbacks = new int[] { 44606 };
        public string AdminBind = "0.0.0.0";
        public bool AdminPrivateOnly = true;
        public string PublicBasePath = "";
        public string AdminBasePath = "";
        public bool TrustProxyHeaders = true;
        public string[] TrustedProxies = new string[] { "127.0.0.1", "::1", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16" };
        public string AdminToken = "";
        public bool AdminAllowQueryToken;
        public int AdminMaxFailures = 10;
        public int AdminBanSeconds = 300;
        public bool Compress = true;
        public int RequestsPerMinute = 900;
        public int MaxConcurrentRequests = 48;
        public bool ExposeLogs = true;
        public string WebServerName = "DPT.Server";
        public string ServerLocation = "";
        public string ServerDescription = "Need For Speed World - DPT.Server";
        public string DefaultTheme = "light";
        public bool DefaultSound = true;
        public int RefreshMs = 3000;
        public string WebRoot = "public";

        // ---------------------------------------------------------------- [logging]
        public string LogDirName = "logs";
        public LogLevel LogLevel = LogLevel.Info;
        public bool ProtocolTrace = true;
        public int LogMaxFileMB = 8;
        public int LogRetentionDays = 14;
        public int LogRingSize = 2000;

        // ---------------------------------------------------------------- [limits]
        public int MaxRequestBytes = 4 * 1024 * 1024;
        public int MaxHeaderBytes = 65536;
        public int MaxProtocolErrorsPerIp = 40;
        public int BanSeconds = 300;

        // ---------------------------------------------------------------- [launcher]
        public string GamePath = "nfsw\\nfsw.exe";
        public string ExtraArgs = "";
        public bool ShowNews = true;

        public string Path { get { return _path; } }

        private Config(string baseDir)
        {
            BaseDir = string.IsNullOrEmpty(baseDir) ? AppDomain.CurrentDomain.BaseDirectory : baseDir;
            _path = System.IO.Path.Combine(BaseDir, "config.ini");
        }

        public string DataDir { get { return System.IO.Path.Combine(BaseDir, "data"); } }
        public string StatePath { get { return Absolutize(StateDir); } }
        public string ProfileRoot { get { return System.IO.Path.Combine(DataDir, "profiles"); } }
        public string LogDir { get { return Absolutize(LogDirName); } }
        public string CertPath { get { return Absolutize(CertFile); } }
        public string WebRootPath
        {
            get
            {
                string p = Absolutize(WebRoot);
                if (Directory.Exists(p)) return p;
                string alt = System.IO.Path.Combine(BaseDir, "public");
                if (Directory.Exists(alt)) return alt;
                string up = System.IO.Path.GetFullPath(System.IO.Path.Combine(BaseDir, "..", "public"));
                if (Directory.Exists(up)) return up;
                return p;
            }
        }

        public string Absolutize(string relativeOrAbsolute)
        {
            if (string.IsNullOrEmpty(relativeOrAbsolute)) return BaseDir;
            if (System.IO.Path.IsPathRooted(relativeOrAbsolute)) return relativeOrAbsolute;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(BaseDir, relativeOrAbsolute));
        }

        // ------------------------------------------------------------- loading

        public static Config Load(string baseDir)
        {
            Config cfg = new Config(baseDir);
            cfg.ReadFile();
            cfg.ApplyValues();
            cfg.Normalize();
            return cfg;
        }

        public static LogLevel ParseLevel(string text)
        {
            if (string.IsNullOrEmpty(text)) return LogLevel.Info;
            switch (text.Trim().ToLowerInvariant())
            {
                case "error": case "0": return LogLevel.Error;
                case "warn": case "warning": case "1": return LogLevel.Warn;
                case "info": case "2": return LogLevel.Info;
                case "debug": case "3": return LogLevel.Debug;
                case "verbose": case "trace": case "4": return LogLevel.Verbose;
                default: return LogLevel.Info;
            }
        }

        private void ReadFile()
        {
            _values.Clear();
            _order.Clear();
            if (!File.Exists(_path)) return;

            string section = "";
            try
            {
                string[] lines = File.ReadAllLines(_path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    // strip trailing " ; comment" only when clearly separated
                    int semi = val.IndexOf(" ;", StringComparison.Ordinal);
                    if (semi >= 0) val = val.Substring(0, semi).Trim();

                    string full = section.Length == 0 ? key : section + "." + key;
                    if (!_values.ContainsKey(full)) _order.Add(full);
                    _values[full] = val;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("config.ini read error: " + ex.Message);
            }
        }

        public string Get(string key, string def)
        {
            string v;
            return _values.TryGetValue(key, out v) ? v : def;
        }

        public int GetInt(string key, int def) { return Util.ParseInt(Get(key, null), def); }
        public long GetLong(string key, long def) { return Util.ParseLong(Get(key, null), def); }
        public bool GetBool(string key, bool def) { return Util.ParseBool(Get(key, null), def); }
        public int[] GetIntList(string key) { return Util.ParseIntList(Get(key, null)); }
        public string[] GetCsv(string key, string[] def)
        {
            string v = Get(key, null);
            if (string.IsNullOrEmpty(v)) return def;
            string[] parts = v.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> list = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim();
                if (s.Length > 0) list.Add(s);
            }
            return list.Count == 0 ? def : list.ToArray();
        }

        private void ApplyValues()
        {
            ServerName = Get("server.name", ServerName);
            PublicUrl = Get("server.publicUrl", PublicUrl);
            Bind = Get("server.bind", Bind);
            Region = Get("server.region", Region);
            RequireAllServices = GetBool("server.requireAllServices", RequireAllServices);

            EngineEnabled = GetBool("engine.enabled", EngineEnabled);
            EnginePort = GetInt("engine.port", EnginePort);
            EnginePortFallbacks = MergeList(GetIntList("engine.portFallbacks"), EnginePortFallbacks);
            Profile = Get("engine.profile", Profile);
            StateDir = Get("engine.stateDir", StateDir);
            SessionTimeoutSec = GetInt("engine.sessionTimeoutSec", SessionTimeoutSec);
            MaxSessions = GetInt("engine.maxSessions", MaxSessions);
            MaxSessionsPerIp = GetInt("engine.maxSessionsPerIp", MaxSessionsPerIp);
            StaticFallback = GetBool("engine.staticFallback", StaticFallback);
            LenientUnhandled = GetBool("engine.lenientUnhandled", LenientUnhandled);
            EngineStaticOnly = GetBool("engine.staticOnly", EngineStaticOnly);
            RewriteUrls = GetBool("engine.rewriteUrls", RewriteUrls);
            RewardCashPerEvent = GetLong("engine.rewardCashPerEvent", RewardCashPerEvent);
            RewardRepPerEvent = GetLong("engine.rewardRepPerEvent", RewardRepPerEvent);
            StartingCash = GetLong("engine.startingCash", StartingCash);
            StartingRep = GetLong("engine.startingRep", StartingRep);
            StartingLevel = GetInt("engine.startingLevel", StartingLevel);
            StartingCar = Get("engine.startingCar", StartingCar);
            AutoCreateAccounts = GetBool("engine.autoCreateAccounts", AutoCreateAccounts);
            RequirePassword = GetBool("engine.requirePassword", RequirePassword);

            XmppEnabled = GetBool("xmpp.enabled", XmppEnabled);
            XmppPort = GetInt("xmpp.port", XmppPort);
            XmppPortFallbacks = MergeList(GetIntList("xmpp.portFallbacks"), XmppPortFallbacks);
            XmppHost = Get("xmpp.host", XmppHost);
            XmppAdvertise = Get("xmpp.advertise", XmppAdvertise);
            if (XmppHost == "auto") XmppHost = "";
            CertFile = Get("xmpp.certFile", CertFile);
            CertPassword = Get("xmpp.certPassword", CertPassword);
            XmppIdleSeconds = GetInt("xmpp.idleSeconds", XmppIdleSeconds);

            RelayEnabled = GetBool("relay.enabled", RelayEnabled);
            RelayPort = GetInt("relay.port", RelayPort);
            RelayPortFallbacks = MergeList(GetIntList("relay.portFallbacks"), RelayPortFallbacks);
            RelayBind = Get("relay.bindAddress", RelayBind);
            RelayMaxRooms = GetInt("relay.maxRooms", RelayMaxRooms);
            RelayMaxClientsPerRoom = GetInt("relay.maxClientsPerRoom", RelayMaxClientsPerRoom);
            RelayMaxClients = GetInt("relay.maxClients", RelayMaxClients);
            RelayIdleSeconds = GetInt("relay.idleSeconds", RelayIdleSeconds);
            RelayLifetimeSeconds = GetInt("relay.lifetimeSeconds", RelayLifetimeSeconds);
            RelayMaxPacketBytes = GetInt("relay.maxPacketBytes", RelayMaxPacketBytes);
            RelayPublicHost = Get("relay.publicHost", RelayPublicHost);
            RelayBandwidthBps = GetInt("relay.bandwidthBps", RelayBandwidthBps);

            PublicPort = GetInt("web.publicPort", PublicPort);
            PublicPortFallbacks = MergeList(GetIntList("web.publicPortFallbacks"), PublicPortFallbacks);
            PublicBind = Get("web.publicBind", PublicBind);
            AdminPort = GetInt("web.adminPort", AdminPort);
            AdminPortFallbacks = MergeList(GetIntList("web.adminPortFallbacks"), AdminPortFallbacks);
            AdminBind = Get("web.adminBind", AdminBind);
            AdminPrivateOnly = GetBool("web.adminPrivateOnly", AdminPrivateOnly);
            PublicBasePath = Get("web.publicBasePath", PublicBasePath);
            AdminBasePath = Get("web.adminBasePath", AdminBasePath);
            TrustProxyHeaders = GetBool("web.trustProxyHeaders", TrustProxyHeaders);
            TrustedProxies = GetCsv("web.trustedProxies", TrustedProxies);
            AdminToken = Get("web.adminToken", AdminToken);
            AdminAllowQueryToken = GetBool("web.adminAllowQueryToken", AdminAllowQueryToken);
            AdminMaxFailures = GetInt("web.adminMaxFailures", AdminMaxFailures);
            AdminBanSeconds = GetInt("web.adminBanSeconds", AdminBanSeconds);
            Compress = GetBool("web.compress", Compress);
            RequestsPerMinute = GetInt("web.requestsPerMinute", RequestsPerMinute);
            MaxConcurrentRequests = GetInt("web.maxConcurrentRequests", MaxConcurrentRequests);
            ExposeLogs = GetBool("web.exposeLogs", ExposeLogs);
            WebServerName = Get("web.serverName", WebServerName);
            ServerLocation = Get("web.serverLocation", ServerLocation);
            ServerDescription = Get("web.serverDescription", ServerDescription);
            DefaultTheme = Get("web.defaultTheme", DefaultTheme);
            DefaultSound = GetBool("web.defaultSound", DefaultSound);
            RefreshMs = GetInt("web.refreshMs", RefreshMs);
            WebRoot = Get("web.webRoot", WebRoot);

            LogDirName = Get("logging.dir", LogDirName);
            LogLevel = ParseLevel(Get("logging.level", "Info"));
            ProtocolTrace = GetBool("logging.protocolTrace", ProtocolTrace);
            LogMaxFileMB = GetInt("logging.maxFileMB", LogMaxFileMB);
            LogRetentionDays = GetInt("logging.retentionDays", LogRetentionDays);
            LogRingSize = GetInt("logging.ringSize", LogRingSize);

            MaxRequestBytes = GetInt("limits.maxRequestBytes", MaxRequestBytes);
            MaxHeaderBytes = GetInt("limits.maxHeaderBytes", MaxHeaderBytes);
            MaxProtocolErrorsPerIp = GetInt("limits.maxProtocolErrorsPerIp", MaxProtocolErrorsPerIp);
            BanSeconds = GetInt("limits.banSeconds", BanSeconds);

            GamePath = Get("launcher.gamePath", GamePath);
            ExtraArgs = Get("launcher.extraArgs", ExtraArgs);
            ShowNews = GetBool("launcher.showNews", ShowNews);
        }

        private static int[] MergeList(int[] primary, int[] fallback)
        {
            return primary != null && primary.Length > 0 ? primary : fallback;
        }

        public void Normalize()
        {
            if (string.IsNullOrEmpty(Bind)) Bind = "0.0.0.0";
            if (string.IsNullOrEmpty(PublicBind)) PublicBind = Bind;
            if (string.IsNullOrEmpty(AdminBind)) AdminBind = Bind;
            if (string.IsNullOrEmpty(RelayBind)) RelayBind = Bind;

            EnginePort = Port(EnginePort, 44600);
            XmppPort = Port(XmppPort, 44603);
            RelayPort = Port(RelayPort, 44610);
            PublicPort = Port(PublicPort, 44601);
            AdminPort = Port(AdminPort, 44602);

            SessionTimeoutSec = Clamp(SessionTimeoutSec, 0, 30 * 24 * 3600);
            MaxSessions = Clamp(MaxSessions, 8, 100000);
            MaxSessionsPerIp = Clamp(MaxSessionsPerIp, 1, 1000);
            XmppIdleSeconds = Clamp(XmppIdleSeconds, 60, 86400);
            RelayMaxRooms = Clamp(RelayMaxRooms, 1, 100000);
            RelayMaxClientsPerRoom = Clamp(RelayMaxClientsPerRoom, 1, 256);
            RelayMaxClients = Clamp(RelayMaxClients, 1, 100000);
            RelayIdleSeconds = Clamp(RelayIdleSeconds, 10, 86400);
            RelayLifetimeSeconds = Clamp(RelayLifetimeSeconds, 60, 7 * 24 * 3600);
            RelayMaxPacketBytes = Clamp(RelayMaxPacketBytes, 64, 65507);
            RequestsPerMinute = Clamp(RequestsPerMinute, 0, 1000000);
            MaxConcurrentRequests = Clamp(MaxConcurrentRequests, 4, 1024);
            MaxRequestBytes = Clamp(MaxRequestBytes, 4096, 128 * 1024 * 1024);
            MaxHeaderBytes = Clamp(MaxHeaderBytes, 1024, 1024 * 1024);
            MaxProtocolErrorsPerIp = Clamp(MaxProtocolErrorsPerIp, 1, 1000000);
            BanSeconds = Clamp(BanSeconds, 10, 86400);
            RefreshMs = Clamp(RefreshMs, 1000, 60000);
            LogMaxFileMB = Clamp(LogMaxFileMB, 1, 512);
            LogRetentionDays = Clamp(LogRetentionDays, 0, 3650);
            LogRingSize = Clamp(LogRingSize, 100, 100000);

            PublicBasePath = NormalizeBasePath(PublicBasePath);
            AdminBasePath = NormalizeBasePath(AdminBasePath);
            if (string.IsNullOrEmpty(Profile)) Profile = "auto";
            if (string.IsNullOrEmpty(StartingCar)) StartingCar = "SRV-CAR0";
            if (string.IsNullOrEmpty(DefaultTheme)) DefaultTheme = "light";
            if (StartingLevel < 1) StartingLevel = 1;
            if (StartingCash < 0) StartingCash = 0;
        }

        private static int Port(int v, int def)
        {
            if (v <= 0 || v > 65535) return def;
            return v;
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        public static string NormalizeBasePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string s = path.Trim();
            if (s == "/") return "";
            if (!s.StartsWith("/", StringComparison.Ordinal)) s = "/" + s;
            if (s.EndsWith("/", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
            return s;
        }

        /// <summary>Generates the admin token on first run and persists it in config.ini.</summary>
        public void EnsureSecrets()
        {
            bool changed = false;
            if (string.IsNullOrEmpty(AdminToken) || AdminToken.Length < 24)
            {
                AdminToken = Util.ShortToken(24);
                changed = true;
            }
            if (changed) Save();
        }

        /// <summary>Writes the whole default file, preserving nothing. Used on first run.</summary>
        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder(4096);
                sb.AppendLine("; =============================================================");
                sb.AppendLine(";  DPT.Server / NFS World - generated configuration");
                sb.AppendLine(";  Edit and restart. Keys that are missing fall back to defaults.");
                sb.AppendLine("; =============================================================");
                sb.AppendLine();
                sb.AppendLine("[server]");
                sb.AppendLine("name=" + ServerName);
                sb.AppendLine("publicUrl=" + PublicUrl);
                sb.AppendLine("bind=" + Bind);
                sb.AppendLine("region=" + Region);
                sb.AppendLine("requireAllServices=" + Bool(RequireAllServices));
                sb.AppendLine();
                sb.AppendLine("[engine]");
                sb.AppendLine("enabled=" + Bool(EngineEnabled));
                sb.AppendLine("port=" + EnginePort.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("portFallbacks=" + Join(EnginePortFallbacks));
                sb.AppendLine("profile=" + Profile);
                sb.AppendLine("stateDir=" + StateDir);
                sb.AppendLine("sessionTimeoutSec=" + SessionTimeoutSec.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("maxSessions=" + MaxSessions.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("maxSessionsPerIp=" + MaxSessionsPerIp.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("staticFallback=" + Bool(StaticFallback));
                sb.AppendLine("lenientUnhandled=" + Bool(LenientUnhandled));
                sb.AppendLine("staticOnly=" + Bool(EngineStaticOnly));
                sb.AppendLine("rewriteUrls=" + Bool(RewriteUrls));
                sb.AppendLine("rewardCashPerEvent=" + RewardCashPerEvent.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("rewardRepPerEvent=" + RewardRepPerEvent.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("startingCash=" + StartingCash.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("startingRep=" + StartingRep.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("startingLevel=" + StartingLevel.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("startingCar=" + StartingCar);
                sb.AppendLine("autoCreateAccounts=" + Bool(AutoCreateAccounts));
                sb.AppendLine("requirePassword=" + Bool(RequirePassword));
                sb.AppendLine();
                sb.AppendLine("[xmpp]");
                sb.AppendLine("enabled=" + Bool(XmppEnabled));
                sb.AppendLine("port=" + XmppPort.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("portFallbacks=" + Join(XmppPortFallbacks));
                sb.AppendLine("host=" + XmppHost);
                sb.AppendLine("advertise=" + XmppAdvertise);
                sb.AppendLine("certFile=" + CertFile);
                sb.AppendLine("certPassword=" + CertPassword);
                sb.AppendLine("idleSeconds=" + XmppIdleSeconds.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.AppendLine("[relay]");
                sb.AppendLine("enabled=" + Bool(RelayEnabled));
                sb.AppendLine("port=" + RelayPort.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("portFallbacks=" + Join(RelayPortFallbacks));
                sb.AppendLine("bindAddress=" + RelayBind);
                sb.AppendLine("maxRooms=" + RelayMaxRooms.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("maxClientsPerRoom=" + RelayMaxClientsPerRoom.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("maxClients=" + RelayMaxClients.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("idleSeconds=" + RelayIdleSeconds.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("lifetimeSeconds=" + RelayLifetimeSeconds.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("maxPacketBytes=" + RelayMaxPacketBytes.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("publicHost=" + RelayPublicHost);
                sb.AppendLine("bandwidthBps=" + RelayBandwidthBps.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("protocolTrace=" + Bool(ProtocolTrace));
                sb.AppendLine();
                sb.AppendLine("[web]");
                sb.AppendLine("publicPort=" + PublicPort.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("publicPortFallbacks=" + Join(PublicPortFallbacks));
                sb.AppendLine("publicBind=" + PublicBind);
                sb.AppendLine("adminPort=" + AdminPort.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("adminPortFallbacks=" + Join(AdminPortFallbacks));
                sb.AppendLine("adminBind=" + AdminBind);
                sb.AppendLine("adminPrivateOnly=" + Bool(AdminPrivateOnly));
                sb.AppendLine("publicBasePath=" + PublicBasePath);
                sb.AppendLine("adminBasePath=" + AdminBasePath);
                sb.AppendLine("trustProxyHeaders=" + Bool(TrustProxyHeaders));
                sb.AppendLine("trustedProxies=" + string.Join(",", TrustedProxies));
                sb.AppendLine("; token is generated on first run, keep it secret");
                sb.AppendLine("adminToken=" + AdminToken);
                sb.AppendLine("adminAllowQueryToken=" + Bool(AdminAllowQueryToken));
                sb.AppendLine("adminMaxFailures=" + AdminMaxFailures.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("adminBanSeconds=" + AdminBanSeconds.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("compress=" + Bool(Compress));
                sb.AppendLine("requestsPerMinute=" + RequestsPerMinute.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("maxConcurrentRequests=" + MaxConcurrentRequests.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("exposeLogs=" + Bool(ExposeLogs));
                sb.AppendLine("serverName=" + WebServerName);
                sb.AppendLine("serverLocation=" + ServerLocation);
                sb.AppendLine("serverDescription=" + ServerDescription);
                sb.AppendLine("defaultTheme=" + DefaultTheme);
                sb.AppendLine("defaultSound=" + Bool(DefaultSound));
                sb.AppendLine("refreshMs=" + RefreshMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("webRoot=" + WebRoot);
                sb.AppendLine();
                sb.AppendLine("[logging]");
                sb.AppendLine("dir=" + LogDirName);
                sb.AppendLine("level=" + LogLevel.ToString());
                sb.AppendLine("protocolTrace=" + Bool(ProtocolTrace));
                sb.AppendLine("maxFileMB=" + LogMaxFileMB.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("retentionDays=" + LogRetentionDays.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("ringSize=" + LogRingSize.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.AppendLine("[limits]");
                sb.AppendLine("maxRequestBytes=" + MaxRequestBytes.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("maxHeaderBytes=" + MaxHeaderBytes.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("maxProtocolErrorsPerIp=" + MaxProtocolErrorsPerIp.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("banSeconds=" + BanSeconds.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.AppendLine("[launcher]");
                sb.AppendLine("gamePath=" + GamePath);
                sb.AppendLine("extraArgs=" + ExtraArgs);
                sb.AppendLine("showNews=" + Bool(ShowNews));

                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("cannot write config.ini: " + ex.Message);
            }
        }

        private static string Bool(bool v) { return v ? "true" : "false"; }

        private static string Join(int[] values)
        {
            if (values == null) return "";
            string[] parts = new string[values.Length];
            for (int i = 0; i < values.Length; i++) parts[i] = values[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }

        public int[] PortsWithFallbacks(int primary, int[] fallbacks)
        {
            List<int> list = new List<int>();
            if (primary > 0) list.Add(primary);
            if (fallbacks != null)
                for (int i = 0; i < fallbacks.Length; i++)
                    if (fallbacks[i] > 0 && fallbacks[i] <= 65535 && !list.Contains(fallbacks[i])) list.Add(fallbacks[i]);
            return list.ToArray();
        }

        public string DescribePorts()
        {
            return "engine=" + EnginePort + " xmpp=" + XmppPort + " relay/udp=" + RelayPort +
                   " public=" + PublicPort + " admin=" + AdminPort;
        }

        /// <summary>Writes config.example.ini next to the exe when there is no config.ini.</summary>
        public static void CopyExampleIfMissing(string baseDir)
        {
            try
            {
                string example = System.IO.Path.Combine(baseDir, "config.example.ini");
                string target = System.IO.Path.Combine(baseDir, "config.ini");
                if (!File.Exists(target) && File.Exists(example))
                    File.Copy(example, target);
            }
            catch { }
        }
    }
}