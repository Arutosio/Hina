using System;
using System.Threading.Tasks;
using Hina.PackageManager.Install;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class InstallCommand
    {
        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            string? url = Args.FirstPositional(args, startIndex: 1);
            if (string.IsNullOrWhiteSpace(url))
            {
                ctx.Logger.LogError("Usage: hina install <url-to-hina.app.json> [--allow-insecure]");
                return 2;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? descriptorUrl))
            {
                ctx.Logger.LogError("'{Url}' is not a valid absolute URL.", url);
                return 2;
            }

            bool allowInsecure = Args.HasFlag(args, "--allow-insecure");

            InstallService service = ctx.NewInstallService();

            InstallOptions options = new InstallOptions
            {
                AllowInsecure = allowInsecure,
                Network = NetworkArgs.FromArgs(args),
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
                InstallResult result = await service.InstallAsync(descriptorUrl, options, ctx.Ct);
                Console.WriteLine($"Installed {result.Name} {result.Version} → {result.InstallPath}");
                return 0;
            }
            catch (OperationCanceledException)
            {
                ctx.Logger.LogError("Install cancelled.");
                return 1;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogError("Install failed: {Message}", ex.Message);
                return 2;
            }
        }
    }
}
