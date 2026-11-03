using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Dpt.NfsWorld.Core
{
    public enum LogLevel
    {
        Error = 0,
        Warn = 1,
        Info = 2,
        Debug = 3,
        Verbose = 4
    }

    public sealed class LogEntry
    {
        public long Seq;
        public DateTime Utc;
        public LogLevel Level;
        public string Category;
        public string Message;

        public string TimeText()
        {
            return Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Trace based logging with a rolling file sink.
    ///
    /// Rules that keep the old "listener bug" away:
    ///  * Trace.Listeners is cleared exactly once during Configure().
    ///  * The single listener owns its writer, never throws and checks a shutdown flag.
    ///  * Nothing is echoed to the console from inside Trace (the console has its own presenter).
    /// </summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        private static readonly List<LogEntry> Ring = new List<LogEntry>();
        private static LogLevel _minLevel = LogLevel.Info;
        private static bool _protocolTrace = true;
        private static int _ringCap = 2000;
        private static long _seq;
        private static bool _configured;
        private static bool _shutdown;
        private static LogFileListener _listener;

        public static LogLevel MinLevel { get { return _minLevel; } }
        public static long LastSeq { get { lock (Sync) { return _seq; } } }
        public static bool ProtocolTrace { get { return _protocolTrace; } }

        public static void Configure(Config cfg)
        {
            if (cfg == null) return;
            lock (Sync)
            {
                _minLevel = cfg.LogLevel;
                _protocolTrace = cfg.ProtocolTrace;
                _ringCap = cfg.LogRingSize;
                _shutdown = false;
            }

            try { Directory.CreateDirectory(cfg.LogDir); }
            catch (Exception ex) { Console.Error.WriteLine("Cannot create log directory: " + ex.Message); }

            // Clear whatever listeners were installed before us (tests, hosting, old code).
            try { Trace.Listeners.Clear(); }
            catch { }

            _listener = new LogFileListener(cfg.LogDir, cfg.LogMaxFileMB, cfg.LogRetentionDays);
            try
            {
                Trace.Listeners.Add(_listener);
                Trace.AutoFlush = false; // the listener flushes every line itself
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Cannot attach log listener: " + ex.Message);
            }

            _configured = true;
            Info("LOG", "Logging started. dir=" + cfg.LogDir + " level=" + cfg.LogLevel.ToString() +
                        " protocolTrace=" + (cfg.ProtocolTrace ? "on" : "off"));
        }

        public static void Shutdown()
        {
            LogEntry e;
            lock (Sync)
            {
                if (_shutdown) return;
                _shutdown = true;
                e = new LogEntry { Seq = ++_seq, Utc = DateTime.UtcNow, Level = LogLevel.Info, Category = "LOG", Message = "Logging stopped." };
                AddToRing(e);
            }
            try { Trace.WriteLine(Format(e)); } catch { }
            try { Trace.Flush(); } catch { }
            try { if (_listener != null) _listener.Dispose(); } catch { }
            try { Trace.Listeners.Clear(); } catch { }
        }

        public static void Error(string category, string message) { Write(LogLevel.Error, category, message); }
        public static void Warn(string category, string message) { Write(LogLevel.Warn, category, message); }
        public static void Info(string category, string message) { Write(LogLevel.Info, category, message); }
        public static void Debug(string category, string message) { Write(LogLevel.Debug, category, message); }
        public static void Verbose(string category, string message) { Write(LogLevel.Verbose, category, message); }

        public static void Error(string category, string format, params object[] args) { Write(LogLevel.Error, category, SafeFormat(format, args)); }
        public static void Warn(string category, string format, params object[] args) { Write(LogLevel.Warn, category, SafeFormat(format, args)); }
        public static void Info(string category, string format, params object[] args) { Write(LogLevel.Info, category, SafeFormat(format, args)); }
        public static void Debug(string category, string format, params object[] args) { Write(LogLevel.Debug, category, SafeFormat(format, args)); }

        /// <summary>
        /// Protocol level log. Gated by logging.protocolTrace only - it deliberately
        /// ignores logging.level, because "trace on, level=info" is the documented
        /// setting and used to produce an empty log exactly when it mattered.
        /// </summary>
        public static void Proto(string category, string format, params object[] args)
        {
            if (!_protocolTrace) return;
            WriteForced(LogLevel.Debug, category, SafeFormat(format, args));
        }

        /// <summary>Writes regardless of the configured level (protocol trace, fatal).</summary>
        private static void WriteForced(LogLevel level, string category, string message)
        {
            LogEntry e = NewEntry(level, category, message);
            lock (Sync)
            {
                if (_shutdown) return;
                AddToRing(e);
            }
            Emit(e);
        }

        public static void Write(LogLevel level, string category, string message)
        {
            if (level > _minLevel) return;
            LogEntry e = NewEntry(level, category, message);
            lock (Sync)
            {
                if (_shutdown) return;
                AddToRing(e);
            }
            Emit(e);
        }

        private static LogEntry NewEntry(LogLevel level, string category, string message)
        {
            return new LogEntry
            {
                Utc = DateTime.UtcNow,
                Level = level,
                Category = category == null ? "APP" : category,
                Message = message == null ? "" : message,
                Seq = Interlocked.Increment(ref _seq)
            };
        }

        /// <summary>Hands one entry to Trace (the file listener formats it). No console echo.</summary>
        private static void Emit(LogEntry e)
        {
            if (!_configured) return; // pre-configure calls still land in the ring
            try { Trace.WriteLine(Format(e)); }
            catch { }
        }

        public static List<LogEntry> Tail(long sinceSeq, int limit, LogLevel? minLevel, string categoryFilter)
        {
            List<LogEntry> result = new List<LogEntry>();
            lock (Sync)
            {
                for (int i = 0; i < Ring.Count; i++)
                {
                    LogEntry e = Ring[i];
                    if (e.Seq <= sinceSeq) continue;
                    if (minLevel.HasValue && e.Level > minLevel.Value) continue;
                    if (!string.IsNullOrEmpty(categoryFilter) &&
                        e.Category.IndexOf(categoryFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    result.Add(e);
                }
            }
            if (limit > 0 && result.Count > limit)
                result.RemoveRange(0, result.Count - limit);
            return result;
        }

        public static string TailText(int lines, string category)
        {
            List<LogEntry> list = Tail(0, lines <= 0 ? 300 : lines, null, category);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++) sb.AppendLine(Format(list[i]));
            return sb.ToString();
        }

        public static string LevelLetter(LogLevel l)
        {
            switch (l)
            {
                case LogLevel.Error: return "E";
                case LogLevel.Warn: return "W";
                case LogLevel.Info: return "I";
                case LogLevel.Debug: return "D";
                default: return "V";
            }
        }

        public static string Format(LogEntry e)
        {
            string cat = e.Category ?? "APP";
            if (cat.Length < 6) cat = cat + new string(' ', 6 - cat.Length);
            return e.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                   " [" + LevelLetter(e.Level) + "] " + cat + " " + SingleLine(e.Message);
        }

        private static string SingleLine(string s)
        {
            if (s == null) return "";
            if (s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return s;
            return s.Replace("\r\n", " | ").Replace("\n", " | ").Replace("\r", " | ");
        }

        private static void AddToRing(LogEntry e)
        {
            Ring.Add(e);
            if (Ring.Count > _ringCap) Ring.RemoveRange(0, Ring.Count - _ringCap);
        }

        private static string SafeFormat(string format, object[] args)
        {
            if (args == null || args.Length == 0) return format;
            try { return string.Format(CultureInfo.InvariantCulture, format, args); }
            catch { return format; }
        }

        /// <summary>
        /// The one and only file listener. Writes formatted lines into
        /// logs/nfsw-YYYYMMDD.log with day and size rollover, thread safe,
        /// never throws.
        /// </summary>
        private sealed class LogFileListener : TraceListener
        {
            private readonly object _l = new object();
            private readonly string _dir;
            private readonly long _maxBytes;
            private readonly int _retentionDays;
            private StreamWriter _writer;
            private DateTime _day = DateTime.MinValue;
            private int _index;
            private long _bytes;
            private bool _dead;
            private bool _disposed;

            public LogFileListener(string dir, int maxMb, int retentionDays)
            {
                _dir = dir;
                _maxBytes = (long)Math.Max(1, maxMb) * 1024L * 1024L;
                _retentionDays = retentionDays;
                try { Directory.CreateDirectory(_dir); } catch { }
                CleanupOldFiles();
            }

            public override void Write(string message) { WriteLine(message); }

            public override void WriteLine(string message)
            {
                if (_disposed || _dead || message == null) return;
                lock (_l)
                {
                    if (_disposed || _dead) return;
                    try
                    {
                        EnsureWriter();
                        _writer.WriteLine(message);
                        _bytes += message.Length + 2;
                    }
                    catch
                    {
                        try { CloseWriter(); } catch { }
                        _dead = !Directory.Exists(_dir);
                    }
                }
            }

            private void EnsureWriter()
            {
                DateTime today = DateTime.Now.Date;
                if (_writer != null && _day == today && _bytes < _maxBytes) return;

                CloseWriter();
                if (_day != today) { _day = today; _index = 0; }
                else _index++;

                string name = string.Format(CultureInfo.InvariantCulture, "nfsw-{0:yyyyMMdd}{1}.log",
                    _day, _index == 0 ? "" : "." + _index.ToString("000", CultureInfo.InvariantCulture));
                string path = Path.Combine(_dir, name);
                FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(fs, new UTF8Encoding(false));
                _writer.AutoFlush = true;
                _bytes = fs.Length;
            }

            private void CloseWriter()
            {
                if (_writer == null) return;
                try { _writer.Flush(); } catch { }
                try { _writer.Dispose(); } catch { }
                _writer = null;
            }

            private void CleanupOldFiles()
            {
                if (_retentionDays <= 0) return;
                try
                {
                    DateTime cutoff = DateTime.Now.AddDays(-_retentionDays);
                    string[] files = Directory.GetFiles(_dir, "nfsw-*.log*");
                    for (int i = 0; i < files.Length; i++)
                    {
                        try
                        {
                            if (File.GetLastWriteTime(files[i]) < cutoff) File.Delete(files[i]);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            protected override void Dispose(bool disposing)
            {
                lock (_l)
                {
                    _disposed = true;
                    CloseWriter();
                }
                base.Dispose(disposing);
            }
        }
    }
}