using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Dpt.NfsWorld.Launcher
{
    /// <summary>
    /// Everything the MSHTML page can call. All network work happens on background
    /// threads; the page asks "are you done?" instead of blocking the UI thread
    /// (that is what used to leave the launcher stuck on "checking...").
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class LauncherBridge
    {
        public const string VersionString = "0.2.0";

        private readonly object _sync = new object();
        private readonly LauncherForm _form;

        private int _checkSeq;
        private int _checkBusy;
        private string _checkPayload = "state=idle";

        private int _playSeq;
        private int _playBusy;
        private string _playPayload = "state=idle";

        public LauncherBridge(LauncherForm form)
        {
            _form = form;
        }

        // ------------------------------------------------------------- basics

        public string Version() { return VersionString; }

        public string BaseDir()
        {
            try { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
            catch { return ""; }
        }

        /// <summary>Append a line to launcher.log (the page uses it for diagnostics).</summary>
        public void Log(string text)
        {
            LogLine(text);
        }

        public static void LogLine(string text)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    try { File.Delete(path); } catch { }
                }
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + (text ?? "") + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // ----------------------------------------------------------- settings

        public string GetSettings()
        {
            LauncherSettings s = LauncherSettings.Load();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("version=" + VersionString);
            sb.AppendLine("serverUrl=" + s.ServerUrl);
            sb.AppendLine("gamePath=" + s.GamePath);
            sb.AppendLine("region=" + s.Region);
            sb.AppendLine("extraArgs=" + s.ExtraArgs);
            sb.AppendLine("theme=" + s.Theme);
            sb.AppendLine("sound=" + (s.Sound ? "1" : "0"));
            sb.AppendLine("closeOnPlay=" + (s.CloseOnPlay ? "1" : "0"));
            sb.AppendLine("selected=" + s.Selected);
            sb.AppendLine("servers=" + LauncherSettings.FormatServers(s.Servers));
            return sb.ToString();
        }

        public void SaveSettings(string serverUrl, string gamePath, string region, string extraArgs,
                                 string theme, string sound, string closeOnPlay, string servers, string selected)
        {
            try
            {
                LauncherSettings s = new LauncherSettings();
                s.ServerUrl = (serverUrl ?? "").Trim();
                s.GamePath = (gamePath ?? "").Trim();
                s.Region = string.IsNullOrEmpty(region) ? "US" : region.Trim().ToUpperInvariant();
                s.ExtraArgs = (extraArgs ?? "").Trim();
                s.Theme = string.IsNullOrEmpty(theme) ? "dark" : theme.Trim();
                s.Sound = sound == "1";
                s.CloseOnPlay = closeOnPlay == "1";
                int sel;
                s.Selected = int.TryParse(selected, out sel) ? sel : 0;

                // the page sends the list back in the same "a|b|c|1;;d|e|f|0" form
                if (!string.IsNullOrEmpty(servers))
                {
                    LauncherSettings parsed = new LauncherSettings();
                    // reuse the parser through a temp save/load-free path: format is parsed in Load only,
                    // so do it manually here with the same rules.
                    s.Servers = ParseServers(servers);
                }
                if (s.Servers == null || s.Servers.Count == 0) s.ResetServers();
                if (s.Selected < 0 || s.Selected >= s.Servers.Count) s.Selected = 0;
                ServerEntry cur = s.Current;
                if (cur != null && !string.IsNullOrEmpty(cur.Url) && string.IsNullOrEmpty(s.ServerUrl))
                    s.ServerUrl = cur.Url;
                s.Save();
                LogLine("settings saved -> " + LauncherSettings.FilePath + " (servers=" + s.Servers.Count + ")");
            }
            catch (Exception ex)
            {
                LogLine("SaveSettings failed: " + ex.Message);
            }
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

        // -------------------------------------------------------------- checks

        /// <summary>Starts a health check in the background. Returns immediately.</summary>
        public void StartCheck(string url)
        {
            if (Interlocked.CompareExchange(ref _checkBusy, 1, 0) != 0) return;
            int seq = Interlocked.Increment(ref _checkSeq);
            lock (_sync) _checkPayload = "state=running";
            string target = NormalizeServerUrl(url);
            LogLine("check #" + seq + " -> " + target);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string payload;
                try
                {
                    string health = HttpGet(target + "/api/health", 4000);
                    if (health.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        payload = "state=error\nmessage=" + OneLine(health);
                    }
                    else
                    {
                        string status = HttpGet(target + "/api/status", 6000);
                        if (status.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) == 0)
                            payload = "state=error\nmessage=" + OneLine(status);
                        else
                            payload = "state=ok\nstatus=" + OneLine(status);
                    }
                }
                catch (Exception ex)
                {
                    payload = "state=error\nmessage=" + OneLine(ex.Message);
                }
                lock (_sync) _checkPayload = payload;
                Interlocked.Exchange(ref _checkBusy, 0);
            });
        }

        /// <summary>Returns "seq=N" + newline + the last check payload.</summary>
        public string GetCheckResult()
        {
            lock (_sync) return "seq=" + _checkSeq + "\n" + _checkPayload;
        }

        // ---------------------------------------------------------------- play

        /// <summary>
        /// Launches the game. For local servers the offline server is started first
        /// (separate ports and a separate data\offline state folder).
        /// </summary>
        public void BeginPlay(string url, string gamePath, string region, string extraArgs, string mode)
        {
            if (Interlocked.CompareExchange(ref _playBusy, 1, 0) != 0) return;
            int seq = Interlocked.Increment(ref _playSeq);
            lock (_sync) _playPayload = "state=running";
            bool local = string.Equals((mode ?? "").Trim(), "local", StringComparison.OrdinalIgnoreCase);
            string target = NormalizeServerUrl(url);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string payload;
                try
                {
                    if (local)
                    {
                        LogLine("play #" + seq + ": local mode, checking " + target);
                        if (!IsHealthy(target, 2500))
                        {
                            string started = StartOfflineServer();
                            LogLine("play #" + seq + ": starting offline server: " + started);
                            if (!WaitHealthy(target, 30000))
                            {
                                payload = "state=error\nmessage=Р›РѕРєР°Р»СЊРЅС‹Р№ СЃРµСЂРІРµСЂ РЅРµ РїРѕРґРЅСЏР»СЃСЏ Р·Р° 30СЃ. Р—Р°РїСѓСЃС‚РёС‚Рµ NFSWorldServer.exe --offline РІСЂСѓС‡РЅСѓСЋ Рё РїРѕСЃРјРѕС‚СЂРёС‚Рµ РµРіРѕ РєРѕРЅСЃРѕР»СЊ.";
                                lock (_sync) _playPayload = payload;
                                Interlocked.Exchange(ref _playBusy, 0);
                                return;
                            }
                        }
                    }
                    else if (!IsHealthy(target, 4000))
                    {
                        payload = "state=error\nmessage=РЎРµСЂРІРµСЂ " + target + " РЅРµ РѕС‚РІРµС‡Р°РµС‚. РџСЂРѕРІРµСЂСЊС‚Рµ Р°РґСЂРµСЃ Рё РїРѕСЂС‚ 44600.";
                        lock (_sync) _playPayload = payload;
                        Interlocked.Exchange(ref _playBusy, 0);
                        return;
                    }

                    string exe = ResolveGame(gamePath);
                    if (exe == null)
                    {
                        payload = "state=error\nmessage=nfsw.exe РЅРµ РЅР°Р№РґРµРЅ. РЈРєР°Р¶РёС‚Рµ РїСѓС‚СЊ Рє РёРіСЂРµ С‡РµСЂРµР· Browse.";
                        lock (_sync) _playPayload = payload;
                        Interlocked.Exchange(ref _playBusy, 0);
                        return;
                    }

                    string engine = target + "/Engine.svc";
                    string args = (string.IsNullOrEmpty(region) ? "US" : region.Trim().ToUpperInvariant()) + " " + engine + " a 1";
                    if (!string.IsNullOrEmpty(extraArgs)) args += " " + extraArgs.Trim();

                    ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                    psi.WorkingDirectory = Path.GetDirectoryName(exe);
                    psi.UseShellExecute = true;
                    Process.Start(psi);

                    payload = "state=ok\nexe=" + OneLine(exe) + "\nargs=" + OneLine(args) + "\nserver=" + OneLine(target);
                    LogLine("play #" + seq + ": launched " + exe + " " + args);
                }
                catch (Exception ex)
                {
                    payload = "state=error\nmessage=" + OneLine(ex.Message);
                    LogLine("play #" + seq + " failed: " + ex.Message);
                }
                lock (_sync) _playPayload = payload;
                Interlocked.Exchange(ref _playBusy, 0);
            });
        }

        public string GetPlayResult()
        {
            lock (_sync) return "seq=" + _playSeq + "\n" + _playPayload;
        }

        /// <summary>Starts bin\NFSWorldServer.exe --offline if it is not running yet.</summary>
        private string StartOfflineServer()
        {
            string exe = FindServerExe();
            if (exe == null) return "NFSWorldServer.exe РЅРµ РЅР°Р№РґРµРЅ СЂСЏРґРѕРј СЃ Р»Р°СѓРЅС‡РµСЂРѕРј";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "--offline");
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                Process p = Process.Start(psi);
                return "pid " + (p == null ? "?" : p.Id.ToString());
            }
            catch (Exception ex)
            {
                return "РѕС€РёР±РєР° Р·Р°РїСѓСЃРєР°: " + ex.Message;
            }
        }

        private static string FindServerExe()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new string[]
            {
                Path.Combine(baseDir, "NFSWorldServer.exe"),
                Path.Combine(baseDir, "..", "NFSWorldServer.exe"),
                Path.Combine(baseDir, "..", "bin", "NFSWorldServer.exe"),
                Path.Combine(baseDir, "server", "NFSWorldServer.exe")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    string full = Path.GetFullPath(candidates[i]);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        private static bool IsHealthy(string baseUrl, int timeoutMs)
        {
            try
            {
                string body = HttpGet(baseUrl + "/api/health", timeoutMs);
                return body.IndexOf("\"ok\":true", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static bool WaitHealthy(string baseUrl, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (IsHealthy(baseUrl, 1500)) return true;
                Thread.Sleep(400);
            }
            return false;
        }

        // ------------------------------------------------------------ game path

        public string BrowseGame()
        {
            try
            {
                using (System.Windows.Forms.OpenFileDialog dlg = new System.Windows.Forms.OpenFileDialog())
                {
                    dlg.Title = "Р’С‹Р±РµСЂРёС‚Рµ nfsw.exe";
                    dlg.Filter = "Need For Speed World (nfsw.exe)|nfsw.exe|РџСЂРѕРіСЂР°РјРјС‹ (*.exe)|*.exe|Р’СЃРµ С„Р°Р№Р»С‹ (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    LauncherSettings s = LauncherSettings.Load();
                    string guess = ResolveGame(s.GamePath);
                    if (guess != null) dlg.FileName = guess;
                    if (dlg.ShowDialog(_form) == System.Windows.Forms.DialogResult.OK)
                    {
                        LogLine("game path picked: " + dlg.FileName);
                        return dlg.FileName;
                    }
                }
            }
            catch (Exception ex)
            {
                LogLine("BrowseGame failed: " + ex.Message);
                return "ERROR: " + ex.Message;
            }
            return "";
        }

        public static string NormalizeServerUrl(string serverUrl)
        {
            string url = (serverUrl ?? "").Trim();
            if (url.Length == 0) url = LauncherSettings.DefaultLocalUrl;
            if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "http://" + url;
            url = url.TrimEnd('/');
            int svc = url.IndexOf("/Engine.svc", StringComparison.OrdinalIgnoreCase);
            if (svc >= 0) url = url.Substring(0, svc);
            return url;
        }

        private static string ResolveGame(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath)) gamePath = "nfsw\\nfsw.exe";
            List<string> candidates = new List<string>();
            if (Path.IsPathRooted(gamePath)) candidates.Add(gamePath);
            else
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                candidates.Add(Path.Combine(baseDir, gamePath));
                candidates.Add(Path.Combine(baseDir, "..", "nfsw", "nfsw.exe"));
                candidates.Add(Path.Combine(baseDir, "..", "..", "nfsw", "nfsw.exe"));
                candidates.Add(Path.Combine(baseDir, "nfsw.exe"));
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                try
                {
                    string full = Path.GetFullPath(candidates[i]);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        // ------------------------------------------------------------ HTTP

        public static string HttpGet(string url, int timeoutMs)
        {
            if (string.IsNullOrEmpty(url)) return "ERROR: empty url";
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "DPT.Server-Launcher/" + VersionString;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                req.Proxy = null;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    try
                    {
                        using (Stream es = wex.Response.GetResponseStream())
                        using (StreamReader er = new StreamReader(es, Encoding.UTF8))
                            return er.ReadToEnd();
                    }
                    catch { }
                }
                return "ERROR: " + wex.Message;
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        // ------------------------------------------------------------ window

        public void OpenLink(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        public void CloseApp()
        {
            try { _form.BeginInvoke(new Action(delegate { _form.Close(); })); }
            catch { }
        }
    }
}