using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.PackageManager.Hooks
{
    // Dispatches each HookAction to the IPlatformIntegration and records the side-effect
    // path/key in the registry as HookEvidence. Same evidence is later read back at uninstall.
    public sealed class HookExecutor
    {
        private readonly IPlatformIntegration _platform;
        private readonly ILogger _logger;

        public HookExecutor(IPlatformIntegration platform, ILogger? logger = null)
        {
            _platform = platform;
            _logger = logger ?? NullLogger.Instance;
        }

        public async Task<HookEvidence> ApplyAsync(HookAction hook, string appDir, string appName, CancellationToken ct)
            => await ApplyAsync(hook, appDir, appName, System.Array.Empty<ShellEntry>(), ct);

        // `entries` are the descriptor's shell entries; MIME/URL/autostart hooks reference one by
        // EntryId and need its resolved executable path so the OS writes a working launch command.
        public async Task<HookEvidence> ApplyAsync(HookAction hook, string appDir, string appName, IReadOnlyList<ShellEntry> entries, CancellationToken ct)
        {
            string identity = HookIdentity.For(hook);
            switch (hook)
            {
                case AddToPathHook a:
                {
                    string targetAbs = Path.Combine(appDir, a.Target);
                    string evidence = await _platform.AddToPath(a.Name, targetAbs, ct);
                    return new HookEvidence { Action = "addToPath", Identity = identity, Evidence = evidence };
                }

                case MimeTypeHook m:
                {
                    string evidence = await _platform.RegisterMimeType(m, appDir, ResolveExecAbs(m.EntryId, appDir, entries), ct);
                    return new HookEvidence { Action = "registerMimeType", Identity = identity, Evidence = evidence };
                }

                case UrlSchemeHook u:
                {
                    string evidence = await _platform.RegisterUrlScheme(u, appDir, ResolveExecAbs(u.EntryId, appDir, entries), ct);
                    return new HookEvidence { Action = "registerUrlScheme", Identity = identity, Evidence = evidence };
                }

                case InstallFontHook f:
                {
                    if (f.Files.Count == 0)
                    {
                        throw new InvalidOperationException("installFont hook requires at least one file.");
                    }
                    // Per-app font install: appName is prefixed onto the destination filename
                    // so two apps can ship the same font filename without collision (H4).
                    List<string> installed = new List<string>(f.Files.Count);
                    foreach (string rel in f.Files)
                    {
                        string abs = Path.Combine(appDir, rel);
                        installed.Add(await _platform.InstallFont(abs, appName, ct));
                    }
                    return new HookEvidence { Action = "installFont", Identity = identity, Evidence = string.Join("|", installed) };
                }

                case AutostartHook au:
                {
                    string evidence = await _platform.RegisterAutostart(au, appDir, ResolveExecAbs(au.EntryId, appDir, entries), ct);
                    return new HookEvidence { Action = "registerAutostart", Identity = identity, Evidence = evidence };
                }

                default:
                    throw new InvalidOperationException($"Unknown hook action: {hook.GetType().Name}");
            }
        }

        // Resolve the absolute executable path for the entry the hook targets. EntryId is validated
        // to reference an existing entries[].id, so this normally finds a match; returns null if not.
        private static string? ResolveExecAbs(string entryId, string appDir, IReadOnlyList<ShellEntry> entries)
        {
            foreach (ShellEntry e in entries)
            {
                if (string.Equals(e.Id, entryId, StringComparison.Ordinal))
                {
                    return Path.Combine(appDir, e.Exec);
                }
            }
            return null;
        }

        public async Task UndoAsync(HookEvidence evidence, CancellationToken ct)
        {
            switch (evidence.Action)
            {
                case "addToPath":
                    await _platform.RemoveFromPath(evidence.Evidence, ct);
                    break;
                case "registerMimeType":
                    await _platform.UnregisterMimeType(evidence.Evidence, ct);
                    break;
                case "registerUrlScheme":
                    await _platform.UnregisterUrlScheme(evidence.Evidence, ct);
                    break;
                case "installFont":
                    foreach (string path in evidence.Evidence.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        await _platform.UninstallFont(path, ct);
                    }
                    break;
                case "registerAutostart":
                    await _platform.UnregisterAutostart(evidence.Evidence, ct);
                    break;
                default:
                    // Unknown action recorded by a future Hina version: log so the user
                    // knows the uninstall may leave residue, but don't throw — uninstall
                    // must be fail-soft.
                    _logger.LogWarning("Skipping unknown hook action '{Action}'; uninstall may leave residue at {Evidence}.",
                        evidence.Action, evidence.Evidence);
                    break;
            }
        }
    }
}
