using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Hina.Core.Hashing
{
    // Built-in strong hash (open source, no external deps).
    public sealed class Sha256Hasher : IHasher
    {
        public string AlgorithmId => "sha256";

        public async Task<string> ComputeHashAsync(Stream stream, CancellationToken ct)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = await sha.ComputeHashAsync(stream, ct);
                return "sha256:" + Hex.ToHexLower(hash);
            }
        }
    }
}
