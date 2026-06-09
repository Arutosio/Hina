using System.Collections.Concurrent;

namespace Hina.Host
{
    // In-memory request counters keyed by (ip, app), backing /stats, the periodic summary
    // log, and abuse warnings.
    internal sealed class AccessStats
    {
        readonly ConcurrentDictionary<string, IpBucket> _keys = new(); // key = ip|app
        readonly ConcurrentDictionary<string, long> _paths = new();
        readonly ConcurrentDictionary<string, long> _apps = new();
        readonly ConcurrentDictionary<string, long> _appRejections = new();
        long _total;
        long _rejections;

        public void RecordRequest(string ip, string appName, string path)
        {
            Interlocked.Increment(ref _total);
            _paths.AddOrUpdate(path, 1, (_, v) => v + 1);
            _apps.AddOrUpdate(appName, 1, (_, v) => v + 1);
            var bucket = _keys.GetOrAdd($"{ip}|{appName}", _ => new IpBucket());
            bucket.Hit();
        }

        public void RecordRejection(string ip, string appName)
        {
            Interlocked.Increment(ref _rejections);
            _appRejections.AddOrUpdate(appName, 1, (_, v) => v + 1);
            var bucket = _keys.GetOrAdd($"{ip}|{appName}", _ => new IpBucket());
            Interlocked.Increment(ref bucket.Rejections);
        }

        public bool ShouldLogAbuse(string ip, string appName, int threshold, out long count)
        {
            count = 0;
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
