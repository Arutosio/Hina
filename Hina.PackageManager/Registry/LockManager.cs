using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hina.PackageManager.Registry
{
    // Process-wide exclusive lock on the registry file. All registry mutations must run
    // inside `using var l = await LockManager.AcquireAsync(...)`.
    public sealed class LockManager
    {
        private readonly string _lockFilePath;
        private readonly TimeSpan _maxWait;

        public LockManager(string lockFilePath, TimeSpan? maxWait = null)
        {
            _lockFilePath = lockFilePath;
            _maxWait = maxWait ?? TimeSpan.FromSeconds(30);
        }

        public async Task<RegistryLock> AcquireAsync(CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lockFilePath) ?? ".");

            TimeSpan delay = TimeSpan.FromMilliseconds(25);
            TimeSpan maxDelay = TimeSpan.FromMilliseconds(500);
            DateTimeOffset deadline = DateTimeOffset.UtcNow + _maxWait;

            while (true)
            {
                try
                {
                    FileStream stream = new FileStream(
                        _lockFilePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.DeleteOnClose);
                    return new RegistryLock(stream);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A held FileShare.None lock surfaces as IOException (sharing violation) or,
                    // on Windows under certain permission conditions, UnauthorizedAccessException.
                    // Retry both up to the deadline instead of failing fast on the latter.
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        throw new TimeoutException($"Could not acquire registry lock at '{_lockFilePath}' within {_maxWait}.");
                    }
                    await Task.Delay(delay, ct);
                    TimeSpan next = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
                    delay = next > maxDelay ? maxDelay : next;
                }
            }
        }
    }

    public sealed class RegistryLock : IDisposable
    {
        private readonly FileStream _stream;
        private bool _disposed;

        internal RegistryLock(FileStream stream)
        {
            _stream = stream;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
        }
    }
}
