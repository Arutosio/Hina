using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hina.PackageManager.Diagnostics;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Registry;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // `hina verify` local integrity: detects a missing executable / missing entry files
    // inside an existing install dir, and a missing descriptor cache.
    public class IntegrityTests : IDisposable
    {
        private readonly string _root;
        private readonly InstallPaths _paths;
        private readonly FakePlatformIntegration _platform = new FakePlatformIntegration();
        private readonly string _appDir;

        public IntegrityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "hina-integrity-" + Path.GetRandomFileName());
            _appDir = Path.Combine(_root, "Apps", "demo");
            Directory.CreateDirectory(Path.Combine(_appDir, "bin"));
            _paths = InstallPaths.ForRoot(_root);
            Seed();
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private void Seed()
        {
            File.WriteAllText(Path.Combine(_appDir, "bin", "demo"), "#!/bin/sh\n");
            Registry.Registry reg = new Registry.Registry();
            reg.Apps["demo"] = new InstalledApp { Name = "demo", InstalledVersion = "1.0.0", InstallPath = _appDir };
            new RegistryStore(_paths.RegistryFile).SaveAsync(reg).GetAwaiter().GetResult();

            Directory.CreateDirectory(_paths.DescriptorCacheRoot);
            File.WriteAllText(_paths.DescriptorCache("demo"),
                "{\"name\":\"demo\",\"exec\":{\"windows\":\"bin/demo\",\"linux\":\"bin/demo\",\"macos\":\"bin/demo\"}," +
                "\"entries\":[{\"id\":\"main\",\"name\":\"Demo\",\"exec\":\"bin/demo\"}]}");
        }

        private AppDiagnostic Inspect() => new RegistryVerifier(_paths, _platform).Inspect("demo").Single();

        [Fact]
        public void Healthy_WhenAllFilesPresent()
        {
            AppDiagnostic d = Inspect();
            Assert.True(d.IsHealthy, string.Join(",", d.MissingFiles));
            Assert.Empty(d.MissingFiles);
            Assert.False(d.DescriptorCacheMissing);
        }

        [Fact]
        public void DetectsMissingExecutable()
        {
            File.Delete(Path.Combine(_appDir, "bin", "demo"));
            AppDiagnostic d = Inspect();
            Assert.False(d.IsHealthy);
            Assert.Contains(d.MissingFiles, f => f.Contains("bin/demo"));
        }

        [Fact]
        public void DetectsMissingDescriptorCache()
        {
            File.Delete(_paths.DescriptorCache("demo"));
            AppDiagnostic d = Inspect();
            Assert.True(d.DescriptorCacheMissing);
            Assert.False(d.IsHealthy);
        }
    }
}
