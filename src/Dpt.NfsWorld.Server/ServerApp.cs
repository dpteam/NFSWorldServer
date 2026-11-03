using System;
using System.Diagnostics;
using Dpt.NfsWorld.Core;
using Dpt.NfsWorld.Engine;
using Dpt.NfsWorld.Net;
using Dpt.NfsWorld.Web;

namespace Dpt.NfsWorld
{
    /// <summary>Shared application state, one instance per process.</summary>
    public sealed class ServerApp
    {
        public const string Name = "DPT.Server NFS World";
        public const string Version = "0.1.0";
        public const string BuildDate = "2026-11-03";

        public Config Cfg;
        public DataStore Db;
        public ProfileStore Profiles;
        public SessionManager Sessions;
        public EventFeed Events;

        public EngineServer Engine;
        public XmppServer Xmpp;
        public RelayServer Relay;
        public WebServer WebPublic;
        public WebServer WebAdmin;

        public volatile bool ShuttingDown;
        public readonly DateTime StartedUtc = DateTime.UtcNow;

        public int ActiveConnections;
        public long TotalRequests;

        public long WorkingSetBytes
        {
            get
            {
                try { return Process.GetCurrentProcess().WorkingSet64; }
                catch { return 0; }
            }
        }

        public string UptimeText
        {
            get
            {
                TimeSpan t = DateTime.UtcNow - StartedUtc;
                if (t.TotalDays >= 1) return ((int)t.TotalDays) + "d " + t.Hours + "h";
                if (t.TotalHours >= 1) return ((int)t.TotalHours) + "h " + t.Minutes + "m";
                return ((int)t.TotalMinutes) + "m " + t.Seconds + "s";
            }
        }
    }
}