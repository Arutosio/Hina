using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform.Linux;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // Field codes (%f / %u) and arguments in generated .desktop files must follow the
    // freedesktop Exec quoting rules: field codes sit OUTSIDE the quoted executable, and
    // each argument is quoted independently. A field code wrapped inside the quotes is
    // literal text and never gets the file/URL substituted, so the handler silently breaks.
    public class LinuxDesktopExecTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly LinuxPlatformIntegration _platform;

        public LinuxDesktopExecTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-exec-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
            _platform = new LinuxPlatformIntegration(
                Path.Combine(_tempDir, "bin"),
                Path.Combine(_tempDir, "apps"),
                Path.Combine(_tempDir, "fonts"),
                Path.Combine(_tempDir, "autostart"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        private static async Task<string> ExecLine(string path)
        {
            string[] lines = await File.ReadAllLinesAsync(path);
            return lines.Single(l => l.StartsWith("Exec=", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RegisterMimeType_SpacedExec_FieldCodeOutsideQuotes()
        {
            string exec = "/home/user/my apps/bin/viewer";
            MimeTypeHook hook = new MimeTypeHook { MimeType = "image/png", EntryId = "main", Extensions = { "png" } };
            string path = await _platform.RegisterMimeType(hook, _tempDir, exec, CancellationToken.None);

            string line = await ExecLine(path);
            // Exec value: quoted exec, then a bare %f.
            Assert.Equal($"Exec=\"{exec}\" %f", line);
            // The field code must NOT be inside the quotes.
            Assert.DoesNotContain("%f\"", line);
        }

        [Fact]
        public async Task RegisterUrlScheme_SpacedExec_FieldCodeOutsideQuotes()
        {
            string exec = "/opt/my app/bin/handler";
            UrlSchemeHook hook = new UrlSchemeHook { Scheme = "myapp", EntryId = "main" };
            string path = await _platform.RegisterUrlScheme(hook, _tempDir, exec, CancellationToken.None);

            string line = await ExecLine(path);
            Assert.Equal($"Exec=\"{exec}\" %u", line);
            Assert.DoesNotContain("%u\"", line);
        }

        [Fact]
        public async Task RegisterAutostart_ArgsWithSpaces_EachQuotedIndependently()
        {
            string exec = "/opt/app/bin/daemon";
            AutostartHook hook = new AutostartHook
            {
                EntryId = "main",
                Args = new() { "--config=/etc/my app/cfg.yaml", "--name=My App" }
            };
            string path = await _platform.RegisterAutostart(hook, _tempDir, exec, CancellationToken.None);

            string line = await ExecLine(path);
            // Exec unquoted (no special chars), each spaced arg quoted on its own.
            Assert.Equal($"Exec={exec} \"--config=/etc/my app/cfg.yaml\" \"--name=My App\"", line);
        }

        [Fact]
        public async Task RegisterMimeType_ExecWithDollar_EscapedInsideQuotes()
        {
            string exec = "/opt/$pecial/bin/app";
            MimeTypeHook hook = new MimeTypeHook { MimeType = "text/plain", EntryId = "main", Extensions = { "txt" } };
            string path = await _platform.RegisterMimeType(hook, _tempDir, exec, CancellationToken.None);

            string line = await ExecLine(path);
            // $ must be backslash-escaped inside the quotes per the spec.
            Assert.Equal("Exec=\"/opt/\\$pecial/bin/app\" %f", line);
        }
    }
}
