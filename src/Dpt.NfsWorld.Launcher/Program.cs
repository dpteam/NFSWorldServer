using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Dpt.NfsWorld.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
                return SelfTest();

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                BrowserEmulation.Install();
                LauncherBridge.LogLine("launcher " + LauncherBridge.VersionString + " starting");
                Application.Run(new LauncherForm());
                return 0;
            }
            catch (Exception ex)
            {
                LauncherBridge.LogLine("FATAL: " + ex);
                MessageBox.Show("Launcher failed to start:\r\n" + ex, "DPT.Server Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        /// <summary>
        /// Headless check used by tools\test-launcher.ps1: settings round-trip,
        /// URL normalisation, log file, and a live probe when a URL is passed.
        /// </summary>
        private static int SelfTest()
        {
            StringBuilder report = new StringBuilder();
            int failures = 0;
            try
            {
                string path = LauncherSettings.FilePath;
                bool existed = File.Exists(path);
                string backup = existed ? File.ReadAllText(path, Encoding.UTF8) : null;

                LauncherSettings s = new LauncherSettings();
                s.ServerUrl = LauncherSettings.DefaultLocalUrl;
                s.GamePath = @"D:\Games\nfsw\nfsw.exe";
                s.Region = "EU";
                s.ExtraArgs = "-test";
                s.Theme = "oled";
                s.Sound = false;
                s.CloseOnPlay = false;
                s.Selected = 1;
                s.Servers.Add(new ServerEntry { Name = "Мой сервер", Url = "http://192.168.1.50:44600", Mode = "remote" });
                s.Save();

                LauncherSettings back = LauncherSettings.Load();
                Check(report, ref failures, "settings file exists", File.Exists(path), path);
                Check(report, ref failures, "serverUrl round-trip", back.ServerUrl == s.ServerUrl, back.ServerUrl);
                Check(report, ref failures, "gamePath round-trip", back.GamePath == s.GamePath, back.GamePath);
                Check(report, ref failures, "region round-trip", back.Region == "EU", back.Region);
                Check(report, ref failures, "extraArgs round-trip", back.ExtraArgs == "-test", back.ExtraArgs);
                Check(report, ref failures, "theme round-trip", back.Theme == "oled", back.Theme);
                Check(report, ref failures, "sound flag round-trip", back.Sound == false, back.Sound.ToString());
                Check(report, ref failures, "selected index round-trip", back.Selected == 1, back.Selected.ToString());
                Check(report, ref failures, "custom server persisted", back.Servers.Count == 3, back.Servers.Count.ToString());
                Check(report, ref failures, "custom server name with spaces", back.Servers[2].Name == "Мой сервер", back.Servers[2].Name);
                Check(report, ref failures, "local/remote modes kept",
                      back.Servers[0].IsLocal && !back.Servers[2].IsLocal,
                      back.Servers[0].Mode + "/" + back.Servers[2].Mode);

                Check(report, ref failures, "url without scheme", LauncherBridge.NormalizeServerUrl("nfsw.dartpower.xyz") == "http://nfsw.dartpower.xyz", "");
                Check(report, ref failures, "url with Engine.svc", LauncherBridge.NormalizeServerUrl("http://host:44600/Engine.svc") == "http://host:44600", "");
                Check(report, ref failures, "url with trailing slash", LauncherBridge.NormalizeServerUrl("http://host:44600/") == "http://host:44600", "");

                LauncherBridge.LogLine("selftest run");
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");
                Check(report, ref failures, "launcher.log written", File.Exists(logPath), logPath);

                // restore whatever the user had
                if (backup != null) File.WriteAllText(path, backup, new UTF8Encoding(false));
                else { try { File.Delete(path); } catch { } }
            }
            catch (Exception ex)
            {
                report.AppendLine("EXCEPTION: " + ex);
                failures++;
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
    }
}