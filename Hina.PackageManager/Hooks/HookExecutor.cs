using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Hooks
{
    // Dispatches each HookAction to the IPlatformIntegration and records the side-effect
    // path/key in the registry as HookEvidence. Same evidence is later read back at uninstall.
    public sealed class HookExecutor
    {
        private readonly IPlatformIntegration _platform;

        public HookExecutor(IPlatformIntegration platform)
        {
            _platform = platform;
        }

        public async Task<HookEvidence> ApplyAsync(HookAction hook, string appDir, CancellationToken ct)
        {
            switch (hook)
            {
                case AddToPathHook a:
                {
                    string targetAbs = Path.Combine(appDir, a.Target);
                    string evidence = await _platform.AddToPath(a.Name, targetAbs, ct);
                    return new HookEvidence { Action = "addToPath", Evidence = evidence };
                }

                case MimeTypeHook m:
                {
                    string evidence = await _platform.RegisterMimeType(m, appDir, ct);
                    return new HookEvidence { Action = "registerMimeType", Evidence = evidence };
                }

                case UrlSchemeHook u:
                {
                    string evidence = await _platform.RegisterUrlScheme(u, appDir, ct);
                    return new HookEvidence { Action = "registerUrlScheme", Evidence = evidence };
                }

                case InstallFontHook f:
                {
                    if (f.Files.Count == 0)
                    {
                        throw new InvalidOperationException("installFont hook requires at least one file.");
                    }
                    // installFont evidence is a comma-joined list of absolute installed-font paths.
                    List<string> installed = new List<string>(f.Files.Count);
                    foreach (string rel in f.Files)
                    {
                        string abs = Path.Combine(appDir, rel);
                        installed.Add(await _platform.InstallFont(abs, ct));
                    }
                    return new HookEvidence { Action = "installFont", Evidence = string.Join("|", installed) };
                }

                case AutostartHook au:
                {
                    string evidence = await _platform.RegisterAutostart(au, appDir, ct);
                    return new HookEvidence { Action = "registerAutostart", Evidence = evidence };
                }

                default:
                    throw new InvalidOperationException($"Unknown hook action: {hook.GetType().Name}");
            }
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
                    // Unknown action recorded by a future Hina version: leave on disk, no-op.
                    break;
            }
        }
    }
}
