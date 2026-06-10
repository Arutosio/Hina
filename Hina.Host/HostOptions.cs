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
        // Literal IPs of reverse proxies whose X-Forwarded-For is trusted. Without this,
        // every client behind a remote proxy/LB shares the proxy's IP — i.e. one
        // rate-limit bucket for everyone (local loopback proxies are trusted by default).
        public List<string> TrustedProxies { get; set; } = new();
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
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                }
                catch (JsonException ex)
                {
                    // A corrupt config on a non-interactive start (service, container) used to
                    // surface as an unhandled JsonReaderException stack trace.
                    throw new InvalidDataException(
                        $"Host config '{Path.GetFullPath(jsonPath)}' is not valid JSON: {ex.Message} " +
                        "Fix the file, or delete it and re-run with --setup to regenerate it.");
                }
                using (doc)
                {
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object)
                    {
                        // TryGetProperty on a non-object root throws a raw
                        // InvalidOperationException; fail with the same actionable shape
                        // as the not-valid-JSON case instead.
                        throw new InvalidDataException(
                            $"Host config '{Path.GetFullPath(jsonPath)}' must contain a JSON object at the top level (found {r.ValueKind}). " +
                            "Fix the file, or delete it and re-run with --setup to regenerate it.");
                    }
                    if (r.TryGetProperty("root", out var v) && v.ValueKind == JsonValueKind.String) opt.Root = v.GetString()!;
                    if (r.TryGetProperty("urls", out v) && v.ValueKind == JsonValueKind.String) opt.Urls = v.GetString();
                    if (r.TryGetProperty("requestsPerMinutePerIp", out v) && v.TryGetInt32(out int n))
                    {
                        if (n < 0) throw new InvalidDataException($"Host config '{jsonPath}': requestsPerMinutePerIp must be >= 0 (got {n}; 0 disables the limit).");
                        // 0 = disabled, same contract as the --rate-limit flag. Without the remap a
                        // zero PermitLimit makes the rate limiter throw on every request (HTTP 500).
                        opt.RequestsPerMinutePerIp = n == 0 ? int.MaxValue : n;
                    }
                    if (r.TryGetProperty("abuseThresholdPerMinute", out v) && v.TryGetInt32(out n))
                    {
                        if (n < 0) throw new InvalidDataException($"Host config '{jsonPath}': abuseThresholdPerMinute must be >= 0 (got {n}).");
                        opt.AbuseThresholdPerMinute = n;
                    }
                    if (r.TryGetProperty("summaryIntervalSeconds", out v) && v.TryGetInt32(out n))
                    {
                        // 0 would turn the summary loop into a busy spin at 100% CPU.
                        if (n < 1) throw new InvalidDataException($"Host config '{jsonPath}': summaryIntervalSeconds must be >= 1 (got {n}).");
                        opt.SummaryIntervalSeconds = n;
                    }
                    if (r.TryGetProperty("statsEnabled", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) opt.StatsEnabled = v.GetBoolean();
                    if (r.TryGetProperty("cors", out v) && v.ValueKind == JsonValueKind.Array)
                        opt.Cors = v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                    if (r.TryGetProperty("trustedProxies", out v) && v.ValueKind == JsonValueKind.Array)
                    {
                        opt.TrustedProxies = v.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString()!)
                            .ToList();
                        ValidateTrustedProxies(opt.TrustedProxies, $"Host config '{jsonPath}': \"trustedProxies\"");
                    }
                    if (r.TryGetProperty("apps", out v) && v.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in v.EnumerateObject())
                        {
                            if (prop.Value.ValueKind != JsonValueKind.String) continue;
                            // The name becomes the /<name> URL prefix. An empty name mounts at "/"
                            // but the unknown-prefix filter then 404s everything; a name with a
                            // slash can never match a first path segment. Both leave the server
                            // up but serving nothing — fail fast instead.
                            if (string.IsNullOrWhiteSpace(prop.Name) || prop.Name.Contains('/') || prop.Name.Contains('\\'))
                            {
                                throw new InvalidDataException(
                                    $"Host config '{jsonPath}': invalid app name '{prop.Name}' in \"apps\" — names must be non-empty and must not contain slashes (each becomes the /<name> URL prefix).");
                            }
                            opt.Apps[prop.Name] = prop.Value.GetString()!;
                        }
                    }
                }
            }

            string? legacy = config["Patcher:Root"];
            if (!string.IsNullOrWhiteSpace(legacy)) opt.Root = legacy;

            // Also honored from IConfiguration (env var / host settings) so in-process
            // deployments and tests can set it without a json file, like Patcher:Root.
            string? trustedFromConfig = config["TrustedProxies"];
            if (!string.IsNullOrWhiteSpace(trustedFromConfig))
            {
                opt.TrustedProxies = SplitList(trustedFromConfig);
                ValidateTrustedProxies(opt.TrustedProxies, "TrustedProxies setting");
            }

            if (GetArg(args, "--root") is { } root) opt.Root = root;
            if (GetArg(args, "--urls") is { } urls) opt.Urls = urls;
            if (GetArg(args, "--port") is { } port)
            {
                // Same validation the setup wizard applies: an out-of-range port crashes
                // Kestrel at bind time with a raw ArgumentOutOfRangeException, and a typo'd
                // value was silently ignored (the host then listens on the default port).
                if (!SetupWizard.IsValidPort(port))
                    throw new InvalidDataException($"--port '{port}' is not a valid port (1-65535).");
                opt.Urls = $"http://0.0.0.0:{int.Parse(port.Trim())}";
            }
            if (GetArg(args, "--rate-limit") is { } rl)
            {
                if (!int.TryParse(rl, out int rln) || rln < 0)
                    throw new InvalidDataException($"--rate-limit '{rl}' must be a number >= 0 (0 disables the limit). A negative limit makes every request fail with HTTP 500.");
                opt.RequestsPerMinutePerIp = rln == 0 ? int.MaxValue : rln;
            }
            if (GetArg(args, "--abuse-threshold") is { } ab)
            {
                if (!int.TryParse(ab, out int abn) || abn < 0)
                    throw new InvalidDataException($"--abuse-threshold '{ab}' must be a number >= 0.");
                opt.AbuseThresholdPerMinute = abn;
            }
            if (GetArg(args, "--cors") is { } cors) opt.Cors = SplitList(cors);
            if (GetArg(args, "--trusted-proxies") is { } proxies)
            {
                opt.TrustedProxies = SplitList(proxies);
                ValidateTrustedProxies(opt.TrustedProxies, "--trusted-proxies");
            }
            if (args.Contains("--no-stats", StringComparer.OrdinalIgnoreCase)) opt.StatsEnabled = false;

            return opt;
        }

        static List<string> SplitList(string value) =>
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        static void ValidateTrustedProxies(List<string> proxies, string source)
        {
            foreach (string p in proxies)
            {
                if (!System.Net.IPAddress.TryParse(p, out _))
                {
                    throw new InvalidDataException(
                        $"{source}: '{p}' is not a valid IP address. Use literal proxy IPs (no hostnames or CIDR ranges), e.g. \"10.0.0.5\".");
                }
            }
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
