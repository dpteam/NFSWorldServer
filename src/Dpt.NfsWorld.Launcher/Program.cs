using System;
using System.Windows.Forms;

namespace Dpt.NfsWorld.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                BrowserEmulation.Install();
                Application.Run(new LauncherForm());
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Launcher failed to start:\r\n" + ex, "DPT.Server Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}