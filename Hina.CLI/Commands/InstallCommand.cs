using System;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Install;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class InstallCommand
    {
        public static async Task<int> RunAsync(string[] args, ILogger logger, CancellationToken ct)
        {
            string? url = Args.FirstPositional(args, startIndex: 1);
            if (string.IsNullOrWhiteSpace(url))
            {
                logger.LogError("Usage: hina install <url-to-hina.app.json> [--allow-insecure]");
                return 2;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? descriptorUrl))
            {
                logger.LogError("'{Url}' is not a valid absolute URL.", url);
                return 2;
            }

            bool allowInsecure = Args.HasFlag(args, "--allow-insecure");

            InstallPaths paths = InstallPaths.ForCurrentOs();
            IPlatformIntegration platform = PlatformIntegrationFactory.Current(paths);
            InstallService service = new InstallService(paths, platform);

            InstallOptions options = new InstallOptions
            {
                AllowInsecure = allowInsecure,
                OnFirstTimeTrust = prompt =>
                {
                    // M2: detect non-interactive stdin (cron, systemd, `ssh -T`, piped
                    // input). Console.ReadLine() returns null there and we'd otherwise
                    // silently reject the install with no useful diagnostic.
                    if (Console.IsInputRedirected)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine($"  App:        {prompt.AppName}");
                        Console.Error.WriteLine($"  Publisher:  {prompt.Publisher}");
                        Console.Error.WriteLine($"  Source:     {prompt.DescriptorUrl}");
                        Console.Error.WriteLine($"  Key fpr:    {prompt.PublicKeyFingerprint}");
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("Refusing to install: TOFU approval needs an interactive terminal. Re-run `hina install` from a TTY.");
                        return false;
                    }

                    Console.WriteLine();
                    Console.WriteLine($"  App:        {prompt.AppName}");
                    Console.WriteLine($"  Publisher:  {prompt.Publisher}");
                    Console.WriteLine($"  Source:     {prompt.DescriptorUrl}");
                    Console.WriteLine($"  Key fpr:    {prompt.PublicKeyFingerprint}");
                    Console.WriteLine();
                    Console.Write("Trust this publisher and install? [y/N] ");
                    string? reply = Console.ReadLine();
                    return reply != null && (reply.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) || reply.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
                }
            };

            try
            {
                InstallResult result = await service.InstallAsync(descriptorUrl, options, ct);
                Console.WriteLine($"Installed {result.Name} {result.Version} → {result.InstallPath}");
                return 0;
            }
            catch (OperationCanceledException)
            {
                logger.LogError("Install cancelled.");
                return 1;
            }
            catch (Exception ex)
            {
                logger.LogError("Install failed: {Message}", ex.Message);
                return 2;
            }
        }
    }
}
