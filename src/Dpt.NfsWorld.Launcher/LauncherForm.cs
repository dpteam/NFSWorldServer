using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Dpt.NfsWorld.Launcher
{
    public sealed class LauncherForm : Form
    {
        private readonly WebBrowser _browser;
        private readonly Bridge _bridge;

        public LauncherForm()
        {
            Text = "DPT.Server - NFS World Launcher";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(780, 620);
            MinimumSize = new Size(680, 560);
            BackColor = Color.FromArgb(20, 26, 33);
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _browser = new WebBrowser();
            _browser.Dock = DockStyle.Fill;
            _browser.IsWebBrowserContextMenuEnabled = false;
            _browser.AllowWebBrowserDrop = false;
            _browser.ScriptErrorsSuppressed = true;
            _browser.WebBrowserShortcutsEnabled = true;
            _bridge = new Bridge(this);
            _browser.ObjectForScripting = _bridge;
            Controls.Add(_browser);

            Load += delegate { LoadUi(); };
        }

        private static string Resource(string name)
        {
            try
            {
                using (Stream s = typeof(LauncherForm).Assembly.GetManifestResourceStream(name))
                {
                    if (s == null) return "";
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                        return r.ReadToEnd();
                }
            }
            catch { return ""; }
        }

        private void LoadUi()
        {
            string html = Resource("LauncherUi.html");
            if (string.IsNullOrEmpty(html))
            {
                MessageBox.Show("Launcher resources are missing. Rebuild with build.cmd.", "DPT.Server Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string css = Resource("LauncherTheme.css");
            string js = Resource("LauncherApp.js");
            html = html.Replace("<!--THEME-->", "<style>\r\n" + css + "\r\n</style>");
            html = html.Replace("<!--APP-->", "<script>\r\n" + js + "\r\n</script>");
            html = html.Replace("{{VERSION}}", Bridge.VersionString);
            _browser.DocumentText = html;
        }

        // ------------------------------------------------------------- bridge

        [ComVisible(true)]
        public sealed class Bridge
        {
            public const string VersionString = "0.1.0";
            private readonly LauncherForm _form;

            public Bridge(LauncherForm form) { _form = form; }

            public string Version() { return VersionString; }

            public string BaseDir()
            {
                try { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
                catch { return ""; }
            }

            /// <summary>Settings as "key=value" lines (simplest thing to parse in old IE).</summary>
            public string GetSettings()
            {
                LauncherSettings s = LauncherSettings.Load();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("serverUrl=" + s.ServerUrl);
                sb.AppendLine("gamePath=" + s.GamePath);
                sb.AppendLine("region=" + s.Region);
                sb.AppendLine("extraArgs=" + s.ExtraArgs);
                sb.AppendLine("theme=" + s.Theme);
                sb.AppendLine("sound=" + (s.Sound ? "1" : "0"));
                sb.AppendLine("closeOnPlay=" + (s.CloseOnPlay ? "1" : "0"));
                return sb.ToString();
            }

            public void SaveSettings(string serverUrl, string gamePath, string region, string extraArgs, string theme, string sound, string closeOnPlay)
            {
                LauncherSettings s = new LauncherSettings();
                s.ServerUrl = (serverUrl ?? "").Trim();
                s.GamePath = (gamePath ?? "").Trim();
                s.Region = (region ?? "US").Trim().ToUpperInvariant();
                s.ExtraArgs = (extraArgs ?? "").Trim();
                s.Theme = string.IsNullOrEmpty(theme) ? "dark" : theme.Trim();
                s.Sound = sound == "1";
                s.CloseOnPlay = closeOnPlay == "1";
                s.Save();
            }

            /// <summary>Blocking HTTP GET used by the launcher page (IE cannot do CORS to localhost).</summary>
            public string HttpGet(string url)
            {
                if (string.IsNullOrEmpty(url)) return "ERROR: empty url";
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.Timeout = 5000;
                    req.ReadWriteTimeout = 5000;
                    req.UserAgent = "DPT.Server-Launcher/" + VersionString;
                    req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
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

            public string NormalizeEngineUrl(string serverUrl)
            {
                return BuildEngineUrl(serverUrl);
            }

            public string Play(string serverUrl, string gamePath, string region, string extraArgs)
            {
                try
                {
                    string engine = BuildEngineUrl(serverUrl);
                    string exe = ResolveGame(gamePath);
                    if (exe == null)
                        return "ERROR: nfsw.exe not found. Set the game folder in the launcher.";

                    string args = (string.IsNullOrEmpty(region) ? "US" : region.Trim().ToUpperInvariant()) +
                                  " " + engine + " a 1";
                    if (!string.IsNullOrEmpty(extraArgs)) args += " " + extraArgs.Trim();

                    ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                    psi.WorkingDirectory = Path.GetDirectoryName(exe);
                    psi.UseShellExecute = true;
                    Process.Start(psi);

                    LauncherSettings s = LauncherSettings.Load();
                    s.ServerUrl = (serverUrl ?? "").Trim();
                    s.GamePath = (gamePath ?? "").Trim();
                    s.Region = string.IsNullOrEmpty(region) ? "US" : region.Trim().ToUpperInvariant();
                    s.ExtraArgs = (extraArgs ?? "").Trim();
                    s.Save();

                    if (s.CloseOnPlay)
                    {
                        try { _form.BeginInvoke(new MethodInvoker(delegate { _form.Close(); })); } catch { }
                    }
                    return "OK " + exe + " " + args;
                }
                catch (Exception ex)
                {
                    return "ERROR: " + ex.Message;
                }
            }

            public string BrowseGame()
            {
                try
                {
                    using (OpenFileDialog dlg = new OpenFileDialog())
                    {
                        dlg.Title = "Select nfsw.exe";
                        dlg.Filter = "Need For Speed World (nfsw.exe)|nfsw.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*";
                        LauncherSettings s = LauncherSettings.Load();
                        string guess = ResolveGame(s.GamePath);
                        if (guess != null) dlg.FileName = guess;
                        if (dlg.ShowDialog(_form) == DialogResult.OK)
                            return dlg.FileName;
                    }
                }
                catch (Exception ex) { return "ERROR: " + ex.Message; }
                return "";
            }

            public void OpenLink(string url)
            {
                if (string.IsNullOrEmpty(url)) return;
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { }
            }

            public void ShowMessage(string title, string text)
            {
                try { MessageBox.Show(_form, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information); }
                catch { }
            }

            public void CloseApp()
            {
                try { _form.BeginInvoke(new MethodInvoker(delegate { _form.Close(); })); } catch { }
            }

            public void Minimize()
            {
                try { _form.BeginInvoke(new MethodInvoker(delegate { _form.WindowState = FormWindowState.Minimized; })); } catch { }
            }

            // ------------------------------------------------------- helpers

            public static string BuildEngineUrl(string serverUrl)
            {
                string url = (serverUrl ?? "").Trim();
                if (url.Length == 0) url = "http://127.0.0.1:44600";
                if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "http://" + url;
                url = url.TrimEnd('/');
                if (url.IndexOf("/Engine.svc", StringComparison.OrdinalIgnoreCase) >= 0) return url;
                return url + "/Engine.svc";
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
        }
    }

    /// <summary>Tiny ini-ish settings file next to the launcher.</summary>
    public sealed class LauncherSettings
    {
        public string ServerUrl = "http://127.0.0.1:44600";
        public string GamePath = "nfsw\\nfsw.exe";
        public string Region = "US";
        public string ExtraArgs = "";
        public string Theme = "dark";
        public bool Sound = true;
        public bool CloseOnPlay = true;

        private static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.ini"); }
        }

        public static LauncherSettings Load()
        {
            LauncherSettings s = new LauncherSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
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
                        case "sound": s.Sound = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "closeonplay": s.CloseOnPlay = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("; DPT.Server launcher settings");
                sb.AppendLine("serverUrl=" + ServerUrl);
                sb.AppendLine("gamePath=" + GamePath);
                sb.AppendLine("region=" + Region);
                sb.AppendLine("extraArgs=" + ExtraArgs);
                sb.AppendLine("theme=" + Theme);
                sb.AppendLine("sound=" + (Sound ? "1" : "0"));
                sb.AppendLine("closeOnPlay=" + (CloseOnPlay ? "1" : "0"));
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}