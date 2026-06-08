using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Hina.PackageManager.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // Real sandbox-exec enforcement test. Unlike the Linux/Landlock path, the macOS
    // dev host CAN run this, so it is the local proof that the Seatbelt profile
    // actually isolates: a granted dir is writable, an ungranted dir is not readable.
    public class MacOsSandboxTests
    {
        [Fact]
        public void Launch_AllowsGrantedWrite_DeniesUngrantedRead()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return; // sandbox-exec enforcement is macOS-only.
            }

            string work = Directory.CreateTempSubdirectory("hina-sbx-").FullName;
            try
            {
                string docs = Path.Combine(work, "docs");
                string secret = Path.Combine(work, "secret");
                Directory.CreateDirectory(docs);
                Directory.CreateDirectory(secret);
                File.WriteAllText(Path.Combine(secret, "key"), "topsecret");

                // docs is granted rw; secret is NOT granted. /bin (sh, cat) is in the
                // system baseline so the interpreter can run.
                SandboxPlan plan = new SandboxPlan(
                    unrestricted: false,
                    new List<ResolvedFsRule> { new ResolvedFsRule(docs, canWrite: true) },
                    restrictNetwork: false);

                string inner =
                    $"r=0; w=0; " +
                    $"cat '{secret}/key' >/dev/null 2>&1 && r=1; " +
                    $"echo data > '{docs}/out' 2>/dev/null && w=1; " +
                    $"echo \"$r$w\" > '{docs}/result'";

                MacOsSandbox sandbox = new MacOsSandbox(NullLogger.Instance);
                Assert.True(sandbox.IsSupported);
                sandbox.Launch("/bin/sh", new[] { "-c", inner }, plan, CancellationToken.None);

                string result = File.ReadAllText(Path.Combine(docs, "result")).Trim();
                Assert.Equal("01", result); // read denied (0), write allowed (1)
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { /* best-effort */ }
            }
        }
    }
}
