using System.Text.Json;

namespace Hina.Host
{
    // Server configuration merged from hina.host.json, the legacy Patcher:Root setting,
    // and command-line flags (flags win).
    internal sealed class HostOptions
    {
        public string Root { get; set; } = "patch";
        public string? Urls { get; set; }
        public int RequestsPerMinutePerIp { get; set; } = 600;
        public int AbuseThresholdPerMinute { get; set; } = 300;
        public int SummaryIntervalSeconds { get; set; } = 60;
        public bool StatsEnabled { get; set; } = true;
        public List<string> Cors { get; set; } = new();
        public Dictionary<string, string> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static HostOptions Load(string[] args, IConfiguration config)
        {
            var opt = new HostOptions();

            string? configPath = GetArg(args, "--config");
            string? jsonPath = configPath is not null && File.Exists(configPath)
                ? configPath
                : (File.Exists("hina.host.json") ? "hina.host.json" : null);

            if (jsonPath is not null)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var r = doc.RootElement;
                if (r.TryGetProperty("root", out var v) && v.ValueKind == JsonValueKind.String) opt.Root = v.GetString()!;
                if (r.TryGetProperty("urls", out v) && v.ValueKind == JsonValueKind.String) opt.Urls = v.GetString();
                if (r.TryGetProperty("requestsPerMinutePerIp", out v) && v.TryGetInt32(out int n)) opt.RequestsPerMinutePerIp = n;
                if (r.TryGetProperty("abuseThresholdPerMinute", out v) && v.TryGetInt32(out n)) opt.AbuseThresholdPerMinute = n;
                if (r.TryGetProperty("summaryIntervalSeconds", out v) && v.TryGetInt32(out n)) opt.SummaryIntervalSeconds = n;
                if (r.TryGetProperty("statsEnabled", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) opt.StatsEnabled = v.GetBoolean();
                if (r.TryGetProperty("cors", out v) && v.ValueKind == JsonValueKind.Array)
                    opt.Cors = v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                if (r.TryGetProperty("apps", out v) && v.ValueKind == JsonValueKind.Object)
                    foreach (var prop in v.EnumerateObject())
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            opt.Apps[prop.Name] = prop.Value.GetString()!;
            }

            string? legacy = config["Patcher:Root"];
            if (!string.IsNullOrWhiteSpace(legacy)) opt.Root = legacy;

            if (GetArg(args, "--root") is { } root) opt.Root = root;
            if (GetArg(args, "--urls") is { } urls) opt.Urls = urls;
            if (GetArg(args, "--port") is { } port && int.TryParse(port, out int p)) opt.Urls = $"http://0.0.0.0:{p}";
            if (GetArg(args, "--rate-limit") is { } rl && int.TryParse(rl, out int rln)) opt.RequestsPerMinutePerIp = rln == 0 ? int.MaxValue : rln;
            if (GetArg(args, "--abuse-threshold") is { } ab && int.TryParse(ab, out int abn)) opt.AbuseThresholdPerMinute = abn;
            if (GetArg(args, "--cors") is { } cors) opt.Cors = cors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (args.Contains("--no-stats", StringComparer.OrdinalIgnoreCase)) opt.StatsEnabled = false;

            return opt;
        }

        static string? GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
