using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dpt.NfsWorld.Core;

namespace Dpt.NfsWorld.Engine
{
    /// <summary>
    /// One client generation: the static Engine.svc tree plus its templates.
    /// The game differs between builds mostly by *files*, not by endpoints, so a
    /// profile is a directory with the same layout the original servers used.
    /// </summary>
    public sealed class ClientProfile
    {
        public string Id = "";
        public string DisplayName = "";
        public string ClientBuild = "";
        public string Description = "";
        public string Root = "";
        public bool ModernAuth;
        public bool TreasureHunt;
        public bool Achievements;
        public int SortOrder;
        /// <summary>"legacy" (Aug/Dec 2010, SoapBox 1.9.x) or "modern" (2011+).</summary>
        public string PersonaFormat = "legacy";

        public string EngineRoot { get { return Path.Combine(Root, "Engine.svc"); } }

        public bool HasFile(string relative)
        {
            return ResolveFile(relative) != null;
        }

        /// <summary>
        /// Resolves a request path inside the profile, refusing traversal.
        /// Looks in Engine.svc/ first (all SoapBox trees), then in the profile root
        /// (the 2011 build keeps basket/ and server/ next to Engine.svc).
        /// </summary>
        public string ResolveFile(string relative)
        {
            try
            {
                string clean = (relative ?? "").Replace('\\', '/').TrimStart('/');
                if (clean.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
                string engineRoot = Path.GetFullPath(EngineRoot);
                string profileRoot = Path.GetFullPath(Root);

                string inEngine = Path.GetFullPath(Path.Combine(engineRoot, clean.Replace('/', '\\')));
                if (inEngine.StartsWith(engineRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(inEngine))
                    return inEngine;

                string inRoot = Path.GetFullPath(Path.Combine(profileRoot, clean.Replace('/', '\\')));
                if (inRoot.StartsWith(profileRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(inRoot))
                    return inRoot;

                return null;
            }
            catch { return null; }
        }

        public override string ToString()
        {
            return Id + (string.IsNullOrEmpty(ClientBuild) ? "" : " [" + ClientBuild + "]");
        }
    }

    /// <summary>
    /// All installed profiles plus the version detection. Detection order:
    /// 1. engine.profile in config (forced),
    /// 2. the URL prefix the client used (/soapbox/Engine.svc, /nfsw/Engine.svc, ...),
    /// 3. the User-Agent build number,
    /// 4. the first profile that actually contains the requested file.
    /// </summary>
    public sealed class ProfileStore
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, ClientProfile> _byId = new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ClientProfile> _all = new List<ClientProfile>();
        private ClientProfile _fallback;

        public string RootDir { get; private set; }

        public ProfileStore(string rootDir, string forced)
        {
            RootDir = rootDir;
            Reload();
            _forced = forced;
            if (!string.IsNullOrEmpty(forced) && !string.Equals(forced, "auto", StringComparison.OrdinalIgnoreCase))
            {
                ClientProfile forcedProfile;
                if (_byId.TryGetValue(forced, out forcedProfile))
                {
                    _fallback = forcedProfile;
                    Log.Info("PROFILE", "forced client profile: " + forcedProfile);
                }
                else
                {
                    Log.Warn("PROFILE", "engine.profile=" + forced + " not found under " + rootDir);
                }
            }
            if (_fallback == null)
            {
                for (int i = 0; i < _all.Count; i++)
                {
                    if (string.Equals(_all[i].Id, "soapbox-1930", StringComparison.OrdinalIgnoreCase)) { _fallback = _all[i]; break; }
                }
                if (_fallback == null && _all.Count > 0) _fallback = _all[0];
            }
            Log.Info("PROFILE", "installed client profiles: " + (_all.Count == 0 ? "none" : string.Join(", ", Describe())));
            if (_fallback != null) Log.Info("PROFILE", "default client profile: " + _fallback);
        }

        private string _forced;

        public void Reload()
        {
            lock (_sync)
            {
                _byId.Clear();
                _all.Clear();
                if (!Directory.Exists(RootDir))
                {
                    try { Directory.CreateDirectory(RootDir); } catch { }
                    return;
                }
                string[] dirs = Directory.GetDirectories(RootDir);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < dirs.Length; i++)
                {
                    try
                    {
                        ClientProfile p = LoadProfile(dirs[i]);
                        if (p == null) continue;
                        _all.Add(p);
                        _byId[p.Id] = p;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("PROFILE", "cannot load profile " + dirs[i] + ": " + ex.Message);
                    }
                }
                _all.Sort(delegate (ClientProfile a, ClientProfile b)
                {
                    int c = a.SortOrder.CompareTo(b.SortOrder);
                    return c != 0 ? c : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
                });
            }
        }

        private static string[] DescribeAll()
        {
            return new string[0];
        }

        private string[] DescribeAllList()
        {
            string[] res = new string[_all.Count];
            for (int i = 0; i < _all.Count; i++) res[i] = _all[i].ToString();
            return res;
        }

        private static ClientProfile LoadProfile(string dir)
        {
            string ini = Path.Combine(dir, "profile.ini");
            ClientProfile p = new ClientProfile();
            p.Root = dir;
            p.Id = Path.GetFileName(dir);
            p.DisplayName = p.Id;
            p.SortOrder = 100;

            if (File.Exists(ini))
            {
                string[] lines = File.ReadAllLines(ini, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "id": p.Id = v; break;
                        case "name": p.DisplayName = v; break;
                        case "build": p.ClientBuild = v; break;
                        case "description": p.Description = v; break;
                        case "sort": p.SortOrder = Util.ParseInt(v, 100); break;
                        case "modernauth": p.ModernAuth = Util.ParseBool(v, false); break;
                        case "treasurehunt": p.TreasureHunt = Util.ParseBool(v, false); break;
                        case "achievements": p.Achievements = Util.ParseBool(v, false); break;
                        case "personaformat": p.PersonaFormat = v.Trim().ToLowerInvariant(); break;
                    }
                }
            }
            if (!Directory.Exists(p.EngineRoot))
            {
                // A profile without Engine.svc is not useful; still accept it if it has files,
                // because some builds use /basket and /server only.
                bool hasAny = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length > 0;
                if (!hasAny) return null;
            }
            return p;
        }

        public List<ClientProfile> All
        {
            get { lock (_sync) return new List<ClientProfile>(_all); }
        }

        public ClientProfile Fallback
        {
            get { lock (_sync) return _fallback; }
        }

        public ClientProfile Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_sync)
            {
                ClientProfile p;
                return _byId.TryGetValue(id, out p) ? p : null;
            }
        }

        /// <summary>Picks a profile for this request. Never returns null when any profile exists.</summary>
        public ClientProfile Resolve(string path, string userAgent)
        {
            lock (_sync)
            {
                if (_all.Count == 0) return null;

                // 1. forced profile
                if (!string.IsNullOrEmpty(_forced) && !string.Equals(_forced, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    ClientProfile forced;
                    if (_byId.TryGetValue(_forced, out forced)) return forced;
                }

                // 2. URL prefix used by the client build
                string lower = (path ?? "").ToLowerInvariant();
                if (lower.StartsWith("/soapbox/engine.svc", StringComparison.Ordinal)) return Pick("soapbox-1930");
                if (lower.StartsWith("/nfsw/engine.svc", StringComparison.Ordinal)) return Pick("nfsw-2011");

                // 3. User-Agent build number
                if (!string.IsNullOrEmpty(userAgent))
                {
                    if (userAgent.IndexOf("1930", StringComparison.Ordinal) >= 0) return Pick("soapbox-1930");
                    if (userAgent.IndexOf("1613", StringComparison.Ordinal) >= 0) return Pick("soapbox-1930");
                    if (userAgent.IndexOf("2011", StringComparison.Ordinal) >= 0) return Pick("nfsw-2011");
                    if (userAgent.IndexOf("2010", StringComparison.Ordinal) >= 0) return Pick("soapbox-aug2010");
                }

                return _fallback;
            }
        }

        private ClientProfile Pick(string preferred)
        {
            ClientProfile p;
            if (_byId.TryGetValue(preferred, out p)) return p;
            return _fallback;
        }

        /// <summary>Finds the first profile containing this file (used when the primary misses).</summary>
        public ClientProfile FindWithFile(string relative)
        {
            lock (_sync)
            {
                for (int i = 0; i < _all.Count; i++)
                    if (_all[i].HasFile(relative)) return _all[i];
            }
            return null;
        }

        public string[] Describe()
        {
            lock (_sync) return DescribeAllList();
        }
    }
}