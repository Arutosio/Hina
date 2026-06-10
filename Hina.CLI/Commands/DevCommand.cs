using System;
using System.IO;
using System.Threading;
using Hina.Core.Configuration;
using Hina.Core.Patching;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Sandbox;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    // `hina dev <subcommand>` — original patcher operations, hidden from end-user help
    // but still available for app developers, CI, and troubleshooting.
    internal static class DevCommand
    {
        public static async System.Threading.Tasks.Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            ILoggerFactory loggerFactory = ctx.LoggerFactory;
            ILogger logger = ctx.Logger;
            CancellationToken ct = ctx.Ct;

            if (args.Length < 2)
            {
                PrintHelp();
                return 2;
            }

            string subcommand = args[1].ToLowerInvariant();
            string? root = Args.GetValue(args, "--dir");
            string? baseUrl = Args.GetValue(args, "--base");
            string? configPath = Args.GetValue(args, "--config");
            string? trustedKey = Args.GetValue(args, "--pubkey");
            string? channel = Args.GetValue(args, "--channel");

            if (subcommand == "help" || subcommand == "--help" || subcommand == "-h")
            {
                PrintHelp();
                return 0;
            }

            // sign-descriptor doesn't operate on a patch dir; route it before the --dir check.
            if (subcommand == "sign-descriptor")
            {
                return RunSignDescriptor(args, logger);
            }

            // sandbox-run applies a filesystem sandbox then execs a command. Used by the
            // Landlock integration test (and handy for manual sandbox debugging). It does
            // not operate on a patch dir, so route it before the --dir check.
            if (subcommand == "sandbox-run")
            {
                return RunSandbox(args, logger);
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                logger.LogError("Missing required --dir <path>");
                return 2;
            }

            // Mutate the loaded config in place: rebuilding it field-by-field (the old
            // ApplyOverrides) silently dropped retry/timeout/CDC settings from hina.config.json
            // whenever any CLI override was passed.
            PatcherConfig config = LoadConfigOrDefault(configPath);
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                // `new Uri` would throw a raw UriFormatException that never names the flag.
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? parsedBase))
                {
                    logger.LogError("--base '{Url}' is not a valid absolute URL (expected e.g. http://localhost:49876/).", baseUrl);
                    return 2;
                }
                config.BaseUrl = parsedBase;
            }
            if (!string.IsNullOrWhiteSpace(trustedKey)) config.TrustedPublicKey = trustedKey;
            if (!string.IsNullOrWhiteSpace(channel)) config.Channel = channel;

            if (string.IsNullOrWhiteSpace(config.BaseUrl?.ToString()))
            {
                logger.LogError("Missing required --base <url> or config baseUrl");
                return 2;
            }

            ILogger<PatchClient> clientLogger = loggerFactory.CreateLogger<PatchClient>();
            PatchClient client = new PatchClient(config, clientLogger);

            switch (subcommand)
            {
                case "check":
                    {
                        var res = await client.CheckAsync(root, ct);
                        logger.LogInformation("{Message}", res.Message);
                        return res.IsUpdateAvailable ? 1 : 0;
                    }
                case "patch":
                    {
                        var res = await client.PatchAsync(root, ct);
                        logger.LogInformation("{Message}", res.Message);
                        return res.Success ? 0 : 2;
                    }
                case "verify":
                    {
                        var res = await client.VerifyAsync(root, ct);
                        logger.LogInformation("{Message}", res.Message);
                        return res.Success ? 0 : 3;
                    }
                case "rollback":
                    await client.RollbackAsync(root, ct);
                    logger.LogInformation("Rollback complete");
                    return 0;
                case "cleanup":
                    PatchCleanup.Cleanup(root);
                    logger.LogInformation("Cleanup complete");
                    return 0;
                default:
                    logger.LogError("Unknown dev subcommand: {Sub}", subcommand);
                    PrintHelp();
                    return 2;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("hina dev <subcommand> [options]");
            Console.WriteLine();
            Console.WriteLine("Patcher subcommands (require --dir <path> --base <url>):");
            Console.WriteLine("  check                Check for updates against a manifest");
            Console.WriteLine("  patch                Apply updates from a manifest");
            Console.WriteLine("  verify               Verify local files against a manifest");
            Console.WriteLine("  rollback             Restore from the most recent backup");
            Console.WriteLine("  cleanup              Remove leftover .hina.tmp/.bak files");
            Console.WriteLine();
            Console.WriteLine("Publisher subcommands:");
            Console.WriteLine("  sign-descriptor --in <hina.app.json> --key <ed25519.priv.b64> [--out <path>]");
            Console.WriteLine("                       Attach an Ed25519 signature to a descriptor file.");
            Console.WriteLine();
            Console.WriteLine("Sandbox subcommand:");
            Console.WriteLine("  sandbox-run --app-dir <dir> [--allow <path>[:ro|:rw] ...] [--host] [--deny-network] -- <exec> [args...]");
            Console.WriteLine("                       Apply a filesystem sandbox (Landlock on Linux) then exec.");
            Console.WriteLine("                       --deny-network blocks TCP bind/connect (Landlock ABI >= 4 / kernel 6.7+).");
        }

        // `hina dev sandbox-run --app-dir <dir> [--allow <path>[:ro|:rw] ...] [--host] -- <exec> [args...]`
        // Applies a filesystem sandbox (Landlock on Linux) then execs the command. On
        // success the process image is replaced, so the exec's exit code is the result.
        // Drives the Landlock integration test; also useful for manual sandbox probing.
        private static int RunSandbox(string[] args, ILogger logger)
        {
            string? appDir = Args.GetValue(args, "--app-dir");
            if (string.IsNullOrWhiteSpace(appDir))
            {
                logger.LogError("Missing required --app-dir <path>");
                return 2;
            }

            // Everything after the first bare "--" is the command to run.
            int sep = Array.IndexOf(args, "--");
            if (sep < 0 || sep + 1 >= args.Length)
            {
                logger.LogError("Missing '-- <exec> [args...]'");
                return 2;
            }
            string exec = args[sep + 1];
            System.Collections.Generic.List<string> execArgs = new System.Collections.Generic.List<string>();
            for (int i = sep + 2; i < args.Length; i++) execArgs.Add(args[i]);

            // Collect every --allow <path>[:ro|:rw] before the separator.
            System.Collections.Generic.List<ResolvedFsRule> rules = new System.Collections.Generic.List<ResolvedFsRule>
            {
                new ResolvedFsRule(Path.GetFullPath(appDir), canWrite: false),
            };
            bool unrestricted = false;
            bool denyNetwork = false;
            for (int i = 0; i < sep; i++)
            {
                if (args[i] == "--host") { unrestricted = true; continue; }
                if (args[i] == "--deny-network") { denyNetwork = true; continue; }
                if (args[i] != "--allow" || i + 1 >= sep) continue;
                string spec = args[++i];
                bool rw = spec.EndsWith(":rw", StringComparison.Ordinal);
                string p = (spec.EndsWith(":rw", StringComparison.Ordinal) || spec.EndsWith(":ro", StringComparison.Ordinal))
                    ? spec[..^3] : spec;
                rules.Add(new ResolvedFsRule(Path.GetFullPath(p), rw));
            }

            SandboxPlan plan = new SandboxPlan(unrestricted, rules, restrictNetwork: denyNetwork);
            ISandboxLauncher launcher = SandboxLauncherFactory.Current(logger);
            logger.LogDebug("Sandbox backend supported: {Supported}", launcher.IsSupported);
            return launcher.Launch(Path.GetFullPath(exec), execArgs, plan, CancellationToken.None);
        }

        // `hina dev sign-descriptor --in hina.app.json --key priv.b64 [--out path]`
        // Publisher-side helper so the workflow doesn't require a custom signing tool.
        // Generate the key pair with `dotnet run --project Hina.Builder -- keygen`.
        private static int RunSignDescriptor(string[] args, ILogger logger)
        {
            string? inPath = Args.GetValue(args, "--in");
            string? keyPath = Args.GetValue(args, "--key");
            string? outPath = Args.GetValue(args, "--out");

            if (string.IsNullOrWhiteSpace(inPath))
            {
                logger.LogError("Missing required --in <hina.app.json>");
                return 2;
            }
            if (string.IsNullOrWhiteSpace(keyPath))
            {
                logger.LogError("Missing required --key <ed25519.priv.b64>");
                return 2;
            }
            if (!File.Exists(inPath))
            {
                logger.LogError("Descriptor not found: {Path}", inPath);
                return 2;
            }
            if (!File.Exists(keyPath))
            {
                logger.LogError("Private key not found: {Path}", keyPath);
                return 2;
            }

            string target = string.IsNullOrWhiteSpace(outPath) ? inPath : outPath;

            string json;
            try { json = File.ReadAllText(inPath); }
            catch (Exception ex) { logger.LogError("Read descriptor failed: {Message}", ex.Message); return 2; }

            AppDescriptor descriptor;
            try { descriptor = DescriptorParser.Parse(json); }
            catch (Exception ex) { logger.LogError("Parse descriptor failed: {Message}", ex.Message); return 2; }

            // Validate before signing so publishers get clear errors instead of shipping
            // garbage that fails at install time.
            ValidationResult v = DescriptorValidator.Validate(descriptor, new ValidationContext { AllowInsecure = true });
            if (!v.IsValid)
            {
                logger.LogError("Descriptor validation failed:\n  - {Errors}", string.Join("\n  - ", v.Errors));
                return 2;
            }

            byte[] privateKey;
            try { privateKey = Convert.FromBase64String(File.ReadAllText(keyPath).Trim()); }
            catch (Exception ex) { logger.LogError("Decode key failed: {Message}", ex.Message); return 2; }

            try
            {
                DescriptorSigner.AttachSignature(descriptor, privateKey);
            }
            catch (Exception ex)
            {
                logger.LogError("Sign failed: {Message}", ex.Message);
                return 2;
            }

            try
            {
                File.WriteAllText(target, DescriptorParser.Serialize(descriptor));
            }
            catch (Exception ex)
            {
                logger.LogError("Write descriptor failed: {Message}", ex.Message);
                return 2;
            }

            Console.WriteLine($"Signed descriptor → {target}");
            return 0;
        }

        private static PatcherConfig LoadConfigOrDefault(string? configPath)
        {
            if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath)) return PatcherConfigLoader.Load(configPath);
            if (File.Exists("hina.config.json")) return PatcherConfigLoader.Load("hina.config.json");
            return new PatcherConfig();
        }

    }
}
