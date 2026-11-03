using System;
using Microsoft.Win32;

namespace Dpt.NfsWorld.Launcher
{
    /// <summary>
    /// The WinForms WebBrowser control uses whatever IE engine the OS offers. Without
    /// this registry switch it may fall back to IE7 mode, where the launcher UI would
    /// look like 2006 and ES5 features would be missing. HKCU only, no admin needed.
    /// </summary>
    internal static class BrowserEmulation
    {
        public static void Install()
        {
            try
            {
                string exe = System.IO.Path.GetFileName(Environment.GetCommandLineArgs()[0] ?? "NFSWorldLauncher.exe");
                SetDword(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION", exe, 11001);
                SetDword(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_GPU_RENDERING", exe, 1);
                SetDword(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_LOCALMACHINE_LOCKDOWN", exe, 0);
                SetDword(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BLOCK_LMZ_SCRIPT", exe, 0);
            }
            catch { }
        }

        private static void SetDword(string path, string name, int value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(path))
                {
                    if (key != null) key.SetValue(name, value, RegistryValueKind.DWord);
                }
            }
            catch { }
        }
    }
}