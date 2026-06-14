using System.Collections.Generic;
using System.Linq;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Sandbox;

namespace Hina.PackageManager.Tests
{
    public class SandboxDiffTests
    {
        private static SandboxSpec Sb(bool enabled, IEnumerable<(string path, string access)>? fs = null, CapabilitySpec? caps = null)
            => new SandboxSpec
            {
                Enabled = enabled,
                Filesystem = (fs ?? System.Array.Empty<(string, string)>())
                    .Select(t => new FsRule { Path = t.path, Access = t.access }).ToList(),
                Capabilities = caps,
            };

        [Fact]
        public void NoChange_NotBroadened()
        {
            SandboxSpec s = Sb(true, new[] { ("home", "ro") });
            Assert.False(SandboxDiff.Compute(s, Sb(true, new[] { ("home", "ro") })).Broadened);
        }

        [Fact]
        public void NewPath_Broadened()
        {
            SandboxDiff d = SandboxDiff.Compute(Sb(true, new[] { ("home", "ro") }),
                                                Sb(true, new[] { ("home", "ro"), ("xdg-documents", "ro") }));
            Assert.True(d.Broadened);
            Assert.Contains(d.Added, a => a.Contains("xdg-documents"));
        }

        [Fact]
        public void RoToRw_Broadened()
        {
            SandboxDiff d = SandboxDiff.Compute(Sb(true, new[] { ("home", "ro") }),
                                                Sb(true, new[] { ("home", "rw") }));
            Assert.True(d.Broadened);
        }

        [Fact]
        public void HostAdded_Broadened()
        {
            SandboxDiff d = SandboxDiff.Compute(Sb(true, new[] { ("home", "ro") }),
                                                Sb(true, new[] { ("host", "rw") }));
            Assert.True(d.Broadened);
            Assert.Contains(d.Added, a => a.Contains("host"));
        }

        [Fact]
        public void CapabilityAdded_Broadened()
        {
            SandboxDiff d = SandboxDiff.Compute(Sb(true), Sb(true, caps: new CapabilitySpec { Network = true }));
            Assert.True(d.Broadened);
            Assert.Contains(d.Added, a => a.ToLowerInvariant().Contains("network"));
        }

        [Fact]
        public void SandboxRemoved_Broadened()
        {
            // Was sandboxed, new version drops it entirely → app regains full access.
            SandboxDiff d = SandboxDiff.Compute(Sb(true, new[] { ("home", "ro") }), Sb(false));
            Assert.True(d.Broadened);
        }

        [Fact]
        public void PathRemoved_NotBroadened()
        {
            SandboxDiff d = SandboxDiff.Compute(Sb(true, new[] { ("home", "ro"), ("tmp", "rw") }),
                                                Sb(true, new[] { ("home", "ro") }));
            Assert.False(d.Broadened);
            Assert.Contains(d.Removed, r => r.Contains("tmp"));
        }

        [Fact]
        public void RwToRo_NotBroadened()
        {
            Assert.False(SandboxDiff.Compute(Sb(true, new[] { ("home", "rw") }),
                                             Sb(true, new[] { ("home", "ro") })).Broadened);
        }

        [Fact]
        public void SandboxNewlyEnabled_NotBroadened()
        {
            // Was unsandboxed (full access), new version sandboxes it → strictly narrowing.
            Assert.False(SandboxDiff.Compute(null, Sb(true, new[] { ("home", "ro") })).Broadened);
        }

        [Fact]
        public void BothUnsandboxed_NotBroadened()
        {
            Assert.False(SandboxDiff.Compute(null, null).Broadened);
        }

        // ── BUG-017: 'host' token treated as maximal set ──────────────────────

        [Fact]
        public void HostToSpecificPath_NotBroadened()
        {
            // Restricting from host (unrestricted) to a specific path is a narrowing.
            SandboxDiff d = SandboxDiff.Compute(
                Sb(true, new[] { ("host", "rw") }),
                Sb(true, new[] { ("home", "ro") }));
            Assert.False(d.Broadened);
            Assert.DoesNotContain(d.Added, a => a.Contains("home"));
        }

        [Fact]
        public void HostToSpecificPath_PathAppearsInRemoved()
        {
            // The new specific path should be surfaced as a removed (narrowing) entry,
            // and the original host grant should also appear as removed.
            SandboxDiff d = SandboxDiff.Compute(
                Sb(true, new[] { ("host", "rw") }),
                Sb(true, new[] { ("home", "ro") }));
            Assert.False(d.Broadened);
            Assert.Contains(d.Removed, r => r.Contains("host"));
        }

        [Fact]
        public void SpecificPathToHost_Broadened()
        {
            // Going from a specific path to host (unrestricted) is a broadening.
            SandboxDiff d = SandboxDiff.Compute(
                Sb(true, new[] { ("home", "ro") }),
                Sb(true, new[] { ("host", "rw") }));
            Assert.True(d.Broadened);
            Assert.Contains(d.Added, a => a.Contains("host"));
        }

        [Fact]
        public void SpecificPathToHost_OldPathAppearsSuperseded()
        {
            // The previously granted specific path should appear in Removed (superseded).
            SandboxDiff d = SandboxDiff.Compute(
                Sb(true, new[] { ("home", "ro") }),
                Sb(true, new[] { ("host", "rw") }));
            Assert.Contains(d.Removed, r => r.Contains("home"));
        }

        [Fact]
        public void BothHost_NotBroadened()
        {
            // No change on the filesystem axis when both sides declare host.
            SandboxDiff d = SandboxDiff.Compute(
                Sb(true, new[] { ("host", "rw") }),
                Sb(true, new[] { ("host", "rw") }));
            Assert.False(d.Broadened);
        }

        [Fact]
        public void HostToMultiplePaths_NotBroadened()
        {
            // Even requesting several specific paths is still narrower than host.
            SandboxDiff d = SandboxDiff.Compute(
                Sb(true, new[] { ("host", "rw") }),
                Sb(true, new[] { ("home", "ro"), ("xdg-documents", "rw"), ("tmp", "rw") }));
            Assert.False(d.Broadened);
        }

        [Fact]
        public void HostToSpecificPath_WithCapabilityAdded_Broadened()
        {
            // Filesystem narrows (host → path) but a new capability is added → still broadened.
            SandboxDiff d = SandboxDiff.Compute(
                Sb(true, new[] { ("host", "rw") }),
                Sb(true, new[] { ("home", "ro") }, caps: new CapabilitySpec { Network = true }));
            Assert.True(d.Broadened);
            Assert.Contains(d.Added, a => a.ToLowerInvariant().Contains("network"));
        }

        [Fact]
        public void SpecificPathToHost_WithCapabilityRemoved_StillBroadened()
        {
            // Filesystem broadens (path → host); a capability is lost but host alone broadens.
            SandboxDiff d = SandboxDiff.Compute(
                Sb(true, new[] { ("home", "ro") }, caps: new CapabilitySpec { Network = true }),
                Sb(true, new[] { ("host", "rw") }));
            Assert.True(d.Broadened);
            Assert.Contains(d.Added, a => a.Contains("host"));
        }
    }
}
