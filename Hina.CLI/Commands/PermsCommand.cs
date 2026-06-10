using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Registry;
using Hina.PackageManager.Sandbox;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    // Aliases: perms / permissions / permessi.
    //   hina perms [list]                  → table of all installed apps' permissions
    //   hina perms <app>                   → full permission detail for one app
    //   hina perms <app> --grant <path>[:ro|:rw] | --revoke <path>
    //                                      → manage the user's runtime filesystem grants
    // Mutations take the registry lock; reads go through the shared read-lock helper.
    internal static class PermsCommand
    {
        private static readonly HashSet<string> KnownFlags =
            new HashSet<string>(StringComparer.Ordinal) { "--grant", "--revoke" };

        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            string? unknownFlag = Args.FirstUnknownFlag(args, KnownFlags, startIndex: 1);
            if (unknownFlag != null)
            {
                ctx.Logger.LogError("Unknown flag '{Flag}'. Usage: hina perms [list] | <app> [--grant <path>[:ro|:rw]] [--revoke <path>]", unknownFlag);
                return 2;
            }

            string? name = Args.FirstPositional(args, startIndex: 1);
            string? grant = Args.GetValue(args, "--grant");
            string? revoke = Args.GetValue(args, "--revoke");

            // A bare `--grant` / `--revoke` (flag present but no path, or an empty path) used to
            // fall through to the read-only detail view, silently dropping the mutation the user
            // asked for. Treat it as a usage error so the missing path is obvious.
            if ((Args.HasFlag(args, "--grant") && string.IsNullOrWhiteSpace(grant))
                || (Args.HasFlag(args, "--revoke") && string.IsNullOrWhiteSpace(revoke)))
            {
                ctx.Logger.LogError("Usage: hina perms <app> [--grant <path>[:ro|:rw]] [--revoke <path>]");
                return 2;
            }

            // No app (or the literal "list") and no mutation → overview table.
            if ((string.IsNullOrWhiteSpace(name) || name == "list") && grant == null && revoke == null)
            {
                return await PrintTableAsync(ctx);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                ctx.Logger.LogError("Usage: hina perms [list] | <app> [--grant <path>[:ro|:rw]] [--revoke <path>]");
                return 2;
            }

            // App given, no mutation → detail view.
            if (grant == null && revoke == null)
            {
                return await PrintDetailAsync(ctx, name);
            }

            LockManager locks = ctx.NewLockManager();
            using RegistryLock l = await locks.AcquireAsync(ctx.Ct);
            RegistryStore store = ctx.NewRegistryStore();
            Registry reg = await store.LoadAsync(ctx.Ct);

            if (!reg.Apps.TryGetValue(name, out InstalledApp? target))
            {
                ctx.Logger.LogError("'{Name}' is not installed.", name);
                return 1;
            }

            if (grant != null)
            {
                (string path, string access) = ParseGrant(grant);
                target.UserGrants.RemoveAll(g => string.Equals(g.Path, path, StringComparison.Ordinal));
                target.UserGrants.Add(new FsGrant { Path = path, Access = access });
                ctx.Logger.LogInformation("Granted {Access} {Path} to '{Name}'.", access, path, name);
            }

            if (revoke != null)
            {
                string path = AbsPath(revoke);
                int removed = target.UserGrants.RemoveAll(g => string.Equals(g.Path, path, StringComparison.Ordinal));
                ctx.Logger.LogInformation(removed > 0
                    ? $"Revoked {path} from '{name}'."
                    : $"No grant for {path} on '{name}'.");
            }

            await store.SaveAsync(reg, ctx.Ct);
            return 0;
        }

        private static async Task<int> PrintTableAsync(CommandContext ctx)
        {
            Registry registry = await ctx.LoadRegistryLockedAsync();
            if (registry.Apps.Count == 0)
            {
                Console.WriteLine("No apps installed.");
                return 0;
            }

            List<AppPermissions> perms = registry.Apps.Values
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => AppPermissions.From(LoadDescriptorOrEmpty(ctx, a.Name), a))
                .ToList();

            Console.Write(PermissionsFormatter.Table(perms));
            return 0;
        }

        private static async Task<int> PrintDetailAsync(CommandContext ctx, string name)
        {
            Registry registry = await ctx.LoadRegistryLockedAsync();
            if (!registry.Apps.TryGetValue(name, out InstalledApp? app))
            {
                ctx.Logger.LogError("'{Name}' is not installed.", name);
                return 1;
            }

            AppPermissions perms = AppPermissions.From(LoadDescriptorOrEmpty(ctx, name), app);
            Console.Write(PermissionsFormatter.Detail(perms));
            return 0;
        }

        // The cached descriptor carries the declared sandbox scope + capabilities.
        // Pre-sandbox installs (or a missing/corrupt cache) simply read as "no
        // sandbox declared" rather than failing the whole listing.
        private static AppDescriptor LoadDescriptorOrEmpty(CommandContext ctx, string name)
        {
            string path = ctx.Paths.DescriptorCache(name);
            if (File.Exists(path))
            {
                try { return DescriptorParser.Parse(File.ReadAllText(path)); }
                catch { /* fall through to empty */ }
            }
            return new AppDescriptor { Name = name };
        }

        private static (string path, string access) ParseGrant(string spec)
        {
            string access = "ro";
            string pathPart = spec;
            if (spec.EndsWith(":rw", StringComparison.Ordinal)) { access = "rw"; pathPart = spec[..^3]; }
            else if (spec.EndsWith(":ro", StringComparison.Ordinal)) { pathPart = spec[..^3]; }
            if (string.IsNullOrWhiteSpace(pathPart))
            {
                // `--grant :rw` would otherwise reach Path.GetFullPath("") and surface as a
                // bare "The path is empty." with no hint which flag was malformed.
                throw new ArgumentException($"--grant '{spec}' is missing the path part. Expected --grant <path>[:ro|:rw].");
            }
            return (AbsPath(pathPart), access);
        }

        private static string AbsPath(string p)
        {
            if (p.StartsWith("~", StringComparison.Ordinal))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                p = home + p[1..];
            }
            return Path.GetFullPath(p);
        }
    }
}
