using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hina.Core.Hashing
{
    public interface IHasher
    {
        string AlgorithmId { get; }
        Task<string> ComputeHashAsync(Stream stream, CancellationToken ct);
    }
}
