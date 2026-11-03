using System;
using System.Globalization;
using System.Threading;
using Dpt.NfsWorld.Core;

namespace Dpt.NfsWorld
{
    /// <summary>
    /// Watchdog: keeps the game endpoint, EA-Chat, the UDP relay and both web boards
    /// alive, runs the housekeeping timer (sessions, relay rooms, state flush) and
    /// logs a health line every ten minutes. A dead accept loop never turns into a
    /// silent zombie: restart with exponential backoff.
    /// </summary>
    public sealed class AppSupervisor
    {
        private sealed class Watched
        {
            public string Name;
            public Func<bool> IsRunning;
            public Func<bool> Restart;
            public DateTime BackoffUntil = DateTime.MinValue;
            public int Restarts;
        }

        private readonly ServerApp _app;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly Watched _engine;
        private readonly Watched _xmpp;
        private readonly Watched _relay;
        private readonly Watched _webPublic;
        private readonly Watched _webAdmin;
        private Thread _thread;

        public AppSupervisor(ServerApp app)
        {
            _app = app;
            _engine = new Watched
            {
                Name = "engine",
                IsRunning = delegate { return app.Engine != null && app.Engine.IsRunning; },
                Restart = delegate { return app.Engine != null && app.Engine.Restart(); }
            };
            _xmpp = new Watched
            {
                Name = "xmpp",
                IsRunning = delegate { return app.Xmpp != null && app.Xmpp.IsRunning; },
                Restart = delegate { return app.Xmpp != null && app.Xmpp.Restart(); }
            };
            _relay = new Watched
            {
                Name = "relay",
                IsRunning = delegate { return app.Relay != null && app.Relay.IsRunning; },
                Restart = delegate { return app.Relay != null && app.Relay.Restart(); }
            };
            _webPublic = new Watched
            {
                Name = "web-public",
                IsRunning = delegate { return app.WebPublic != null && app.WebPublic.IsRunning; },
                Restart = delegate { return app.WebPublic != null && app.WebPublic.Restart(); }
            };
            _webAdmin = new Watched
            {
                Name = "web-admin",
                IsRunning = delegate { return app.WebAdmin != null && app.WebAdmin.IsRunning; },
                Restart = delegate { return app.WebAdmin != null && app.WebAdmin.Restart(); }
            };
        }

        public int EngineRestarts { get { return _engine.Restarts; } }
        public int XmppRestarts { get { return _xmpp.Restarts; } }
        public int RelayRestarts { get { return _relay.Restarts; } }
        public int PublicRestarts { get { return _webPublic.Restarts; } }
        public int AdminRestarts { get { return _webAdmin.Restarts; } }

        public void Start()
        {
            if (_app.Engine != null) _app.Engine.Failed += OnFailed;
            if (_app.Xmpp != null) _app.Xmpp.Failed += OnFailed;
            if (_app.Relay != null) _app.Relay.Failed += OnFailed;
            if (_app.WebPublic != null) _app.WebPublic.Failed += OnFailed;
            if (_app.WebAdmin != null) _app.WebAdmin.Failed += OnFailed;

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "supervisor";
            _thread.Start();
            Log.Debug("APP", "supervisor started");
        }

        public void Stop()
        {
            try { _stop.Set(); } catch { }
            try { if (_thread != null && _thread.IsAlive) _thread.Join(2000); } catch { }
            if (_app.Engine != null) _app.Engine.Failed -= OnFailed;
            if (_app.Xmpp != null) _app.Xmpp.Failed -= OnFailed;
            if (_app.Relay != null) _app.Relay.Failed -= OnFailed;
            if (_app.WebPublic != null) _app.WebPublic.Failed -= OnFailed;
            if (_app.WebAdmin != null) _app.WebAdmin.Failed -= OnFailed;
        }

        private void OnFailed(object sender, EventArgs e)
        {
            Log.Error("APP", "supervisor: a listener reported a failure, it will be restarted");
        }

        private void Loop()
        {
            DateTime lastHealth = DateTime.UtcNow;
            DateTime lastHousekeeping = DateTime.UtcNow;

            while (!_stop.WaitOne(2000))
            {
                if (_app.ShuttingDown) return;
                try
                {
                    DateTime now = DateTime.UtcNow;
                    Check(_engine, now);
                    Check(_xmpp, now);
                    Check(_relay, now);
                    Check(_webPublic, now);
                    Check(_webAdmin, now);

                    if ((now - lastHousekeeping).TotalSeconds >= 10)
                    {
                        lastHousekeeping = now;
                        Housekeeping();
                    }

                    if ((now - lastHealth).TotalMinutes >= 10)
                    {
                        lastHealth = now;
                        Log.Info("APP", "health: engine=" + Prefix(_app.Engine) + " | xmpp=" + Prefix(_app.Xmpp) +
                                        " | relay=" + Prefix(_app.Relay) + " | public=" + Prefix(_app.WebPublic) +
                                        " | admin=" + Prefix(_app.WebAdmin) + " | sessions=" + _app.Sessions.Count +
                                        " | mem=" + (_app.WorkingSetBytes / 1048576).ToString(CultureInfo.InvariantCulture) + "MB");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("APP", "supervisor loop: " + ex.Message);
                }
            }
        }

        private void Housekeeping()
        {
            try
            {
                int dead = _app.Sessions.Sweep();
                if (dead > 0) _app.Events.Add("timeout", dead + " idle session(s) expired", "server");
            }
            catch (Exception ex) { Log.Warn("APP", "session sweep: " + ex.Message); }

            try { if (_app.Xmpp != null) _app.Xmpp.Sweep(); }
            catch (Exception ex) { Log.Warn("APP", "xmpp sweep: " + ex.Message); }

            try { if (_app.Relay != null) _app.Relay.Sweep(); }
            catch (Exception ex) { Log.Warn("APP", "relay sweep: " + ex.Message); }

            try { _app.Db.FlushIfDue(); }
            catch (Exception ex) { Log.Warn("APP", "state flush: " + ex.Message); }
        }

        private void Check(Watched w, DateTime now)
        {
            bool running;
            try { running = w.IsRunning(); } catch { running = false; }
            if (running) return;
            if (now < w.BackoffUntil) return;

            w.Restarts++;
            int delay = BackoffSeconds(w.Restarts);
            w.BackoffUntil = now.AddSeconds(delay);
            Log.Warn("APP", "supervisor: restarting " + w.Name + " (attempt " + w.Restarts +
                            ", next retry not before " + delay + "s)");
            bool ok = false;
            try { ok = w.Restart(); }
            catch (Exception ex) { Log.Error("APP", "supervisor: " + w.Name + " restart threw: " + ex.Message); }
            Log.Warn("APP", "supervisor: " + w.Name + " restart " + (ok ? "succeeded" : "failed"));
        }

        private static int BackoffSeconds(int attempt)
        {
            int v = 1 << Math.Min(attempt, 5);
            return Math.Min(30, v);
        }

        private static string Prefix(object server)
        {
            if (server == null) return "n/a";
            try
            {
                Dpt.NfsWorld.Engine.EngineServer engine = server as Dpt.NfsWorld.Engine.EngineServer;
                if (engine != null) return engine.StatusLine();
                Dpt.NfsWorld.Net.XmppServer xmpp = server as Dpt.NfsWorld.Net.XmppServer;
                if (xmpp != null) return xmpp.StatusLine();
                Dpt.NfsWorld.Net.RelayServer relay = server as Dpt.NfsWorld.Net.RelayServer;
                if (relay != null) return relay.StatusLine();
                Dpt.NfsWorld.Web.WebServer web = server as Dpt.NfsWorld.Web.WebServer;
                if (web != null) return web.StatusLine();
            }
            catch { }
            return "?";
        }
    }
}