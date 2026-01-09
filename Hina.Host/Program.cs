using System.IO;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Simple static host for manifest and chunks.
string root = ResolveRoot(args, app.Configuration["Patcher:Root"]);
Directory.CreateDirectory(root);

var fileProvider = new PhysicalFileProvider(Path.GetFullPath(root));
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });

app.MapGet("/health", () => Results.Ok("ok"));

app.Run();

static string ResolveRoot(string[] args, string? configuredRoot)
{
    string? configPath = GetArgValue(args, "--config");
    if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
    {
        string? fileRoot = TryReadRoot(configPath);
        if (!string.IsNullOrWhiteSpace(fileRoot))
        {
            return fileRoot;
        }
    }

    if (File.Exists("hina.host.json"))
    {
        string? fileRoot = TryReadRoot("hina.host.json");
        if (!string.IsNullOrWhiteSpace(fileRoot))
        {
            return fileRoot;
        }
    }

    return configuredRoot ?? "patch";
}

static string? TryReadRoot(string path)
{
    string json = File.ReadAllText(path);
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    if (doc.RootElement.TryGetProperty("root", out var rootProp))
    {
        return rootProp.GetString();
    }
    return null;
}

static string? GetArgValue(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}
