using System.IO;
using Hina.Core.Cli;
using Hina.Core.Crypto;
using Microsoft.Extensions.Logging;

namespace Hina.Builder
{
    // `hina-builder keygen` — generates an Ed25519 key pair for signing manifests and
    // descriptors. Extracted from Program so the init wizard can offer to generate keys
    // when none are present.
    internal static class KeygenCommand
    {
        // Generates a key pair under outDir as <name>.key.b64 / <name>.pub.b64 and returns
        // the two paths so callers (the wizard) can reuse them for signing.
        public static (string PrivatePath, string PublicPath) Generate(string outDir, string name, ILogger logger)
        {
            Directory.CreateDirectory(outDir);
            (string PrivateKeyBase64, string PublicKeyBase64) pair = KeyGenerator.GenerateEd25519();

            string privPath = Path.Combine(outDir, $"{name}.key.b64");
            string pubPath = Path.Combine(outDir, $"{name}.pub.b64");

            File.WriteAllText(privPath, pair.PrivateKeyBase64);
            File.WriteAllText(pubPath, pair.PublicKeyBase64);

            logger.LogInformation("Key pair generated: {PrivateKeyPath}, {PublicKeyPath}", privPath, pubPath);
            return (privPath, pubPath);
        }

        public static int Run(string[] args, ILogger logger)
        {
            string outDir = Args.GetValue(args, "--out") ?? ".";
            string name = Args.GetValue(args, "--name") ?? "hina";
            Generate(outDir, name, logger);
            return 0;
        }
    }
}
