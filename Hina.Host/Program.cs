using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;

if (args.Any(a => a is "--help" or "-h" or "/?"))
{
    PrintHelp();
    return 0;
}

bool forceSetup = args.Contains("--setup", StringComparer.OrdinalIgnoreCase);
string? explicitConfig = GetArgTop(args, "--config");
string defaultConfigPath = explicitConfig ?? "hina.host.json";

if (forceSetup || ShouldRunWizard(defaultConfigPath))
{
    if (!SetupWizard.Run(defaultConfigPath, forceSetup))
    {
        Console.Error.WriteLine("Setup skipped: no config written. Exiting.");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

HostOptions options = HostOptions.Load(args, builder.Configuration);

static bool ShouldRunWizard(string path)
{
    if (Console.IsInputRedirected) return false;
    if (!File.Exists(path)) return true;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;
        int props = 0;
        foreach (var _ in doc.RootElement.EnumerateObject()) { props++; break; }
        return props == 0;
    }
    catch { return true; }
}

static string? GetArgTop(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}

if (string.IsNullOrWhiteSpace(options.Urls) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    options.Urls = "http://0.0.0.0:49876";
}

if (!string.IsNullOrWhiteSpace(options.Urls))
{
    builder.WebHost.UseUrls(options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries));
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<AccessStats>();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(rl =>
{
    rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rl.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        string path = ctx.Request.Path.Value ?? "";
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/stats", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetNoLimiter("exempt");
        string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string app = Routing.ExtractApp(path, options);
        return RateLimitPartition.GetFixedWindowLimiter($"{ip}|{app}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.RequestsPerMinutePerIp,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
    rl.OnRejected = (ctx, _) =>
    {
        var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
        var stats = ctx.HttpContext.RequestServices.GetRequiredService<AccessStats>();
        string ip = ctx.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string appName = Routing.ExtractApp(ctx.HttpContext.Request.Path.Value ?? "", options);
        stats.RecordRejection(ip, appName);
        logger.LogWarning("Rate limit exceeded by {Ip} on app={App} path={Path} (possible abuse)",
            ip, appName, ctx.HttpContext.Request.Path);
        return ValueTask.CompletedTask;
    };
});

if (options.Cors.Count > 0)
{
    builder.Services.AddCors(c => c.AddDefaultPolicy(p => p
        .WithOrigins(options.Cors.ToArray())
        .WithMethods("GET", "HEAD")
        .AllowAnyHeader()));
}

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var stats = app.Services.GetRequiredService<AccessStats>();

// Resolve app mounts. If Apps is configured, each entry is a mount at /<name> → physical path.
// Otherwise fall back to single-root mode.
var mounts = new List<(string Name, string Path, string Mount)>();
if (options.Apps.Count > 0)
{
    foreach (var (name, path) in options.Apps)
    {
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            logger.LogWarning("App '{App}' path {Path} does not exist. Creating empty directory.", name, full);
            Directory.CreateDirectory(full);
        }
        mounts.Add((name, full, $"/{name}"));
    }
}
else
{
    string rootFull = Path.GetFullPath(options.Root);
    if (!Directory.Exists(rootFull))
    {
        logger.LogWarning("Patch root {Root} does not exist. Creating empty directory.", rootFull);
        Directory.CreateDirectory(rootFull);
    }
    mounts.Add(("default", rootFull, ""));
}

logger.LogInformation("Hina.Host starting. Urls={Urls} RateLimit={Limit}/min/(IP,App) Stats={Stats} Apps={Apps}",
    options.Urls ?? "(default)", options.RequestsPerMinutePerIp, options.StatsEnabled,
    string.Join(", ", mounts.Select(m => $"{m.Name}->{m.Path}")));

app.UseForwardedHeaders();
app.UseRateLimiter();
if (options.Cors.Count > 0) app.UseCors();

app.Use(async (ctx, next) =>
{
    string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    string path = ctx.Request.Path.Value ?? "";
    string appName = Routing.ExtractApp(path, options);
    stats.RecordRequest(ip, appName, path);

    if (path.Contains("manifest", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogInformation("Update check: app={App} ip={Ip} path={Path} ua={UserAgent}",
            appName, ip, path, ctx.Request.Headers.UserAgent.ToString());
    }
    else
    {
        logger.LogDebug("Request: app={App} ip={Ip} {Method} {Path}", appName, ip, ctx.Request.Method, path);
    }

    await next();

    if (stats.ShouldLogAbuse(ip, appName, options.AbuseThresholdPerMinute, out long count))
    {
        logger.LogWarning("Possible abuse from {Ip} on app={App}: {Count} requests in last minute", ip, appName, count);
    }
});

// If Apps is configured, reject unknown prefixes explicitly before static files.
if (options.Apps.Count > 0)
{
    var known = new HashSet<string>(options.Apps.Keys, StringComparer.OrdinalIgnoreCase);
    app.Use(async (ctx, next) =>
    {
        string path = ctx.Request.Path.Value ?? "";
        if (path is "/health" or "/stats")
        {
            await next();
            return;
        }
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0 || !known.Contains(segs[0]))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        await next();
    });
}

foreach (var m in mounts)
{
    var fp = new PhysicalFileProvider(m.Path);
    var defaultOpts = new DefaultFilesOptions { FileProvider = fp };
    var staticOpts = new StaticFileOptions
    {
        FileProvider = fp,
        OnPrepareResponse = ctx =>
        {
            string? p = ctx.Context.Request.Path.Value;
            if (p is null) return;
            if (p.EndsWith(".chunk.br", StringComparison.OrdinalIgnoreCase))
                ctx.Context.Response.Headers.CacheControl = "public, immutable, max-age=31536000";
            else if (p.Contains("manifest", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                ctx.Context.Response.Headers.CacheControl = "public, must-revalidate, max-age=30";
        },
    };
    if (!string.IsNullOrEmpty(m.Mount))
    {
        defaultOpts.RequestPath = m.Mount;
        staticOpts.RequestPath = m.Mount;
    }
    app.UseDefaultFiles(defaultOpts);
    app.UseStaticFiles(staticOpts);
}

app.MapGet("/health", () => Results.Ok("ok"));

if (options.StatsEnabled)
{
    app.MapGet("/stats", (HttpContext ctx) =>
    {
        if (!IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? IPAddress.None))
            return Results.NotFound();
        return Results.Json(stats.Snapshot());
    });
}

var summaryCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    var interval = TimeSpan.FromSeconds(options.SummaryIntervalSeconds);
    while (!summaryCts.IsCancellationRequested)
    {
        try { await Task.Delay(interval, summaryCts.Token); } catch { break; }
        var snap = stats.Snapshot();
        if (snap.TotalRequests == 0) continue;
        logger.LogInformation("Summary: total={Total} topIp={TopIp} topApp={TopApp} topPath={TopPath} rejections={Rej}",
            snap.TotalRequests, snap.TopIp, snap.TopApp, snap.TopPath, snap.Rejections);
    }
});

app.Lifetime.ApplicationStopping.Register(() => summaryCts.Cancel());

app.Run();
return 0;

static void PrintHelp()
{
    Console.WriteLine("""
        Hina.Host - static server for manifests and chunks (multi-app capable)

        Usage: Hina.Host [options]

        Options:
          --root <path>              Single-app: directory with manifest.json + chunks/ (default: patch)
          --port <n>                 Listen port (shortcut for --urls http://0.0.0.0:<n>)
          --urls <list>              Full URL bind list (semicolon separated)
          --config <path>            Path to hina.host.json
          --rate-limit <n>           Max requests/minute per (IP,App) (default: 600, 0 = disabled)
          --abuse-threshold <n>      Log warning when (IP,App) exceeds N req/min (default: 300)
          --no-stats                 Disable the /stats endpoint (loopback-only by default)
          --cors <origin[,origin]>   Enable CORS for the given origins
          --setup                    Run the interactive setup wizard (overwrites existing config)
          -h, --help                 Show this help

        On first run (no hina.host.json) an interactive wizard is launched
        automatically when stdin is a terminal. Pipe /dev/null or redirect input
        to skip and use built-in defaults.

        Multi-app mode: set "apps" in hina.host.json to serve several programs from one host:
          {
            "apps": {
              "gameA": "/var/patches/gameA",
              "gameB": "/srv/gameB/release"
            },
            "urls": "http://0.0.0.0:5000",
            "requestsPerMinutePerIp": 600
          }
        Each app is served under /<name>/... Clients set baseUrl = https://host/<name>/.
        When "apps" is set, "root" is ignored and unknown prefixes return 404.

        See docs/Host-Guide.md for deployment details.
        """);
}

static class SetupWizard
{
    public static bool Run(string outPath, bool force)
    {
        Console.WriteLine();
        Console.WriteLine("=== Hina.Host first-run setup ===");
        if (File.Exists(outPath) && force)
        {
            Console.Write($"'{outPath}' already exists. Overwrite? [y/N]: ");
            var confirm = Console.ReadLine();
            if (!string.Equals(confirm?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return false;
        }
        else if (!File.Exists(outPath))
        {
            Console.WriteLine($"No config found at '{outPath}'. Let's create one.");
        }
        else
        {
            Console.WriteLine($"Config '{outPath}' looks empty. Let's populate it.");
        }

        string port = Ask("Listen port (default 49876 is in the dynamic/private range)", "49876");
        string bindAll = Ask("Bind on all interfaces (0.0.0.0)? [Y/n]", "y").Trim().ToLowerInvariant();
        string host = bindAll is "" or "y" or "yes" ? "0.0.0.0" : "127.0.0.1";
        string urls = $"http://{host}:{port}";

        string mode = Ask("Mode: [s]ingle-app or [m]ulti-app?", "s").Trim().ToLowerInvariant();
        var json = new Dictionary<string, object> { ["urls"] = urls };

        if (mode.StartsWith("m"))
        {
            var apps = new Dictionary<string, string>();
            Console.WriteLine("Add apps. Leave app name blank to finish.");
            while (true)
            {
                string name = Ask("  App name (blank to stop)", "").Trim();
                if (string.IsNullOrEmpty(name)) break;
                string path = Ask($"  Path for '{name}'", $"./patches/{name}").Trim();
                apps[name] = path;
            }
            if (apps.Count == 0)
            {
                Console.WriteLine("No apps defined; falling back to single-app mode.");
                json["root"] = Ask("Patch root directory", "patch").Trim();
            }
            else
            {
                json["apps"] = apps;
            }
        }
        else
        {
            json["root"] = Ask("Patch root directory", "patch").Trim();
        }

        if (int.TryParse(Ask("Max requests/min per (IP,App)", "600"), out int rl) && rl > 0)
            json["requestsPerMinutePerIp"] = rl;
        if (int.TryParse(Ask("Abuse warning threshold/min", "300"), out int at) && at > 0)
            json["abuseThresholdPerMinute"] = at;

        string cors = Ask("CORS origins (comma-separated, blank for none)", "").Trim();
        if (!string.IsNullOrEmpty(cors))
            json["cors"] = cors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        string serialized = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outPath, serialized);
        Console.WriteLine();
        Console.WriteLine($"Wrote {Path.GetFullPath(outPath)}:");
        Console.WriteLine(serialized);
        Console.WriteLine("=== Setup complete. Starting host... ===");
        Console.WriteLine();
        return true;
    }

    static string Ask(string prompt, string def)
    {
        Console.Write(string.IsNullOrEmpty(def) ? $"{prompt}: " : $"{prompt} [{def}]: ");
        string? line = Console.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? def : line;
    }
}

static class Routing
{
    public static string ExtractApp(string path, HostOptions opt)
    {
        if (opt.Apps.Count == 0) return "default";
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return "unknown";
        return opt.Apps.ContainsKey(segs[0]) ? segs[0] : "unknown";
    }
}

sealed class HostOptions
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

sealed class AccessStats
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

record StatsSnapshot(long TotalRequests, long Rejections, string TopIp, string TopApp, string TopPath,
    Dictionary<string, long> TopIpApps, Dictionary<string, long> Apps, Dictionary<string, long> TopPaths,
    Dictionary<string, long> AppRejections);
