using System.Collections.Concurrent;

namespace Hina.Host
{
    // In-memory request counters keyed by (ip, app), backing /stats, the periodic summary
    // log, and abuse warnings.
    internal sealed class AccessStats
    {
        // Caps on the tracking tables: a scanned/abused public host would otherwise grow an
        // entry per unique probe path and per (ip,app) for the process lifetime — a slow
        // memory exhaustion. Past the cap, NEW keys are no longer tracked individually
        // (totals still count and rate limiting is unaffected); existing keys keep updating.
        // The check-then-add is not atomic, so the cap can overshoot by a few entries under
        // concurrency — it bounds growth, it is not an exact limit.
        internal const int MaxTrackedPaths = 10_000;
        internal const int MaxTrackedKeys = 50_000;

        readonly ConcurrentDictionary<string, IpBucket> _keys = new(); // key = ip|app
        readonly ConcurrentDictionary<string, long> _paths = new();
        readonly ConcurrentDictionary<string, long> _apps = new();
        readonly ConcurrentDictionary<string, long> _appRejections = new();
        long _total;
        long _rejections;

        internal int TrackedPathCount => _paths.Count;
        internal int TrackedKeyCount => _keys.Count;

        public void RecordRequest(string ip, string appName, string path)
        {
            Interlocked.Increment(ref _total);
            if (_paths.ContainsKey(path) || _paths.Count < MaxTrackedPaths)
            {
                _paths.AddOrUpdate(path, 1, (_, v) => v + 1);
            }
            // App names come from Routing.ExtractApp (configured apps, "default", "unknown"),
            // so _apps and _appRejections are naturally bounded.
            _apps.AddOrUpdate(appName, 1, (_, v) => v + 1);
            string key = $"{ip}|{appName}";
            if (_keys.TryGetValue(key, out var bucket))
            {
                bucket.Hit();
            }
            else if (_keys.Count < MaxTrackedKeys)
            {
                _keys.GetOrAdd(key, _ => new IpBucket()).Hit();
            }
        }

        public void RecordRejection(string ip, string appName)
        {
            Interlocked.Increment(ref _rejections);
            _appRejections.AddOrUpdate(appName, 1, (_, v) => v + 1);
            string key = $"{ip}|{appName}";
            if (_keys.TryGetValue(key, out var bucket))
            {
                Interlocked.Increment(ref bucket.Rejections);
            }
            else if (_keys.Count < MaxTrackedKeys)
            {
                bucket = _keys.GetOrAdd(key, _ => new IpBucket());
                Interlocked.Increment(ref bucket.Rejections);
            }
        }

        // threshold == 0 means "abuse detection disabled": always returns false regardless of
        // how many requests the IP has made. This mirrors the requestsPerMinutePerIp == 0
        // convention (no rate limit) so that a single value silences the feature cleanly.
        public bool ShouldLogAbuse(string ip, string appName, int threshold, out long count)
        {
            count = 0;
            if (threshold == 0) return false;
            if (!_keys.TryGetValue($"{ip}|{appName}", out var b)) return false;
            count = b.CountLastMinute();
            if (count < threshold) return false;
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref b.LastAbuseLogTick);
            if (now - last < 60_000) return false;
            return Interlocked.CompareExchange(ref b.LastAbuseLogTick, now, last) == last;
        }

        public StatsSnapshot Snapshot()
        {
            var topKey = _keys.Select(kv => (kv.Key, kv.Value.CountLastMinute())).OrderByDescending(t => t.Item2).FirstOrDefault();
            var topPath = _paths.OrderByDescending(kv => kv.Value).FirstOrDefault();
            var topApp = _apps.OrderByDescending(kv => kv.Value).FirstOrDefault();
            string topIp = topKey.Key is null ? "-" : topKey.Key.Split('|')[0];
            return new StatsSnapshot(
                Interlocked.Read(ref _total),
                Interlocked.Read(ref _rejections),
                topIp,
                topApp.Key ?? "-",
                topPath.Key ?? "-",
                _keys.OrderByDescending(kv => kv.Value.CountLastMinute()).Take(10)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.CountLastMinute()),
                _apps.OrderByDescending(kv => kv.Value).Take(20).ToDictionary(kv => kv.Key, kv => kv.Value),
                _paths.OrderByDescending(kv => kv.Value).Take(10).ToDictionary(kv => kv.Key, kv => kv.Value),
                _appRejections.ToDictionary(kv => kv.Key, kv => kv.Value));
        }

        sealed class IpBucket
        {
            readonly ConcurrentQueue<long> _hits = new();
            public long Rejections;
            public long LastAbuseLogTick;

            public void Hit()
            {
                long now = Environment.TickCount64;
                _hits.Enqueue(now);
                Prune(now);
            }

            public long CountLastMinute()
            {
                Prune(Environment.TickCount64);
                return _hits.Count;
            }

            void Prune(long now)
            {
                long cutoff = now - 60_000;
                while (_hits.TryPeek(out long t) && t < cutoff) _hits.TryDequeue(out _);
            }
        }
    }

    internal record StatsSnapshot(long TotalRequests, long Rejections, string TopIp, string TopApp, string TopPath,
        Dictionary<string, long> TopIpApps, Dictionary<string, long> Apps, Dictionary<string, long> TopPaths,
        Dictionary<string, long> AppRejections);
}
