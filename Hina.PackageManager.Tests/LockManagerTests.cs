using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Tests
{
    public class LockManagerTests : IDisposable
    {
        private readonly string _tempDir;

        public LockManagerTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-lock-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Acquire_WhenUncontested_ReturnsImmediately()
        {
            LockManager mgr = new LockManager(Path.Combine(_tempDir, "x.lock"));
            using RegistryLock l = await mgr.AcquireAsync();
            Assert.NotNull(l);
        }

        [Fact]
        public async Task Acquire_WhenAlreadyHeld_TimesOut()
        {
            string lockPath = Path.Combine(_tempDir, "contended.lock");

            LockManager first = new LockManager(lockPath);
            using RegistryLock _ = await first.AcquireAsync();

            LockManager second = new LockManager(lockPath, maxWait: TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAsync<TimeoutException>(() => second.AcquireAsync());
        }

        [Fact]
        public async Task Acquire_AfterPriorReleased_Succeeds()
        {
            string lockPath = Path.Combine(_tempDir, "sequential.lock");

            LockManager mgr = new LockManager(lockPath);

            using (RegistryLock first = await mgr.AcquireAsync()) { }
            using (RegistryLock second = await mgr.AcquireAsync()) { }
        }

        [Fact]
        public async Task Acquire_ConcurrentTasks_AreSerialized()
        {
            string lockPath = Path.Combine(_tempDir, "concurrent.lock");
            string counterFile = Path.Combine(_tempDir, "counter");
            await File.WriteAllTextAsync(counterFile, "0");

            int observedOverlaps = 0;
            int inCriticalSection = 0;

            async Task Worker()
            {
                LockManager mgr = new LockManager(lockPath, maxWait: TimeSpan.FromSeconds(5));
                using RegistryLock l = await mgr.AcquireAsync();

                int now = Interlocked.Increment(ref inCriticalSection);
                if (now > 1) Interlocked.Increment(ref observedOverlaps);

                await Task.Delay(20);

                Interlocked.Decrement(ref inCriticalSection);
            }

            await Task.WhenAll(Worker(), Worker(), Worker(), Worker());

            Assert.Equal(0, observedOverlaps);
        }
    }
}
