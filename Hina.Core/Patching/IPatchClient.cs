using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Configuration;

namespace Hina.Core.Patching
{
    // Public API for clients embedding the patcher.
    public interface IPatchClient
    {
        PatcherConfig Config { get; }
        Task<CheckResult> CheckAsync(string rootDir, CancellationToken ct);
        Task<PatchResult> PatchAsync(string rootDir, CancellationToken ct);
        Task<VerifyResult> VerifyAsync(string rootDir, CancellationToken ct);
        Task RollbackAsync(string rootDir, CancellationToken ct);
    }
}
