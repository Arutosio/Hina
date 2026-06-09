using System.Net;
using System.Threading.RateLimiting;
using Hina.Host;
using Microsoft.AspNetCore.HttpOverrides;
// ASP.NET Core ships its own Microsoft.Extensions.Hosting.HostOptions; alias ours explicitly.
using HostOptions = Hina.Host.HostOptions;
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

// The wizard only makes sense on an interactive terminal; piped/redirected stdin
// (services, containers, tests) skips it and runs with defaults.
if (forceSetup || (!Console.IsInputRedirected && SetupWizard.IsConfigMissingOrEmpty(defaultConfigPath)))
{
    if (!SetupWizard.Run(defaultConfigPath, forceSetup))
    {
        Console.Error.WriteLine("Setup skipped: no config written. Exiting.");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

HostOptions options = HostOptions.Load(args, builder.Configuration);

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
        // ".chunk.br" (and any other unmapped extension in a patch root) must be served:
        // the default content-type provider has no ".br" mapping, so without this every
        // chunk request 404s.
        ServeUnknownFileTypes = true,
        DefaultContentType = "application/octet-stream",
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

// Exposes the implicit Program class so Hina.Host.Tests can boot the app in-process
// via WebApplicationFactory<Program>.
public partial class Program { }
