using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Dpt.NfsWorld.Launcher
{
    /// <summary>
    /// Everything that actually starts something: health probes, the offline server,
    /// nfsw.exe and the launcher log. Shared by the protocol handler and the WebView2 UI.
    /// </summary>
    public static class LaunchService
    {
        public const string VersionString = "0.4.0";

        public sealed class PlayResult
        {
            public bool Ok;
            public bool NeedGamePath;
            public string Message = "";
            public string Exe = "";
            public string Args = "";
            public string Server = "";
        }

        // ------------------------------------------------------------------ log

        public static void Log(string text)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 512 * 1024) { try { File.Delete(path); } catch { } }
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + (text ?? "") + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // -------------------------------------------------------------- urls

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

        public static string BuildGameArgs(string engineUrl, string region, string extraArgs)
        {
            string args = (string.IsNullOrEmpty(region) ? "US" : region.Trim().ToUpperInvariant()) +
                          " " + engineUrl + " a 1";
            if (!string.IsNullOrEmpty(extraArgs)) args += " " + extraArgs.Trim();
            return args;
        }

        public static bool IsLoopback(string serverUrl)
        {
            try
            {
                Uri u = new Uri(serverUrl);
                return u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ------------------------------------------------------------ network

        public static string HttpGet(string url, int timeoutMs)
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

        public static bool IsHealthy(string baseUrl, int timeoutMs)
        {
            try
            {
                string body = HttpGet(baseUrl + "/api/health", timeoutMs);
                return body.IndexOf("\"ok\":true", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        public static bool WaitHealthy(string baseUrl, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (IsHealthy(baseUrl, 1500)) return true;
                Thread.Sleep(400);
            }
            return false;
        }

        // ------------------------------------------------------------- server

        public static string FindServerExe()
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

        public static string StartOfflineServer()
        {
            string exe = FindServerExe();
            if (exe == null) return "NFSWorldServer.exe не найден рядом с лаунчером";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "--offline");
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                Process p = Process.Start(psi);
                return "started pid " + (p == null ? "?" : p.Id.ToString());
            }
            catch (Exception ex)
            {
                return "ошибка запуска: " + ex.Message;
            }
        }

        // --------------------------------------------------------------- game

        public static string ResolveGame(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath)) gamePath = "nfsw\\nfsw.exe";
            string[] candidates;
            if (Path.IsPathRooted(gamePath))
            {
                candidates = new string[] { gamePath };
            }
            else
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                candidates = new string[]
                {
                    Path.Combine(baseDir, gamePath),
                    Path.Combine(baseDir, "..", "nfsw", "nfsw.exe"),
                    Path.Combine(baseDir, "..", "..", "nfsw", "nfsw.exe"),
                    Path.Combine(baseDir, "nfsw.exe"),
                    @"C:\Program Files (x86)\Electronic Arts\Need For Speed World\nfsw.exe",
                    @"C:\Program Files\Electronic Arts\Need For Speed World\nfsw.exe"
                };
            }
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

        /// <summary>Thread safe: no dialogs, no UI access. Call from a worker thread.</summary>
        public static PlayResult Launch(string serverUrl, string gamePath, string region, string extraArgs, bool local)
        {
            PlayResult r = new PlayResult();
            try
            {
                string server = NormalizeServerUrl(serverUrl);
                r.Server = server;
                local = local || IsLoopback(server);

                if (local)
                {
                    if (!IsHealthy(server, 2500))
                    {
                        Log("offline: " + StartOfflineServer());
                        if (!WaitHealthy(server, 30000))
                        {
                            r.Message = "Локальный сервер не поднялся за 30 секунд. Запустите NFSWorldServer.exe --offline вручную.";
                            return r;
                        }
                    }
                }
                else if (!IsHealthy(server, 4000))
                {
                    r.Message = "Сервер " + server + " не отвечает. Проверьте адрес (порт 44600).";
                    return r;
                }

                string exe = ResolveGame(gamePath);
                if (exe == null)
                {
                    r.NeedGamePath = true;
                    r.Message = "nfsw.exe не найден — укажите путь к игре.";
                    return r;
                }

                string args = BuildGameArgs(server + "/Engine.svc", region, extraArgs);
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                Process p = Process.Start(psi);

                r.Ok = true;
                r.Exe = exe;
                r.Args = args;
                Log("launched pid " + (p == null ? "?" : p.Id.ToString()) + ": " + exe + " " + args);
                return r;
            }
            catch (Exception ex)
            {
                r.Message = ex.Message;
                Log("launch failed: " + ex);
                return r;
            }
        }
    }
}