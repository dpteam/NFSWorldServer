using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Dpt.NfsWorld.Launcher
{
    public sealed class LauncherForm : Form
    {
        private readonly WebBrowser _browser;
        private readonly LauncherBridge _bridge;

        public LauncherForm()
        {
            Text = "DPT.Server - NFS World Launcher";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 660);
            MinimumSize = new Size(700, 580);
            BackColor = Color.FromArgb(18, 24, 33);
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _browser = new WebBrowser();
            _browser.Dock = DockStyle.Fill;
            _browser.IsWebBrowserContextMenuEnabled = false;
            _browser.AllowWebBrowserDrop = false;
            _browser.ScriptErrorsSuppressed = true;
            _browser.WebBrowserShortcutsEnabled = true;
            _bridge = new LauncherBridge(this);
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
            html = html.Replace("{{VERSION}}", LauncherBridge.VersionString);
            LauncherBridge.LogLine("UI loaded (resources " + html.Length + " bytes)");
            _browser.DocumentText = html;
        }
    }
}