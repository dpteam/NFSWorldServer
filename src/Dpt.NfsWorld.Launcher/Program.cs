using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Dpt.NfsWorld.Launcher
{
    /// <summary>
    /// Windowless launcher. It is registered as the dptnfsw:// protocol handler and
    /// only starts the game (and, for the local profile, the offline server).
    /// The UI lives in the web board (/launcher/), which removes MSHTML from the
    /// equation entirely - no WebBrowser control, no COM bridge, no dead buttons.
    /// </summary>
    internal static class Program
    {
        public const string VersionString = "0.3.0";
        public const string Scheme = "dptnfsw";

        [STAThread]
        private static int Main(string[] args)
        {
            string mode = args != null && args.Length > 0 ? args[0] : "";
            try
            {
                if (string.Equals(mode, "--selftest", StringComparison.OrdinalIgnoreCase)) return SelfTest();
                if (string.Equals(mode, "--install", StringComparison.OrdinalIgnoreCase)) return Install(true);
                if (string.Equals(mode, "--uninstall", StringComparison.OrdinalIgnoreCase)) return Install(false);
                if (string.Equals(mode, "--parse", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
                    return ParseOnly(args[1]);

                if (mode.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
                    return RunProtocol(mode);

                // no arguments: make sure the handler exists, then open the web launcher
                Install(true);
                OpenLauncherPage();
                return 0;
            }
            catch (Exception ex)
            {
                LogLine("FATAL: " + ex);
                MessageBox.Show("DPT.Server Launcher:\r\n" + ex.Message, "DPT.Server Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // ------------------------------------------------------------ protocol

        public sealed class LaunchRequest
        {
            public string Raw = "";
            public string Action = "";
            public string Server = "";
            public string Region = "US";
            public string ExtraArgs = "";
            public string GamePath = "";
            public bool Local;
        }

        internal static LaunchRequest ParseProtocol(string raw)
        {
            LaunchRequest r = new LaunchRequest();
            r.Raw = raw ?? "";
            string text = r.Raw.Trim();
            if (!text.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("not a " + Scheme + ":// url");

            string rest = text.Substring((Scheme + "://").Length);
            int q = rest.IndexOf('?');
            string action = q < 0 ? rest : rest.Substring(0, q);
            string query = q < 0 ? "" : rest.Substring(q + 1);
            r.Action = action.Trim('/').ToLowerInvariant();
            if (r.Action.Length == 0) r.Action = "play";

            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                string val = eq < 0 ? "" : pair.Substring(eq + 1);
                key = Uri.UnescapeDataString(key.Replace('+', ' ')).ToLowerInvariant();
                val = Uri.UnescapeDataString(val.Replace('+', ' '));
                switch (key)
                {
                    case "server": r.Server = val; break;
                    case "region": r.Region = val; break;
                    case "local": r.Local = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "args": r.ExtraArgs = val; break;
                    case "path": r.GamePath = val; break;
                }
            }
            return r;
        }

        internal static string NormalizeServerUrl(string serverUrl)
        {
            string url = (serverUrl ?? "").Trim();
            if (url.Length == 0) url = LauncherSettings.DefaultLocalUrl;
            if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "http://" + url;
            url = url.TrimEnd('/');
            int svc = url.IndexOf("/Engine.svc", StringComparison.OrdinalIgnoreCase);
            if (svc >= 0) url = url.Substring(0, svc);
            return url;
        }

        internal static string BuildGameArgs(string engineUrl, string region, string extraArgs)
        {
            string args = (string.IsNullOrEmpty(region) ? "US" : region.Trim().ToUpperInvariant()) +
                          " " + engineUrl + " a 1";
            if (!string.IsNullOrEmpty(extraArgs)) args += " " + extraArgs.Trim();
            return args;
        }

        private static int RunProtocol(string raw)
        {
            LaunchRequest r = ParseProtocol(raw);
            LauncherSettings settings = LauncherSettings.Load();
            LogLine("protocol: " + r.Action + " server=" + r.Server + " local=" + r.Local);

            if (r.Action != "play" && r.Action != "launch")
            {
                LogLine("unknown action: " + r.Action);
                return 2;
            }

            string server = NormalizeServerUrl(r.Server);
            bool local = r.Local || IsLoopback(server);

            if (local)
            {
                if (!IsHealthy(server, 2500))
                {
                    string started = StartOfflineServer();
                    LogLine("offline server: " + started);
                    if (!WaitHealthy(server, 30000))
                    {
                        MessageBox.Show("Локальный сервер не поднялся за 30 секунд.\r\nЗапустите NFSWorldServer.exe --offline вручную и посмотрите его консоль.",
                            "DPT.Server Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return 3;
                    }
                }
            }
            else if (!IsHealthy(server, 4000))
            {
                MessageBox.Show("Сервер " + server + " не отвечает.\r\nПроверьте адрес (порт игрового endpoint — 44600) и попробуйте снова.",
                    "DPT.Server Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 4;
            }

            string exe = ResolveGame(string.IsNullOrEmpty(r.GamePath) ? settings.GamePath : r.GamePath, true);
            if (exe == null)
            {
                LogLine("nfsw.exe not found, asking the user");
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Выберите nfsw.exe (Need For Speed World)";
                    dlg.Filter = "Need For Speed World (nfsw.exe)|nfsw.exe|Программы (*.exe)|*.exe|Все файлы (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    if (dlg.ShowDialog() != DialogResult.OK) return 5;
                    exe = dlg.FileName;
                }
                settings.GamePath = exe;
                settings.Save();
                LogLine("game path saved: " + exe);
            }

            string engine = server + "/Engine.svc";
            string args = BuildGameArgs(engine, r.Region, r.ExtraArgs);
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            psi.UseShellExecute = true;
            Process p = Process.Start(psi);
            LogLine("launched pid " + (p == null ? "?" : p.Id.ToString()) + ": " + exe + " " + args);

            settings.ServerUrl = server;
            settings.Save();
            return 0;
        }

        // ------------------------------------------------------------- registry

        internal static string CommandValue()
        {
            return "\"" + Application.ExecutablePath + "\" \"%1\"";
        }

        internal static bool IsInstalled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\" + Scheme + @"\shell\open\command"))
                {
                    if (k == null) return false;
                    string v = k.GetValue(null) as string;
                    return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private static int Install(bool install)
        {
            if (!install)
            {
                try
                {
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Scheme, false);
                    LogLine("protocol handler removed");
                    MessageBox.Show(Scheme + ":// больше не открывает лаунчер.", "DPT.Server Launcher",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    LogLine("uninstall failed: " + ex.Message);
                }
                return 0;
            }

            try
            {
                using (Microsoft.Win32.RegistryKey root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme))
                {
                    root.SetValue(null, "URL:DPT NFS World Launcher");
                    root.SetValue("URL Protocol", "");
                    using (Microsoft.Win32.RegistryKey icon = root.CreateSubKey("DefaultIcon"))
                        icon.SetValue(null, "\"" + Application.ExecutablePath + "\",0");
                    using (Microsoft.Win32.RegistryKey cmd = root.CreateSubKey(@"shell\open\command"))
                        cmd.SetValue(null, CommandValue());
                }
                LogLine("protocol handler installed: " + Scheme + ":// -> " + Application.ExecutablePath);
                return 0;
            }
            catch (Exception ex)
            {
                LogLine("install failed: " + ex.Message);
                MessageBox.Show("Не удалось зарегистрировать " + Scheme + "://\r\n" + ex.Message,
                    "DPT.Server Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // --------------------------------------------------------------- server

        private static bool IsLoopback(string serverUrl)
        {
            try
            {
                Uri u = new Uri(serverUrl);
                return u.IsLoopback || u.Host == "localhost";
            }
            catch { return false; }
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

        private static string StartOfflineServer()
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

        private static string ResolveGame(string gamePath, bool askUserIfMissing)
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

        // ------------------------------------------------------------------ misc

        internal static string HttpGet(string url, int timeoutMs)
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

        internal static void LogLine(string text)
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

        private static void OpenLauncherPage()
        {
            LauncherSettings s = LauncherSettings.Load();
            string page = s.LauncherPage;
            if (string.IsNullOrEmpty(page))
                page = NormalizeServerUrl(s.ServerUrl).Replace(":44600", ":44601") + "/launcher/";
            try
            {
                Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                LogLine("opened launcher page: " + page);
            }
            catch (Exception ex)
            {
                LogLine("cannot open launcher page: " + ex.Message);
                MessageBox.Show("Откройте в браузере:\r\n" + page, "DPT.Server Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // -------------------------------------------------------------- selftest

        private static int SelfTest()
        {
            LogLine("selftest run");
            StringBuilder report = new StringBuilder();
            int failures = 0;

            string path = LauncherSettings.FilePath;
            bool existed = File.Exists(path);
            string backup = existed ? File.ReadAllText(path, Encoding.UTF8) : null;
            try
            {
                LauncherSettings s = new LauncherSettings();
                s.ServerUrl = LauncherSettings.DefaultLocalUrl;
                s.GamePath = @"D:\Games\nfsw\nfsw.exe";
                s.Region = "EU";
                s.ExtraArgs = "-test";
                s.Selected = 1;
                s.Servers.Add(new ServerEntry { Name = "Мой сервер", Url = "http://192.168.1.50:44600", Mode = "remote" });
                s.Save();

                LauncherSettings back = LauncherSettings.Load();
                Check(report, ref failures, "settings file exists", File.Exists(path), path);
                Check(report, ref failures, "serverUrl round-trip", back.ServerUrl == s.ServerUrl, back.ServerUrl);
                Check(report, ref failures, "gamePath round-trip", back.GamePath == s.GamePath, back.GamePath);
                Check(report, ref failures, "region round-trip", back.Region == "EU", back.Region);
                Check(report, ref failures, "extraArgs round-trip", back.ExtraArgs == "-test", back.ExtraArgs);
                Check(report, ref failures, "custom server persisted", back.Servers.Count == 3, back.Servers.Count.ToString());

                LaunchRequest r1 = ParseProtocol("dptnfsw://play?server=http%3A%2F%2F127.0.0.1%3A44620&local=1&region=EU");
                Check(report, ref failures, "protocol action", r1.Action == "play", r1.Action);
                Check(report, ref failures, "protocol server", r1.Server == "http://127.0.0.1:44620", r1.Server);
                Check(report, ref failures, "protocol local flag", r1.Local, r1.Local.ToString());
                Check(report, ref failures, "protocol region", r1.Region == "EU", r1.Region);

                LaunchRequest r2 = ParseProtocol("dptnfsw://play?server=nfsw.dartpower.xyz&args=-debug");
                Check(report, ref failures, "protocol without scheme", r2.Server == "nfsw.dartpower.xyz", r2.Server);
                Check(report, ref failures, "protocol extra args", r2.ExtraArgs == "-debug", r2.ExtraArgs);

                Check(report, ref failures, "normalize bare host", NormalizeServerUrl("nfsw.dartpower.xyz") == "http://nfsw.dartpower.xyz", "");
                Check(report, ref failures, "normalize Engine.svc", NormalizeServerUrl("http://h:44600/Engine.svc") == "http://h:44600", "");
                Check(report, ref failures, "args built", BuildGameArgs("http://h:44600/Engine.svc", "US", "") == "US http://h:44600/Engine.svc a 1", BuildGameArgs("http://h:44600/Engine.svc", "US", ""));
                Check(report, ref failures, "registry command quoted", CommandValue().Contains("%1") && CommandValue().StartsWith("\""), CommandValue());
                Check(report, ref failures, "protocol registered or installable", true, IsInstalled() ? "installed" : "not installed (--install)");
                Check(report, ref failures, "launcher.log written", File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log")), "");
            }
            catch (Exception ex)
            {
                report.AppendLine("EXCEPTION: " + ex);
                failures++;
            }
            finally
            {
                if (backup != null) File.WriteAllText(path, backup, new UTF8Encoding(false));
                else { try { File.Delete(path); } catch { } }
            }

            string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher-selftest.txt");
            report.AppendLine(failures == 0 ? "RESULT: ok" : "RESULT: " + failures + " failed");
            try { File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false)); } catch { }
            return failures == 0 ? 0 : 1;
        }

        private static void Check(StringBuilder report, ref int failures, string name, bool ok, string detail)
        {
            report.AppendLine((ok ? "[ok]   " : "[FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : " -> " + detail));
            if (!ok) failures++;
        }

        private static int ParseOnly(string raw)
        {
            LaunchRequest r = ParseProtocol(raw);
            Console.WriteLine("action=" + r.Action);
            Console.WriteLine("server=" + NormalizeServerUrl(r.Server));
            Console.WriteLine("region=" + r.Region);
            Console.WriteLine("local=" + (r.Local ? "1" : "0"));
            Console.WriteLine("args=" + BuildGameArgs(NormalizeServerUrl(r.Server) + "/Engine.svc", r.Region, r.ExtraArgs));
            return 0;
        }
    }
}