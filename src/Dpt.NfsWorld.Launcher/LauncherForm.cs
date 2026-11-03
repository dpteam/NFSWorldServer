using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Dpt.NfsWorld.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Dpt.NfsWorld.Launcher
{
    /// <summary>
    /// WebView2 shell for the launcher UI. The page talks to this window with
    /// chrome.webview.postMessage, so there is no COM scripting bridge and no
    /// browser protocol prompt - Play just works.
    /// </summary>
    public sealed class LauncherForm : Form
    {
        private readonly WebView2 _web;
        private string _html = "";
        private bool? _lastCheck;

        public LauncherForm()
        {
            Text = "DPT.Server - NFS World Launcher";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(940, 700);
            MinimumSize = new Size(760, 600);
            BackColor = Color.FromArgb(18, 24, 33);
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _web = new WebView2();
            _web.Dock = DockStyle.Fill;
            Controls.Add(_web);

            Shown += delegate { BeginInit(); };
            FormClosed += delegate { try { _web.Dispose(); } catch { } };
        }

        private async void BeginInit()
        {
            try
            {
                _html = BuildHtml();
                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DPT.Server", "WebView2");
                Directory.CreateDirectory(userData);

                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userData);
                await _web.EnsureCoreWebView2Async(env);

                CoreWebView2 core = _web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.WebMessageReceived += OnWebMessage;
                core.NavigateToString(_html);
                LaunchService.Log("webview2 " + core.Environment.BrowserVersionString + " ready");
            }
            catch (Exception ex)
            {
                LaunchService.Log("webview2 init failed: " + ex);
                MessageBox.Show(
                    "WebView2 не запустился:\r\n" + ex.Message +
                    "\r\n\r\nУстановите Microsoft Edge WebView2 Runtime:\r\nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    "DPT.Server Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                OpenBrowserFallback();
                Close();
            }
        }

        private string BuildHtml()
        {
            string html = Resource("LauncherUi.html");
            if (string.IsNullOrEmpty(html))
                html = "<!doctype html><html><body style='font:14px Segoe UI;background:#121820;color:#dbe4ee;padding:24px'>" +
                       "<h1>DPT.Server</h1><p>UI resources are missing. Rebuild with build.cmd.</p></body></html>";
            html = html.Replace("<!--THEME-->", "<style>\r\n" + Resource("LauncherTheme.css") + "\r\n</style>");
            html = html.Replace("<!--APP-->", "<script>\r\n" + Resource("LauncherApp.js") + "\r\n</script>");
            html = html.Replace("{{VERSION}}", LaunchService.VersionString);
            return html;
        }

        public static string Resource(string name)
        {
            try
            {
                using (Stream s = typeof(LauncherForm).Assembly.GetManifestResourceStream(name))
                {
                    if (s == null) return "";
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
                }
            }
            catch { return ""; }
        }

        private void OpenBrowserFallback()
        {
            try
            {
                string url = LaunchService.NormalizeServerUrl(LauncherSettings.Load().ServerUrl);
                Uri u = new Uri(url);
                string page = u.Scheme + "://" + u.Host + ":44601/launcher/";
                Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
                LaunchService.Log("fallback page: " + page);
            }
            catch (Exception ex) { LaunchService.Log("fallback failed: " + ex.Message); }
        }

        // ---------------------------------------------------------- messaging

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg = null;
            try { msg = Json.TryParseObject(e.WebMessageAsJson); }
            catch { }
            if (msg == null) return;

            string action = Json.GetString(msg, "action", "");
            LaunchService.Log("message: " + action);
            switch (action)
            {
                case "ready": SendSettings(); break;
                case "save": SaveFromPage(msg); break;
                case "check": CheckFromPage(msg); break;
                case "play": PlayFromPage(msg); break;
                case "browse": BrowseGame(); break;
                case "openSite": OpenSite(msg); break;
                case "installHandler": InstallHandler(); break;
                case "close": Close(); break;
                default: Send(new Dictionary<string, object> { { "type", "error" }, { "message", "unknown action: " + action } }); break;
            }
        }

        private void Send(Dictionary<string, object> payload)
        {
            try
            {
                string json = Json.Stringify(payload);
                Action deliver = delegate
                {
                    try
                    {
                        if (_web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
                    }
                    catch (Exception ex) { LaunchService.Log("post failed: " + ex.Message); }
                };
                if (InvokeRequired) BeginInvoke(deliver);
                else deliver();
            }
            catch (Exception ex) { LaunchService.Log("post queue failed: " + ex.Message); }
        }

        private void SendSettings()
        {
            LauncherSettings s = LauncherSettings.Load();
            List<object> servers = new List<object>();
            for (int i = 0; i < s.Servers.Count; i++)
            {
                ServerEntry e = s.Servers[i];
                servers.Add(new Dictionary<string, object>
                {
                    { "name", e.Name }, { "url", e.Url }, { "mode", e.Mode }, { "builtin", e.BuiltIn }
                });
            }
            string runtime = "";
            try { runtime = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            Send(new Dictionary<string, object>
            {
                { "type", "settings" },
                { "version", LaunchService.VersionString },
                { "webview2", runtime },
                { "servers", servers },
                { "selected", (long)s.Selected },
                { "serverUrl", s.ServerUrl },
                { "gamePath", s.GamePath },
                { "region", s.Region },
                { "extraArgs", s.ExtraArgs },
                { "theme", s.Theme },
                { "sound", s.Sound }
            });
        }

        private void SaveFromPage(Dictionary<string, object> msg)
        {
            LauncherSettings s = LauncherSettings.Load();
            s.ServerUrl = Json.GetString(msg, "serverUrl", s.ServerUrl);
            s.GamePath = Json.GetString(msg, "gamePath", s.GamePath);
            s.Region = Json.GetString(msg, "region", s.Region);
            s.ExtraArgs = Json.GetString(msg, "extraArgs", s.ExtraArgs);
            s.Theme = Json.GetString(msg, "theme", s.Theme);
            s.Sound = Json.GetBool(msg, "sound", s.Sound);
            s.Selected = Json.GetInt(msg, "selected", s.Selected);

            List<object> servers = Json.Get(msg, "servers") as List<object>;
            if (servers != null && servers.Count > 0)
            {
                List<ServerEntry> list = new List<ServerEntry>();
                for (int i = 0; i < servers.Count; i++)
                {
                    Dictionary<string, object> d = servers[i] as Dictionary<string, object>;
                    if (d == null) continue;
                    list.Add(new ServerEntry
                    {
                        Name = Json.GetString(d, "name", ""),
                        Url = Json.GetString(d, "url", ""),
                        Mode = Json.GetString(d, "mode", "remote"),
                        BuiltIn = Json.GetBool(d, "builtin", false)
                    });
                }
                if (list.Count > 0) s.Servers = list;
            }
            if (s.Selected < 0 || s.Selected >= s.Servers.Count) s.Selected = 0;
            s.Save();
        }

        private void CheckFromPage(Dictionary<string, object> msg)
        {
            string server = LaunchService.NormalizeServerUrl(Json.GetString(msg, "server", ""));
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = LaunchService.IsHealthy(server, 4000);
                // the page polls every 15 s - log only when the answer changes
                bool? last = _lastCheck;
                if (last == null || last.Value != ok)
                {
                    _lastCheck = ok;
                    LaunchService.Log("check " + server + " -> " + (ok ? "ok" : "offline"));
                }
                string status = "";
                if (ok)
                {
                    try { status = LaunchService.HttpGet(server + "/api/status", 5000); }
                    catch { status = ""; }
                }
                Send(new Dictionary<string, object>
                {
                    { "type", "check" }, { "ok", ok }, { "server", server }, { "status", status }
                });
            });
        }

        private void PlayFromPage(Dictionary<string, object> msg)
        {
            string server = Json.GetString(msg, "server", "");
            string region = Json.GetString(msg, "region", "US");
            string extra = Json.GetString(msg, "extraArgs", "");
            bool local = Json.GetBool(msg, "local", false);
            LauncherSettings settings = LauncherSettings.Load();
            string gamePath = Json.GetString(msg, "gamePath", settings.GamePath);

            Send(new Dictionary<string, object> { { "type", "playStarted" }, { "server", server } });

            ThreadPool.QueueUserWorkItem(delegate
            {
                LaunchService.PlayResult r = LaunchService.Launch(server, gamePath, region, extra, local);
                settings.ServerUrl = r.Server;
                if (!r.NeedGamePath && !string.IsNullOrEmpty(r.Exe)) settings.GamePath = r.Exe;
                settings.Save();
                Send(new Dictionary<string, object>
                {
                    { "type", "play" },
                    { "ok", r.Ok },
                    { "needGame", r.NeedGamePath },
                    { "message", r.Message },
                    { "exe", r.Exe },
                    { "args", r.Args },
                    { "server", r.Server }
                });
            });
        }

        private void BrowseGame()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Выберите nfsw.exe (Need For Speed World)";
                dlg.Filter = "Need For Speed World (nfsw.exe)|nfsw.exe|Программы (*.exe)|*.exe|Все файлы (*.*)|*.*";
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    Send(new Dictionary<string, object> { { "type", "gamePath" }, { "path", "" } });
                    return;
                }
                LauncherSettings s = LauncherSettings.Load();
                s.GamePath = dlg.FileName;
                s.Save();
                LaunchService.Log("game path: " + dlg.FileName);
                Send(new Dictionary<string, object> { { "type", "gamePath" }, { "path", dlg.FileName } });
            }
        }

        private void OpenSite(Dictionary<string, object> msg)
        {
            string url = Json.GetString(msg, "url", "");
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { LaunchService.Log("openSite failed: " + ex.Message); }
        }

        private void InstallHandler()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\dptnfsw"))
                {
                    root.SetValue(null, "URL:DPT NFS World Launcher");
                    root.SetValue("URL Protocol", "");
                    using (Microsoft.Win32.RegistryKey cmd = root.CreateSubKey(@"shell\open\command"))
                        cmd.SetValue(null, Program.CommandValue());
                }
                Send(new Dictionary<string, object> { { "type", "handler" }, { "ok", true } });
            }
            catch (Exception ex)
            {
                Send(new Dictionary<string, object> { { "type", "handler" }, { "ok", false }, { "message", ex.Message } });
            }
        }
    }
}