using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Dpt.NfsWorld.Core
{
    public static class Util
    {
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long UnixNow { get { return (long)(DateTime.UtcNow - UnixEpoch).TotalSeconds; } }

        public static string Iso(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        public static string LocalTime(DateTime utc)
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public static string Ip(IPAddress address)
        {
            if (address == null) return "0.0.0.0";
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
                return address.MapToIPv4().ToString();
            return address.ToString();
        }

        public static string Ip(EndPoint ep)
        {
            IPEndPoint ipep = ep as IPEndPoint;
            return ipep == null ? "0.0.0.0" : Ip(ipep.Address);
        }

        /// <summary>Private, loopback, link-local and CGNAT space.</summary>
        public static bool IsPrivateIpv4(string ip)
        {
            IPAddress a;
            if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out a)) return false;
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (IPAddress.IPv6Loopback.Equals(a)) return true;
                if (a.IsIPv4MappedToIPv6) return IsPrivateIpv4(a.MapToIPv4().ToString());
                return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal;
            }
            byte[] b = a.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 127) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // CGNAT
            if (b[0] == 0) return true;
            return false;
        }

        public static string[] LocalIPv4()
        {
            List<string> list = new List<string>();
            try
            {
                NetworkInterface[] ifaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < ifaces.Length; i++)
                {
                    NetworkInterface ni = ifaces[i];
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); } catch { continue; }
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = ua.Address.ToString();
                        if (s.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                        if (!list.Contains(s)) list.Add(s);
                    }
                }
            }
            catch { }
            if (list.Count == 0) list.Add("127.0.0.1");
            return list.ToArray();
        }

        /// <summary>Default gateway for the primary IPv4 interface (NAT hairpin detection).</summary>
        public static string DefaultGateway()
        {
            try
            {
                NetworkInterface[] ifaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < ifaces.Length; i++)
                {
                    if (ifaces[i].OperationalStatus != OperationalStatus.Up) continue;
                    if (ifaces[i].NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (GatewayIPAddressInformation g in ifaces[i].GetIPProperties().GatewayAddresses)
                    {
                        if (g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork)
                            return g.Address.ToString();
                    }
                }
            }
            catch { }
            return "";
        }

        public static string Hex(byte[] data, int maxBytes)
        {
            if (data == null) return "";
            int n = maxBytes > 0 && data.Length > maxBytes ? maxBytes : data.Length;
            StringBuilder sb = new StringBuilder(n * 3);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            if (n < data.Length) sb.Append(" ...");
            return sb.ToString();
        }

        public static string HexCompact(byte[] data)
        {
            if (data == null) return "";
            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++) sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static string Printable(byte[] data, int maxBytes)
        {
            if (data == null) return "";
            int n = maxBytes > 0 && data.Length > maxBytes ? maxBytes : data.Length;
            StringBuilder sb = new StringBuilder(n);
            for (int i = 0; i < n; i++)
            {
                byte b = data[i];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            return sb.ToString();
        }

        public static string ShortToken(int bytes)
        {
            if (bytes <= 0) bytes = 16;
            byte[] buf = new byte[bytes];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) rng.GetBytes(buf);
            return HexCompact(buf);
        }

        /// <summary>URL-safe random id (no padding).</summary>
        public static string UrlToken(int bytes)
        {
            byte[] buf = new byte[bytes <= 0 ? 18 : bytes];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) rng.GetBytes(buf);
            return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Length independent comparison (avoids leaking a token by timing).</summary>
        public static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] x = Encoding.UTF8.GetBytes(a);
            byte[] y = Encoding.UTF8.GetBytes(b);
            int diff = x.Length ^ y.Length;
            int n = Math.Min(x.Length, y.Length);
            for (int i = 0; i < n; i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }

        public static string Sha256Hex(string text)
        {
            using (SHA256Managed sha = new SHA256Managed())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                return HexCompact(hash);
            }
        }

        public static string Base64Url(byte[] data)
        {
            if (data == null) return "";
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Joins a URL base path and a relative path, avoiding double slashes.</summary>
        public static string UrlJoin(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? "";
            if (string.IsNullOrEmpty(b)) return a;
            if (a.EndsWith("/", StringComparison.Ordinal) && b.StartsWith("/", StringComparison.Ordinal))
                return a + b.Substring(1);
            if (!a.EndsWith("/", StringComparison.Ordinal) && !b.StartsWith("/", StringComparison.Ordinal))
                return a + "/" + b;
            return a + b;
        }

        public static string TrimSlashes(string s)
        {
            if (s == null) return "";
            return s.Trim().Trim('/');
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "x";
            StringBuilder sb = new StringBuilder(name.Length);
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = true;
                for (int j = 0; j < bad.Length; j++) if (bad[j] == c) { ok = false; break; }
                sb.Append(ok ? c : '_');
            }
            string res = sb.ToString();
            return res.Length == 0 ? "x" : res;
        }

        public static int ParseInt(string text, int def)
        {
            int v;
            return int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public static long ParseLong(string text, long def)
        {
            long v;
            return long.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public static bool ParseBool(string text, bool def)
        {
            if (text == null) return def;
            string s = text.Trim().ToLowerInvariant();
            if (s.Length == 0) return def;
            if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off") return false;
            return def;
        }

        public static int[] ParseIntList(string text)
        {
            if (string.IsNullOrEmpty(text)) return new int[0];
            string[] parts = text.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            List<int> list = new List<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                int v;
                if (int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) &&
                    v > 0 && v <= 65535 && !list.Contains(v))
                    list.Add(v);
            }
            return list.ToArray();
        }

        /// <summary>Extracts the first text node value of an XML tag, namespace agnostic.</summary>
        public static string XmlValue(string xml, string tag)
        {
            if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(tag)) return null;
            int i = xml.IndexOf("<" + tag, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int gt = xml.IndexOf('>', i);
            if (gt < 0) return null;
            // self closing?
            if (gt > 0 && xml[gt - 1] == '/') return "";
            int close = xml.IndexOf("</", gt, StringComparison.Ordinal);
            if (close < 0) return null;
            int end = xml.IndexOf('>', close);
            if (end < 0) return null;
            return xml.Substring(gt + 1, close - gt - 1).Trim();
        }

        public static string EscapeXml(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                       .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        public static string Truncate(string s, int max)
        {
            if (s == null) return "";
            if (max <= 0 || s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }

        /// <summary>Human readable byte count, e.g. "12.4 KB".</summary>
        public static string Bytes(long value)
        {
            if (value < 1024) return value.ToString(CultureInfo.InvariantCulture) + " B";
            if (value < 1024 * 1024) return (value / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            if (value < 1024L * 1024 * 1024) return (value / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (value / (1024.0 * 1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        public static string Ago(DateTime utc)
        {
            TimeSpan d = DateTime.UtcNow - utc.ToUniversalTime();
            if (d.TotalSeconds < 0) return "just now";
            if (d.TotalSeconds < 60) return ((int)d.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s ago";
            if (d.TotalMinutes < 60) return ((int)d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m ago";
            if (d.TotalHours < 24) return ((int)d.TotalHours).ToString(CultureInfo.InvariantCulture) + "h ago";
            return ((int)d.TotalDays).ToString(CultureInfo.InvariantCulture) + "d ago";
        }
    }
}