using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Dpt.NfsWorld.Core;
using Dpt.NfsWorld.Engine;
using Dpt.NfsWorld.Net;
using Dpt.NfsWorld.Web;

namespace Dpt.NfsWorld
{
    internal static class Program
    {
        private static ServerApp _app;
        private static AppSupervisor _supervisor;
        private static ConsoleUi _ui;
        private static readonly ManualResetEvent StopEvent = new ManualResetEvent(false);
        private static int _exitCode;

        private static int Main(string[] args)
        {
            try { Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory); } catch { }

            bool headless = false;
            string levelOverride = null;
            string profileOverride = null;
            int? enginePort = null;
            int? publicPort = null;
            int? adminPort = null;
            int? xmppPort = null;
            int? relayPort = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                string next = i + 1 < args.Length ? args[i + 1] : null;
                if (Eq(a, "--headless") || Eq(a, "-q")) headless = true;
                else if (Eq(a, "--help") || Eq(a, "-h")) { PrintUsage(); return 0; }
                else if (Eq(a, "--version") || Eq(a, "-v")) { Console.WriteLine(ServerApp.Name + " v" + ServerApp.Version); return 0; }
                else if (Eq(a, "--level") && next != null) { levelOverride = next; i++; }
                else if (Eq(a, "--profile") && next != null) { profileOverride = next; i++; }
                else if (Eq(a, "--engine-port") && next != null) { enginePort = Util.ParseInt(next, 0); i++; }
                else if (Eq(a, "--public-port") && next != null) { publicPort = Util.ParseInt(next, 0); i++; }
                else if (Eq(a, "--admin-port") && next != null) { adminPort = Util.ParseInt(next, 0); i++; }
                else if (Eq(a, "--xmpp-port") && next != null) { xmppPort = Util.ParseInt(next, 0); i++; }
                else if (Eq(a, "--relay-port") && next != null) { relayPort = Util.ParseInt(next, 0); i++; }
                else if (Eq(a, "--firewall"))
                {
                    PrintFirewall(Util.ParseInt(next, 44600));
                    return 0;
                }
                else if (a.StartsWith("-", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("Unknown option: " + a);
                    PrintUsage();
                    return 1;
                }
            }

            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            Console.CancelKeyPress += OnCancelKey;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Config.CopyExampleIfMissing(baseDir);
            Config cfg = Config.Load(baseDir);
            if (!string.IsNullOrEmpty(levelOverride)) cfg.LogLevel = Config.ParseLevel(levelOverride);
            if (!string.IsNullOrEmpty(profileOverride)) cfg.Profile = profileOverride;
            if (enginePort.HasValue && enginePort.Value > 0) cfg.EnginePort = enginePort.Value;
            if (publicPort.HasValue && publicPort.Value > 0) cfg.PublicPort = publicPort.Value;
            if (adminPort.HasValue && adminPort.Value > 0) cfg.AdminPort = adminPort.Value;
            if (xmppPort.HasValue && xmppPort.Value > 0) cfg.XmppPort = xmppPort.Value;
            if (relayPort.HasValue && relayPort.Value > 0) cfg.RelayPort = relayPort.Value;
            cfg.Normalize();
            cfg.EnsureSecrets();

            Log.Configure(cfg);
            Banner(cfg);

            try
            {
                Directory.CreateDirectory(cfg.LogDir);
                Directory.CreateDirectory(cfg.StatePath);
                Directory.CreateDirectory(cfg.ProfileRoot);
            }
            catch (Exception ex)
            {
                Log.Error("APP", "cannot prepare directories: " + ex.Message);
            }

            _app = new ServerApp();
            _app.Cfg = cfg;
            _app.Events = new EventFeed(500);
            _app.Db = new DataStore(cfg);
            _app.Profiles = new ProfileStore(cfg.ProfileRoot, cfg.Profile);
            _app.Sessions = new SessionManager(cfg);

            _app.Engine = new EngineServer(_app);
            _app.Xmpp = new XmppServer(_app);
            _app.Relay = new RelayServer(_app);
            _app.WebPublic = new WebServer(_app, WebRole.Public);
            _app.WebAdmin = new WebServer(_app, WebRole.Admin);

            Log.Info("APP", "starting " + ServerApp.Name + " v" + ServerApp.Version +
                            " (CLR " + Environment.Version + ", " + Environment.OSVersion.VersionString + ")");
            Log.Info("APP", "working directory: " + Environment.CurrentDirectory);
            Log.Info("APP", "ports: " + cfg.DescribePorts());
            Log.Info("APP", "public url: " + (string.IsNullOrEmpty(cfg.PublicUrl) ? "(not set)" : cfg.PublicUrl));

            bool fatal = false;

            if (cfg.EngineEnabled)
            {
                if (!_app.Engine.Start())
                {
                    Log.Error("APP", "game endpoint could not start");
                    fatal = true;
                }
            }
            else
            {
                Log.Warn("APP", "engine.enabled=false: the game endpoint is off");
            }

            if (cfg.XmppEnabled)
            {
                if (!_app.Xmpp.Start())
                    Log.Warn("APP", "EA-Chat (XMPP) did not start; in-game chat and lobby invites stay unavailable");
            }
            else
            {
                Log.Info("APP", "xmpp.enabled=false: EA-Chat is off");
            }

            if (cfg.RelayEnabled)
            {
                if (!_app.Relay.Start())
                    Log.Warn("APP", "UDP relay did not start; hairpin/NAT players will not be able to use it");
            }
            else
            {
                Log.Info("APP", "relay.enabled=false: UDP relay is off");
            }

            if (!_app.WebPublic.Start())
                Log.Error("APP", "public board could not start; the game endpoint keeps running");
            if (!_app.WebAdmin.Start())
                Log.Error("APP", "admin board could not start; the game endpoint keeps running");

            if (fatal && cfg.RequireAllServices)
            {
                _exitCode = 2;
                Shutdown(headless);
                return _exitCode;
            }

            _app.Events.Add("start", "server started", "server");

            _supervisor = new AppSupervisor(_app);
            _supervisor.Start();

            Log.Info("APP", "ready. uptime counter started, " + cfg.DescribePorts());
            Log.Info("APP", "public board: " + _app.WebPublic.BaseUrl);
            Log.Info("APP", "admin board : " + _app.WebAdmin.BaseUrl + " (token in config.ini)");
            Log.Info("APP", "launcher    : NFSWorldLauncher.exe " + (string.IsNullOrEmpty(cfg.PublicUrl) ? "" : "(url " + cfg.PublicUrl + ")"));

            PrintStartupSummary(cfg);

            if (!headless)
            {
                _ui = new ConsoleUi(_app, RequestShutdown);
                _ui.Start();
                PrintAdminHint(cfg);
            }
            else
            {
                Log.Info("APP", "headless mode: Ctrl+C to stop");
            }

            StopEvent.WaitOne();
            Shutdown(headless);
            return _exitCode;
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintUsage()
        {
            Console.WriteLine(ServerApp.Name + " v" + ServerApp.Version);
            Console.WriteLine("  --engine-port N   game endpoint (default 44600)");
            Console.WriteLine("  --public-port N   public board (default 44601)");
            Console.WriteLine("  --admin-port N    admin board (default 44602)");
            Console.WriteLine("  --xmpp-port N     EA-Chat (default 44603)");
            Console.WriteLine("  --relay-port N    UDP relay (default 44610)");
            Console.WriteLine("  --profile NAME    force a client profile (auto by default)");
            Console.WriteLine("  --level X         Error|Warn|Info|Debug|Verbose");
            Console.WriteLine("  --headless        no interactive console");
            Console.WriteLine("  --firewall        print the netsh commands and exit");
            Console.WriteLine("  --help, --version");
        }

        private static void Banner(Config cfg)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine();
                Console.WriteLine("  ____  ____ _____     ____                            ");
                Console.WriteLine(" |  _ \\|  _ \\_   _|   / ___|  ___ _ ____   _____ _ __  ");
                Console.WriteLine(" | | | | |_) || |     \\___ \\ / _ \\ '__\\ \\ / / _ \\ '__| ");
                Console.WriteLine(" | |_| |  __/ | |      ___) |  __/ |   \\ V /  __/ |    ");
                Console.WriteLine(" |____/|_|    |_|     |____/ \\___|_|    \\_/ \\___|_|    ");
                Console.ResetColor();
                Console.WriteLine("  DPT.Server - Need For Speed World");
                Console.WriteLine("  engine " + cfg.EnginePort + " | public " + cfg.PublicPort + " | admin " + cfg.AdminPort +
                                  " | xmpp " + cfg.XmppPort + " | relay " + cfg.RelayPort);
                Console.WriteLine("  .NET 4.0 / C# 4 - Windows XP SP3 ready - no external libraries");
                Console.WriteLine();
            }
            catch { }
        }

        private static void PrintStartupSummary(Config cfg)
        {
            try
            {
                string host = "localhost";
                string[] lan = Util.LocalIPv4();
                if (lan.Length > 0) host = lan[0];
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  READY - the server is running, this window is the console");
                Console.ResetColor();
                Console.WriteLine("  engine : http://" + host + ":" + cfg.EnginePort + "/Engine.svc   (nfsw.exe)");
                Console.WriteLine("  public : http://" + host + ":" + cfg.PublicPort + "/");
                Console.WriteLine("  admin  : http://" + host + ":" + cfg.AdminPort + "/   (LAN only)");
                Console.WriteLine("  xmpp   : " + host + ":" + cfg.XmppPort + " (TLS)   relay: udp " + host + ":" + cfg.RelayPort);
                Console.WriteLine("  logs   : " + cfg.LogDir);
                Console.WriteLine();
            }
            catch { }
        }

        private static void PrintAdminHint(Config cfg)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  ADMIN BOARD (LAN only): " + _app.WebAdmin.BaseUrl);
                Console.WriteLine("  admin token:            " + cfg.AdminToken);
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  token is saved in config.ini (web.adminToken) and sent as the X-Admin-Token header.");
                Console.ResetColor();
                Console.WriteLine();
            }
            catch { }
        }

        private static void PrintFirewall(int enginePort)
        {
            Console.WriteLine("netsh advfirewall firewall add rule name=\"DPT.Server Engine\" dir=in action=allow protocol=TCP localport=" + enginePort);
            Console.WriteLine("netsh advfirewall firewall add rule name=\"DPT.Server XMPP\" dir=in action=allow protocol=TCP localport=44603");
            Console.WriteLine("netsh advfirewall firewall add rule name=\"DPT.Server Relay\" dir=in action=allow protocol=UDP localport=44610");
            Console.WriteLine("netsh advfirewall firewall add rule name=\"DPT.Server Public\" dir=in action=allow protocol=TCP localport=44601");
            Console.WriteLine("netsh advfirewall firewall add rule name=\"DPT.Server Admin\" dir=in action=allow protocol=TCP localport=44602");
        }

        private static void RequestShutdown()
        {
            try { StopEvent.Set(); } catch { }
        }

        private static void OnCancelKey(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Log.Warn("APP", "Ctrl+C received, shutting down");
            RequestShutdown();
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Exception ex = e.ExceptionObject as Exception;
                Log.Error("APP", "UNHANDLED: " + (ex == null ? "unknown" : ex.ToString()));
                Log.Shutdown();
            }
            catch { }
            if (e.IsTerminating) _exitCode = 3;
        }

        private static void Shutdown(bool headless)
        {
            if (_app == null) return;
            _app.ShuttingDown = true;
            Log.Info("APP", "shutdown requested");

            try { if (_supervisor != null) _supervisor.Stop(); } catch { }
            try { if (_ui != null) _ui.Stop(); } catch { }

            try { _app.Db.Save(); } catch (Exception ex) { Log.Warn("APP", "state save: " + ex.Message); }
            try { if (_app.WebAdmin != null) _app.WebAdmin.Stop(); } catch (Exception ex) { Log.Warn("APP", "admin stop: " + ex.Message); }
            try { if (_app.WebPublic != null) _app.WebPublic.Stop(); } catch (Exception ex) { Log.Warn("APP", "public stop: " + ex.Message); }
            try { if (_app.Relay != null) _app.Relay.Stop(); } catch (Exception ex) { Log.Warn("APP", "relay stop: " + ex.Message); }
            try { if (_app.Xmpp != null) _app.Xmpp.Stop(); } catch (Exception ex) { Log.Warn("APP", "xmpp stop: " + ex.Message); }
            try { if (_app.Engine != null) _app.Engine.Stop(); } catch (Exception ex) { Log.Warn("APP", "engine stop: " + ex.Message); }

            Log.Info("APP", "goodbye");
            Log.Shutdown();

            if (!headless)
            {
                try
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("Server stopped. Press ENTER to close this window.");
                    Console.ResetColor();
                    Console.ReadLine();
                }
                catch { }
            }
        }
    }
}