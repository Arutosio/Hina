using System;
using System.IO;
using System.Threading.Tasks;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    // `hina perms <app> [--list] [--grant <path>[:ro|:rw]] [--revoke <path>]`
    // Manages the user's runtime filesystem grants for a sandboxed app — the paths
    // allowed beyond what the descriptor declared. Mutations take the registry lock.
    internal static class PermsCommand
    {
        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                ctx.Logger.LogError("Usage: hina perms <app> [--list] [--grant <path>[:ro|:rw]] [--revoke <path>]");
                return 2;
            }

            string? grant = Args.GetValue(args, "--grant");
            string? revoke = Args.GetValue(args, "--revoke");

            // List-only path: read under lock, no write.
            if (grant == null && revoke == null)
            {
                Registry registry = await ctx.LoadRegistryLockedAsync();
                if (!registry.Apps.TryGetValue(name, out InstalledApp? app))
                {
                    ctx.Logger.LogError("'{Name}' is not installed.", name);
                    return 1;
                }
                if (app.UserGrants.Count == 0)
                {
                    Console.WriteLine($"{name}: no user grants.");
                }
                else
                {
                    Console.WriteLine($"{name} user grants:");
                    foreach (FsGrant g in app.UserGrants)
                    {
                        Console.WriteLine($"  {g.Access}  {g.Path}");
                    }
                }
                return 0;
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

        private static (string path, string access) ParseGrant(string spec)
        {
            string access = "ro";
            string pathPart = spec;
            if (spec.EndsWith(":rw", StringComparison.Ordinal)) { access = "rw"; pathPart = spec[..^3]; }
            else if (spec.EndsWith(":ro", StringComparison.Ordinal)) { pathPart = spec[..^3]; }
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
