namespace Hina.Host
{
    internal static class Routing
    {
        public static string ExtractApp(string path, HostOptions opt)
        {
            if (opt.Apps.Count == 0) return "default";
            var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0) return "unknown";
            return opt.Apps.ContainsKey(segs[0]) ? segs[0] : "unknown";
        }
    }
}
