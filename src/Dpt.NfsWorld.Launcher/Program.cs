using System;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Dpt.NfsWorld.Launcher
{
    /// <summary>
    /// Entry point. Two ways in:
    ///   * dptnfsw://play?...  - protocol handler, starts the game (and the offline server)
    ///   * no arguments        - opens the WebView2 launcher window
    /// </summary>
    internal static class Program
    {
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

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                if (mode.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
                    return RunProtocol(mode);

                Application.Run(new LauncherForm());
                return 0;
            }
            catch (Exception ex)
            {
                LaunchService.Log("FATAL: " + ex);
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

        public static LaunchRequest ParseProtocol(string raw)
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

        private static int RunProtocol(string raw)
        {
            LaunchRequest req = ParseProtocol(raw);
            LauncherSettings settings = LauncherSettings.Load();
            LaunchService.Log("protocol: " + req.Action + " server=" + req.Server + " local=" + req.Local);

            if (req.Action != "play" && req.Action != "launch")
            {
                LaunchService.Log("unknown action: " + req.Action);
                return 2;
            }

            LaunchService.PlayResult result = LaunchService.Launch(
                req.Server,
                string.IsNullOrEmpty(req.GamePath) ? settings.GamePath : req.GamePath,
                req.Region, req.ExtraArgs, req.Local);

            if (result.NeedGamePath)
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Выберите nfsw.exe (Need For Speed World)";
                    dlg.Filter = "Need For Speed World (nfsw.exe)|nfsw.exe|Программы (*.exe)|*.exe|Все файлы (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    if (dlg.ShowDialog() != DialogResult.OK) return 5;
                    settings.GamePath = dlg.FileName;
                    settings.Save();
                    LaunchService.Log("game path saved: " + dlg.FileName);
                }
                result = LaunchService.Launch(req.Server, settings.GamePath, req.Region, req.ExtraArgs, req.Local);
            }

            if (!result.Ok)
            {
                MessageBox.Show(result.Message, "DPT.Server Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 4;
            }
            settings.ServerUrl = result.Server;
            settings.Save();
            return 0;
        }

        // ------------------------------------------------------------- registry

        public static string CommandValue()
        {
            return "\"" + Application.ExecutablePath + "\" \"%1\"";
        }

        public static bool IsInstalled()
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
                    LaunchService.Log("protocol handler removed");
                    MessageBox.Show(Scheme + ":// больше не открывает лаунчер.", "DPT.Server Launcher",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { LaunchService.Log("uninstall failed: " + ex.Message); }
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
                LaunchService.Log("protocol handler installed: " + Scheme + ":// -> " + Application.ExecutablePath);
                return 0;
            }
            catch (Exception ex)
            {
                LaunchService.Log("install failed: " + ex.Message);
                MessageBox.Show("Не удалось зарегистрировать " + Scheme + "://\r\n" + ex.Message,
                    "DPT.Server Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // -------------------------------------------------------------- selftest

        private static int SelfTest()
        {
            LaunchService.Log("selftest run");
            StringBuilder report = new StringBuilder();
            int failures = 0;

            string path = LauncherSettings.FilePath;
            bool existed = System.IO.File.Exists(path);
            string backup = existed ? System.IO.File.ReadAllText(path, Encoding.UTF8) : null;
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
                Check(report, ref failures, "settings file exists", System.IO.File.Exists(path), path);
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

                Check(report, ref failures, "normalize bare host", LaunchService.NormalizeServerUrl("nfsw.dartpower.xyz") == "http://nfsw.dartpower.xyz", "");
                Check(report, ref failures, "normalize Engine.svc", LaunchService.NormalizeServerUrl("http://h:44600/Engine.svc") == "http://h:44600", "");
                Check(report, ref failures, "args built",
                      LaunchService.BuildGameArgs("http://h:44600/Engine.svc", "US", "") == "US http://h:44600/Engine.svc a 1",
                      LaunchService.BuildGameArgs("http://h:44600/Engine.svc", "US", ""));
                Check(report, ref failures, "registry command quoted", CommandValue().Contains("%1") && CommandValue().StartsWith("\""), CommandValue());
                Check(report, ref failures, "protocol registered or installable", true, IsInstalled() ? "installed" : "not installed (--install)");

                string wv2 = "missing";
                try { wv2 = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
                catch (Exception wex) { wv2 = "missing (" + wex.Message + ")"; }
                Check(report, ref failures, "WebView2 runtime", true, wv2);

                // the window is a blank white rectangle without these three
                string markup = LauncherForm.Resource("LauncherUi.html");
                string style = LauncherForm.Resource("LauncherTheme.css");
                string script = LauncherForm.Resource("LauncherApp.js");
                Check(report, ref failures, "ui.html embedded", markup.Length > 500, markup.Length + " chars");
                Check(report, ref failures, "theme.css embedded", style.Length > 500, style.Length + " chars");
                Check(report, ref failures, "app.js talks to the host",
                      script.IndexOf("chrome.webview", StringComparison.Ordinal) >= 0, script.Length + " chars");

                Check(report, ref failures, "launcher.log written",
                      System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log")), "");
            }
            catch (Exception ex)
            {
                report.AppendLine("EXCEPTION: " + ex);
                failures++;
            }
            finally
            {
                if (backup != null) System.IO.File.WriteAllText(path, backup, new UTF8Encoding(false));
                else { try { System.IO.File.Delete(path); } catch { } }
            }

            string outPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher-selftest.txt");
            report.AppendLine(failures == 0 ? "RESULT: ok" : "RESULT: " + failures + " failed");
            try { System.IO.File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false)); } catch { }
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
            Console.WriteLine("server=" + LaunchService.NormalizeServerUrl(r.Server));
            Console.WriteLine("region=" + r.Region);
            Console.WriteLine("local=" + (r.Local ? "1" : "0"));
            Console.WriteLine("args=" + LaunchService.BuildGameArgs(LaunchService.NormalizeServerUrl(r.Server) + "/Engine.svc", r.Region, r.ExtraArgs));
            return 0;
        }
    }
}