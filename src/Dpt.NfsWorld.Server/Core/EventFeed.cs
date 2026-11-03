using System;
using System.Collections.Generic;

namespace Dpt.NfsWorld.Core
{
    public sealed class FeedItem
    {
        public long Id;
        public DateTime Utc;
        public string Kind = "info";
        public string Text = "";
        public string Persona = "";
    }

    /// <summary>
    /// Small in-memory feed feeding the "live events" strip on both boards.
    /// No IPs, no tokens: it is safe to expose the public subset.
    /// </summary>
    public sealed class EventFeed
    {
        private readonly object _sync = new object();
        private readonly List<FeedItem> _items = new List<FeedItem>();
        private readonly int _cap;
        private long _next = 1;

        public EventFeed(int capacity)
        {
            _cap = capacity < 20 ? 20 : capacity;
        }

        public void Add(string kind, string text, string persona)
        {
            lock (_sync)
            {
                FeedItem f = new FeedItem();
                f.Id = _next++;
                f.Utc = DateTime.UtcNow;
                f.Kind = kind ?? "info";
                f.Text = text ?? "";
                f.Persona = persona ?? "";
                _items.Add(f);
                if (_items.Count > _cap) _items.RemoveRange(0, _items.Count - _cap);
            }
        }

        public List<FeedItem> Tail(long sinceId, int limit)
        {
            List<FeedItem> result = new List<FeedItem>();
            lock (_sync)
            {
                for (int i = 0; i < _items.Count; i++)
                    if (_items[i].Id > sinceId) result.Add(_items[i]);
            }
            if (limit > 0 && result.Count > limit) result.RemoveRange(0, result.Count - limit);
            return result;
        }

        public long LastId { get { lock (_sync) return _next - 1; } }
    }
}