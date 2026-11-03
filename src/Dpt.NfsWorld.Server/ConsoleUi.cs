using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Dpt.NfsWorld.Core;
using Dpt.NfsWorld.Engine;

namespace Dpt.NfsWorld
{
    /// <summary>Small interactive console. Status is printed on demand so typing is never interrupted.</summary>
    public sealed class ConsoleUi
    {
        private readonly ServerApp _app;
        private readonly Action _shutdown;
        private Thread _thread;
        private volatile bool _running;

        public ConsoleUi(ServerApp app, Action shutdown)
        {
            _app = app;
            _shutdown = shutdown;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "console";
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
        }

        private void Loop()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  type 'help' for commands, 'status' for a snapshot");
            Console.ResetColor();

            while (_running)
            {
                string line;
                try { line = Console.ReadLine(); }
                catch { return; }
                if (line == null) return;
                line = line.Trim();
                if (line.Length == 0) continue;
                try { Execute(line); }
                catch (Exception ex) { Write("error: " + ex.Message, ConsoleColor.Red); }
            }
        }

        private void Execute(string line)
        {
            string[] parts = line.Split(new char[] { ' ' }, 2);
            string cmd = parts[0].ToLowerInvariant();
            string rest = parts.Length > 1 ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "help": case "?":
                    Help();
                    return;
                case "status":
                    Status();
                    return;
                case "players": case "who":
                    Players();
                    return;
                case "kick":
                    Kick(rest);
                    return;
                case "say": case "announce":
                    Say(rest);
                    return;
                case "reload":
                    _app.Profiles.Reload();
                    EngineXml.ClearCache();
                    Write("profiles reloaded: " + string.Join(", ", _app.Profiles.Describe()), ConsoleColor.Green);
                    return;
                case "save":
                    _app.Db.Save();
                    Write("state saved", ConsoleColor.Green);
                    return;
                case "links": case "urls":
                    Links();
                    return;
                case "clear": case "cls":
                    try { Console.Clear(); } catch { }
                    return;
                case "quit": case "exit": case "stop":
                    _shutdown();
                    return;
                default:
                    Write("unknown command '" + cmd + "', try 'help'", ConsoleColor.Yellow);
                    return;
            }
        }

        private void Help()
        {
            Console.WriteLine();
            Console.WriteLine("  status            services, counts, traffic");
            Console.WriteLine("  players           online personas");
            Console.WriteLine("  kick <id> [why]   kick a persona (session + EA-Chat)");
            Console.WriteLine("  say <text>        announcement to every EA-Chat client");
            Console.WriteLine("  reload            re-read data/profiles and templates");
            Console.WriteLine("  save              flush accounts/personas to data/state/db.json");
            Console.WriteLine("  links             show board and launcher URLs");
            Console.WriteLine("  quit              stop the server");
            Console.WriteLine();
        }

        private void Status()
        {
            Console.WriteLine();
            Console.WriteLine("  " + ServerApp.Name + " v" + ServerApp.Version + "  uptime " + _app.UptimeText);
            Console.WriteLine("  engine  " + State(_app.Engine != null && _app.Engine.IsRunning, _app.Engine == null ? 0 : _app.Engine.Port));
            Console.WriteLine("  xmpp    " + State(_app.Xmpp != null && _app.Xmpp.IsRunning, _app.Xmpp == null ? 0 : _app.Xmpp.Port) +
                              "  clients " + (_app.Xmpp == null ? 0 : _app.Xmpp.Clients));
            Console.WriteLine("  relay   " + State(_app.Relay != null && _app.Relay.IsRunning, _app.Relay == null ? 0 : _app.Relay.Port) +
                              "  rooms " + (_app.Relay == null ? 0 : _app.Relay.RoomCount) +
                              "  " + (_app.Relay == null ? "" : Util.Bytes(_app.Relay.BytesIn) + " in / " + Util.Bytes(_app.Relay.BytesOut) + " out"));
            Console.WriteLine("  public  " + State(_app.WebPublic != null && _app.WebPublic.IsRunning, _app.WebPublic == null ? 0 : _app.WebPublic.Port));
            Console.WriteLine("  admin   " + State(_app.WebAdmin != null && _app.WebAdmin.IsRunning, _app.WebAdmin == null ? 0 : _app.WebAdmin.Port));
            Console.WriteLine("  online " + _app.Sessions.Online().Count + " / sessions " + _app.Sessions.Count +
                              " | accounts " + _app.Db.AccountCount + " | personas " + _app.Db.PersonaCount +
                              " | cars " + _app.Db.CarCount);
            Console.WriteLine("  requests " + _app.TotalRequests + " | memory " + Util.Bytes(_app.WorkingSetBytes));
            Console.WriteLine();
        }

        private static string State(bool running, int port)
        {
            return (running ? "up   " : "DOWN ") + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        private void Players()
        {
            List<PlayerSession> online = _app.Sessions.Online();
            Console.WriteLine();
            if (online.Count == 0) { Console.WriteLine("  nobody is online"); Console.WriteLine(); return; }
            Console.WriteLine("  id        name              lvl  car                 xmpp relay  ip");
            for (int i = 0; i < online.Count; i++)
            {
                PlayerSession s = online[i];
                Persona p = _app.Db.FindPersona(s.PersonaId);
                Console.WriteLine("  " + s.PersonaId.ToString(CultureInfo.InvariantCulture).PadRight(9) +
                                  (s.PersonaName ?? "").PadRight(17) +
                                  (p == null ? "0" : p.Level.ToString(CultureInfo.InvariantCulture)).PadRight(4) +
                                  (p == null || p.CurrentCar == null ? "" : p.CurrentCar.Name).PadRight(19) +
                                  (s.Xmpp ? "yes " : "no  ") + " " + (s.Relay ? "yes " : "no  ") + " " + s.Ip);
            }
            Console.WriteLine();
        }

        private void Kick(string rest)
        {
            if (rest.Length == 0) { Write("usage: kick <personaId> [reason]", ConsoleColor.Yellow); return; }
            string[] parts = rest.Split(new char[] { ' ' }, 2);
            long id = Util.ParseLong(parts[0], 0);
            string reason = parts.Length > 1 ? parts[1] : "console kick";
            PlayerSession s = _app.Sessions.FindByPersona(id);
            if (s == null) { Write("persona " + id + " is not online", ConsoleColor.Yellow); return; }
            _app.Sessions.Kick(s, reason);
            if (_app.Xmpp != null) _app.Xmpp.Kick(id, reason);
            _app.Events.Add("kick", "kicked from the server", s.PersonaName);
            Write("kicked " + s.PersonaName + " (" + reason + ")", ConsoleColor.Green);
        }

        private void Say(string text)
        {
            if (text.Length == 0) { Write("usage: say <text>", ConsoleColor.Yellow); return; }
            int sent = _app.Xmpp == null ? 0 : _app.Xmpp.Broadcast(text);
            _app.Events.Add("announce", Util.Truncate(text, 160), "admin");
            Write("announcement sent to " + sent + " client(s)", ConsoleColor.Green);
        }

        private void Links()
        {
            Console.WriteLine();
            Console.WriteLine("  public board : " + (_app.WebPublic == null ? "-" : _app.WebPublic.BaseUrl));
            Console.WriteLine("  admin board  : " + (_app.WebAdmin == null ? "-" : _app.WebAdmin.BaseUrl));
            Console.WriteLine("  admin token  : see config.ini web.adminToken");
            Console.WriteLine("  game endpoint: http://<host>:" + (_app.Engine == null ? 0 : _app.Engine.Port) + "/Engine.svc");
            Console.WriteLine("  launcher     : bin\\NFSWorldLauncher.exe");
            Console.WriteLine("  public url   : " + _app.Cfg.PublicUrl);
            Console.WriteLine();
        }

        private static void Write(string text, ConsoleColor color)
        {
            try
            {
                Console.ForegroundColor = color;
                Console.WriteLine("  " + text);
                Console.ResetColor();
            }
            catch { }
        }
    }
}