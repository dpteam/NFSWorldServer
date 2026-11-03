using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dpt.NfsWorld.Launcher
{
    /// <summary>One entry of the server list shown in the launcher.</summary>
    public sealed class ServerEntry
    {
        public string Name = "";
        public string Url = "";
        public string Mode = "remote"; // local | remote
        public bool BuiltIn;

        public bool IsLocal { get { return string.Equals(Mode, "local", StringComparison.OrdinalIgnoreCase); } }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// Flat key=value settings next to the launcher. Written on every change, so a
    /// crash or a closed window never loses the server address or the game path.
    /// </summary>
    public sealed class LauncherSettings
    {
        public const string DefaultLocalUrl = "http://127.0.0.1:44620";
        public const string DefaultRemoteUrl = "http://nfsw.dartpower.xyz:44600";

        public string ServerUrl = DefaultLocalUrl;
        public string GamePath = "nfsw\\nfsw.exe";
        public string Region = "US";
        public string ExtraArgs = "";
        public string Theme = "dark";
        public bool Sound = true;
        public bool CloseOnPlay = true;
        public int Selected = 0;
        public List<ServerEntry> Servers = new List<ServerEntry>();

        public LauncherSettings()
        {
            ResetServers();
        }

        public void ResetServers()
        {
            Servers = new List<ServerEntry>();
            Servers.Add(new ServerEntry { Name = "Локальный сервер", Url = DefaultLocalUrl, Mode = "local", BuiltIn = true });
            Servers.Add(new ServerEntry { Name = "DPT.Server (домен)", Url = DefaultRemoteUrl, Mode = "remote", BuiltIn = true });
        }

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.ini"); }
        }

        public ServerEntry Current
        {
            get
            {
                if (Selected < 0 || Selected >= Servers.Count) return Servers.Count > 0 ? Servers[0] : null;
                return Servers[Selected];
            }
        }

        public static LauncherSettings Load()
        {
            LauncherSettings s = new LauncherSettings();
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return s;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "serverurl": s.ServerUrl = v; break;
                        case "gamepath": s.GamePath = v; break;
                        case "region": s.Region = v; break;
                        case "extraargs": s.ExtraArgs = v; break;
                        case "theme": s.Theme = v; break;
                        case "sound": s.Sound = IsTrue(v); break;
                        case "closeonplay": s.CloseOnPlay = IsTrue(v); break;
                        case "selected": s.Selected = ParseInt(v, 0); break;
                        case "servers":
                            List<ServerEntry> list = ParseServers(v);
                            if (list.Count > 0) s.Servers = list;
                            break;
                    }
                }
                if (s.Selected < 0 || s.Selected >= s.Servers.Count) s.Selected = 0;
                ServerEntry cur = s.Current;
                if (cur != null && !string.IsNullOrEmpty(cur.Url) && string.IsNullOrEmpty(s.ServerUrl)) s.ServerUrl = cur.Url;
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("; DPT.Server launcher settings (safe to edit)");
                sb.AppendLine("version=2");
                sb.AppendLine("serverUrl=" + ServerUrl);
                sb.AppendLine("gamePath=" + GamePath);
                sb.AppendLine("region=" + Region);
                sb.AppendLine("extraArgs=" + ExtraArgs);
                sb.AppendLine("theme=" + Theme);
                sb.AppendLine("sound=" + (Sound ? "1" : "0"));
                sb.AppendLine("closeOnPlay=" + (CloseOnPlay ? "1" : "0"));
                sb.AppendLine("selected=" + Selected);
                sb.AppendLine("servers=" + FormatServers(Servers));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch { }
        }

        public static string FormatServers(List<ServerEntry> list)
        {
            if (list == null || list.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(";;");
                ServerEntry e = list[i];
                sb.Append(Clean(e.Name)).Append('|').Append(Clean(e.Url)).Append('|').Append(e.IsLocal ? "local" : "remote")
                  .Append('|').Append(e.BuiltIn ? "1" : "0");
            }
            return sb.ToString();
        }

        private static List<ServerEntry> ParseServers(string text)
        {
            List<ServerEntry> list = new List<ServerEntry>();
            if (string.IsNullOrEmpty(text)) return list;
            string[] chunks = text.Split(new string[] { ";;" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < chunks.Length; i++)
            {
                string[] parts = chunks[i].Split('|');
                if (parts.Length < 2) continue;
                ServerEntry e = new ServerEntry();
                e.Name = parts[0].Trim();
                e.Url = parts[1].Trim();
                e.Mode = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : "remote";
                e.BuiltIn = parts.Length > 3 && parts[3].Trim() == "1";
                if (e.Name.Length == 0) e.Name = e.Url;
                if (e.Url.Length == 0) continue;
                list.Add(e);
            }
            return list;
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('|', '/').Replace(';', ',').Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static bool IsTrue(string v)
        {
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseInt(string v, int def)
        {
            int r;
            return int.TryParse(v, out r) ? r : def;
        }
    }
}