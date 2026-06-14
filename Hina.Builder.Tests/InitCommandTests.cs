using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Builder.Init;
using Hina.PackageManager.Descriptor;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.Builder.Tests
{
    public sealed class InitCommandTests : IDisposable
    {
        private readonly string _payload;
        private readonly string _out;

        public InitCommandTests()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), "hina-init-" + Guid.NewGuid().ToString("N"));
            _payload = Path.Combine(baseDir, "payload");
            _out = Path.Combine(baseDir, "dist");
            Directory.CreateDirectory(_payload);
        }

        public void Dispose()
        {
            try { Directory.Delete(Directory.GetParent(_payload)!.FullName, recursive: true); } catch { /* best-effort */ }
        }

        private void WriteElf(string name)
        {
            byte[] body = new byte[64];
            byte[] elf = { 0x7F, 0x45, 0x4C, 0x46 };
            Array.Copy(elf, body, elf.Length);
            File.WriteAllBytes(Path.Combine(_payload, name), body);
        }

        // Full wizard run over a single-OS (Linux) payload. The scripted prompt answers each
        // question in order; queues are per-method so they don't have to interleave.
        private ScriptedPrompt SingleLinuxScript() => new ScriptedPrompt(
            asks: new[]
            {
                "mygame",                        // app id
                "My Game",                       // display name
                "1.2.3",                         // version
                "Acme",                          // publisher
                "A fun game",                    // description
                "",                              // homepage (none)
                "https://patch.example.com/",    // base url
                "",                              // windows exec (none)
                "",                              // macos exec (none)
                "",                              // launcher name (→ default "My Game")
                "",                              // icon (none)
                _out                             // output folder (keys + patch)
            },
            confirms: new[]
            {
                true,   // use 'game' as the linux executable
                true,   // create a launcher
                true,   // run in a sandbox
                true,   // needs internet
                true    // generate a key pair
            },
            chooses: new[] { 1 }); // data location → config

        [Fact]
        public async Task Init_SingleOsLinux_WritesSignedValidDescriptorAndPatch()
        {
            WriteElf("game");
            File.WriteAllText(Path.Combine(_payload, "data.bin"), "some asset data");

            int code = await InitCommand.RunAsync(
                new[] { "init", "--input", _payload },
                SingleLinuxScript(),
                NullLogger.Instance,
                CancellationToken.None);

            Assert.Equal(0, code);

            // Descriptor written into the project folder, signed and valid.
            string descPath = Path.Combine(_payload, "hina.app.json");
            Assert.True(File.Exists(descPath));
            AppDescriptor d = DescriptorParser.Parse(await File.ReadAllTextAsync(descPath));

            Assert.Equal("mygame", d.Name);
            Assert.Equal("1.2.3", d.Version);
            Assert.Equal("Acme", d.Publisher);
            Assert.Equal("game", d.Exec.Linux);
            Assert.Null(d.Exec.Windows);
            Assert.Single(d.Entries);
            Assert.Equal("game", d.Entries[0].Exec);
            Assert.True(d.Sandbox!.Enabled);
            Assert.True(d.Sandbox.Capabilities!.Network);
            Assert.NotNull(d.DescriptorSignature);
            Assert.True(DescriptorValidator.Validate(d).IsValid);

            // Keys + patch written OUTSIDE the payload.
            Assert.True(File.Exists(Path.Combine(_out, "patch", "manifest.json")));
            Assert.True(Directory.Exists(Path.Combine(_out, "keys")));
            // The private key must NOT be inside the shipped payload.
            Assert.Empty(Directory.GetFiles(_payload, "*.key.b64", SearchOption.AllDirectories));
        }

        private void WriteBin(string relDir, string name, byte[] magic)
        {
            string dir = Path.Combine(_payload, relDir);
            Directory.CreateDirectory(dir);
            byte[] body = new byte[64];
            Array.Copy(magic, body, magic.Length);
            File.WriteAllBytes(Path.Combine(dir, name), body);
        }

        [Fact]
        public async Task Init_MultiVariantSubdirs_WritesPlatformsAndPerVariantManifests()
        {
            // Per-platform subdirs named by token; each holds one OS's executable.
            WriteBin("windows-x64", "Game.exe", new byte[] { 0x4D, 0x5A, 0x90, 0x00 });   // PE
            WriteBin("macos-arm64", "Game", new byte[] { 0xCF, 0xFA, 0xED, 0xFE });        // Mach-O
            WriteBin("linux", "game", new byte[] { 0x7F, 0x45, 0x4C, 0x46 });              // ELF

            ScriptedPrompt script = new ScriptedPrompt(
                asks: new[]
                {
                    "mygame", "My Game", "1.2.3", "Acme", "A fun game", "",
                    "https://patch.example.com/",
                    _out                              // output folder
                },
                confirms: new[]
                {
                    true, true, true,   // use detected exec for each of the 3 variants
                    true,               // sandbox
                    true,               // internet
                    true                // generate key
                },
                chooses: new[] { 1 });

            int code = await InitCommand.RunAsync(
                new[] { "init", "--input", _payload }, script, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, code);

            AppDescriptor d = DescriptorParser.Parse(
                await File.ReadAllTextAsync(Path.Combine(_payload, "hina.app.json")));

            Assert.Equal(3, d.Platforms.Count);
            Assert.Empty(d.Exec.Windows ?? "");   // legacy exec map left empty
            Assert.Contains(d.Platforms, p => p.Os == "windows" && p.Arch == "x64" && p.Exec == "Game.exe");
            Assert.Contains(d.Platforms, p => p.Os == "macos" && p.Arch == "arm64" && p.Exec == "Game");
            Assert.Contains(d.Platforms, p => p.Os == "linux" && p.Arch == null && p.Exec == "game");
            Assert.NotNull(d.DescriptorSignature);
            Assert.True(DescriptorValidator.Validate(d).IsValid);

            // One manifest per variant, shared chunk store, all outside the payload.
            string patch = Path.Combine(_out, "patch");
            Assert.True(File.Exists(Path.Combine(patch, "manifest.windows-x64.json")));
            Assert.True(File.Exists(Path.Combine(patch, "manifest.macos-arm64.json")));
            Assert.True(File.Exists(Path.Combine(patch, "manifest.linux.json")));
            Assert.True(Directory.Exists(Path.Combine(patch, "chunks")));
        }

        [Fact]
        public async Task Init_VariantSubdirsWithCommonFolder_MergesCommonIntoEveryVariantManifest()
        {
            WriteBin("windows-x64", "Game.exe", new byte[] { 0x4D, 0x5A, 0x90, 0x00 });   // PE
            WriteBin("linux", "game", new byte[] { 0x7F, 0x45, 0x4C, 0x46 });              // ELF
            string commonDir = Path.Combine(_payload, "common");
            Directory.CreateDirectory(commonDir);
            await File.WriteAllTextAsync(Path.Combine(commonDir, "shared.dat"), "game data shared by every OS");

            ScriptedPrompt script = new ScriptedPrompt(
                asks: new[]
                {
                    "mygame", "My Game", "1.2.3", "Acme", "A fun game", "",
                    "https://patch.example.com/",
                    _out
                },
                confirms: new[]
                {
                    true, true,   // use detected exec for each of the 2 variants
                    true,         // sandbox
                    true,         // internet
                    true          // generate key
                },
                chooses: new[] { 1 });

            int code = await InitCommand.RunAsync(
                new[] { "init", "--input", _payload }, script, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, code);

            string patch = Path.Combine(_out, "patch");
            foreach (string manifestName in new[] { "manifest.windows-x64.json", "manifest.linux.json" })
            {
                var manifest = await Hina.Core.Manifest.ManifestSerializer.ReadAsync(
                    Path.Combine(patch, manifestName), CancellationToken.None);
                // Shared file present at its root-relative path, NOT under a common/ prefix.
                Assert.Contains(manifest.Files, f => f.Path == "shared.dat");
                Assert.DoesNotContain(manifest.Files, f => f.Path.StartsWith("common/", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task Init_SinglePayloadWithCommonSubfolder_PackagedAsOrdinaryContent()
        {
            // No variant subdirs: a folder named "common" is just app content and keeps its
            // path prefix in the manifest.
            WriteElf("game");
            string commonDir = Path.Combine(_payload, "common");
            Directory.CreateDirectory(commonDir);
            await File.WriteAllTextAsync(Path.Combine(commonDir, "shared.dat"), "asset");

            int code = await InitCommand.RunAsync(
                new[] { "init", "--input", _payload },
                SingleLinuxScript(),
                NullLogger.Instance,
                CancellationToken.None);

            Assert.Equal(0, code);
            var manifest = await Hina.Core.Manifest.ManifestSerializer.ReadAsync(
                Path.Combine(_out, "patch", "manifest.json"), CancellationToken.None);
            Assert.Contains(manifest.Files, f => f.Path == "common/shared.dat");
        }

        [Fact]
        public async Task Init_ReRun_UsesExistingDescriptorAsDefaults()
        {
            WriteElf("game");

            // First run creates hina.app.json in the payload.
            int first = await InitCommand.RunAsync(
                new[] { "init", "--input", _payload }, SingleLinuxScript(), NullLogger.Instance, CancellationToken.None);
            Assert.Equal(0, first);

            // Second run: press Enter on every text field (empty asks → defaults). The defaults
            // must come from the existing descriptor (id "mygame", version "1.2.3", publisher
            // "Acme"), proving re-run = edit. Still answer the new output-folder + key prompts.
            ScriptedPrompt rerun = new ScriptedPrompt(
                asks: new[]
                {
                    "", "", "", "", "", "", "",   // id, name, version, publisher, desc, homepage, baseurl
                    "", "",                       // windows exec, macos exec
                    "", "",                       // launcher name, icon
                    _out                          // output folder
                },
                confirms: new[]
                {
                    true,   // use 'game' as linux exec
                    true,   // create launcher
                    true,   // sandbox
                    true,   // internet
                    true,   // (key already exists from first run → this confirm is unused, harmless)
                    true    // overwrite existing hina.app.json
                },
                chooses: new[] { 1 });

            int second = await InitCommand.RunAsync(
                new[] { "init", "--input", _payload }, rerun, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(0, second);

            AppDescriptor d = DescriptorParser.Parse(
                await File.ReadAllTextAsync(Path.Combine(_payload, "hina.app.json")));
            Assert.Equal("mygame", d.Name);
            Assert.Equal("1.2.3", d.Version);
            Assert.Equal("Acme", d.Publisher);
            Assert.Equal("https://patch.example.com/", d.BaseUrl);
        }

        // ---- BUG-013: CollectVariants must not propose a wrong-OS executable as default ----

        // When a variant folder (e.g. "linux") contains only a Windows PE binary that the
        // detector misidentifies or that got cross-copied, the wizard must NOT auto-confirm it
        // as the linux default. After the fix the OS-matched candidate is null and the wizard
        // drops to the manual-ask path; because the scripted prompt answers that ask with "",
        // the variant is skipped and the returned platforms list is empty → RunAsync returns 1.
        [Fact]
        public async Task Init_VariantLinux_DoesNotProposeWindowsExeAsDefault()
        {
            // Place a Windows PE binary inside the "linux" variant folder.
            // ExecutableDetector will detect it as Windows (TargetOs.Windows), not Linux.
            byte[] peMagic = { 0x4D, 0x5A, 0x90, 0x00 };
            WriteBin("linux", "Game.exe", peMagic);

            ScriptedPrompt script = new ScriptedPrompt(
                asks: new[]
                {
                    "mygame", "My Game", "1.2.3", "Acme", "A fun game", "",
                    "https://patch.example.com/",
                    // [linux] manual exec ask → blank means skip this variant
                    "",
                    _out
                },
                confirms: new[]
                {
                    // No Confirm for the variant (best == null after OS filter, so no auto-confirm prompt)
                    true,   // sandbox
                    true,   // internet
                    true    // generate key
                },
                chooses: new[] { 1 });

            int code = await InitCommand.RunAsync(
                new[] { "init", "--input", _payload }, script, NullLogger.Instance, CancellationToken.None);

            // No OS-matched executable found and user skipped manual entry → no variants → error exit.
            Assert.Equal(1, code);
        }

        // When the linux variant folder has the right ELF binary, CollectVariants must
        // propose it (OS matches) and the descriptor must record it under "linux", not "windows".
        [Fact]
        public async Task Init_VariantLinux_ProposesSameOsExecutableAsDefault()
        {
            WriteBin("linux", "game", new byte[] { 0x7F, 0x45, 0x4C, 0x46 });   // ELF

            ScriptedPrompt script = new ScriptedPrompt(
                asks: new[]
                {
                    "mygame", "My Game", "1.2.3", "Acme", "A fun game", "",
                    "https://patch.example.com/",
                    _out
                },
                confirms: new[]
                {
                    true,   // [linux] use 'game' as executable (OS-matched → auto-confirm offered)
                    true,   // sandbox
                    true,   // internet
                    true    // generate key
                },
                chooses: new[] { 1 });

            int code = await InitCommand.RunAsync(
                new[] { "init", "--input", _payload }, script, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, code);

            AppDescriptor d = DescriptorParser.Parse(
                await File.ReadAllTextAsync(Path.Combine(_payload, "hina.app.json")));

            Assert.Single(d.Platforms);
            Assert.Equal("linux", d.Platforms[0].Os);
            Assert.Equal("game", d.Platforms[0].Exec);
            // No Windows executable must have leaked into the linux variant.
            Assert.DoesNotContain(d.Platforms, p => p.Os == "windows");
        }

        // ---- BUG-027: BaseUrl in descriptor must always have a trailing slash ----

        [Theory]
        [InlineData("https://cdn.example.com/game")]    // no trailing slash → must gain one
        [InlineData("https://cdn.example.com/game/")]   // already has slash → idempotent
        public async Task Init_BaseUrl_NormalizedWithTrailingSlashInDescriptor(string rawUrl)
        {
            WriteElf("game");

            ScriptedPrompt script = new ScriptedPrompt(
                asks: new[]
                {
                    "mygame", "My Game", "1.2.3", "Acme", "A fun game", "",
                    rawUrl,
                    "",          // windows exec (none)
                    "",          // macos exec (none)
                    "",          // launcher name (→ default)
                    "",          // icon (none)
                    _out
                },
                confirms: new[]
                {
                    true,   // use 'game' as linux executable
                    true,   // create launcher
                    true,   // sandbox
                    true,   // internet
                    true    // generate key
                },
                chooses: new[] { 1 });

            int code = await InitCommand.RunAsync(
                new[] { "init", "--input", _payload }, script, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, code);

            AppDescriptor d = DescriptorParser.Parse(
                await File.ReadAllTextAsync(Path.Combine(_payload, "hina.app.json")));

            // Descriptor BaseUrl must always end with exactly one slash.
            Assert.EndsWith("/", d.BaseUrl, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Init_BaseUrl_WithoutSlash_DescriptorAndManifestBaseUrlConsistent()
        {
            // Verifies that a URL typed without trailing slash produces a descriptor whose
            // BaseUrl matches what BuildCommand writes into the manifest (both end with "/").
            WriteElf("game");

            const string rawUrl = "https://patch.example.com/mygame";

            ScriptedPrompt script = new ScriptedPrompt(
                asks: new[]
                {
                    "mygame", "My Game", "1.2.3", "Acme", "A fun game", "",
                    rawUrl,
                    "",          // windows exec (none)
                    "",          // macos exec (none)
                    "",          // launcher name (→ default)
                    "",          // icon (none)
                    _out
                },
                confirms: new[]
                {
                    true,   // use 'game' as linux executable
                    true,   // create launcher
                    true,   // sandbox
                    true,   // internet
                    true    // generate key
                },
                chooses: new[] { 1 });

            int code = await InitCommand.RunAsync(
                new[] { "init", "--input", _payload }, script, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, code);

            AppDescriptor d = DescriptorParser.Parse(
                await File.ReadAllTextAsync(Path.Combine(_payload, "hina.app.json")));

            var manifest = await Hina.Core.Manifest.ManifestSerializer.ReadAsync(
                Path.Combine(_out, "patch", "manifest.json"), CancellationToken.None);

            // Both descriptor and manifest must carry the normalized URL (with slash).
            string expectedUrl = rawUrl + "/";
            Assert.Equal(expectedUrl, d.BaseUrl);
            Assert.Equal(expectedUrl, manifest.BaseUrl);
        }
    }
}
