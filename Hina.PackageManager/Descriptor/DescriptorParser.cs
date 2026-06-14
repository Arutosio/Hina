using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Json;

namespace Hina.PackageManager.Descriptor
{
    // Wraps source-gen JSON for AppDescriptor. Single entry point for parse / serialize / canonicalize.
    public static class DescriptorParser
    {
        public static AppDescriptor Parse(string json)
        {
            try
            {
                AppDescriptor? descriptor = JsonSerializer.Deserialize(json, PackageManagerJsonContext.Default.AppDescriptor);
                if (descriptor == null)
                {
                    throw new InvalidDataException("Descriptor JSON parsed to null; this is not a valid Hina app descriptor.");
                }
                return descriptor;
            }
            catch (JsonException ex)
            {
                // The most common way here is a URL that serves a web page / captive portal /
                // bucket listing instead of hina.app.json. Surface that, not parser internals.
                throw new InvalidDataException(
                    $"Content is not a valid Hina app descriptor (hina.app.json): {ex.Message}", ex);
            }
        }

        public static AppDescriptor Parse(byte[] utf8Json)
        {
            return Parse(Encoding.UTF8.GetString(utf8Json));
        }

        public static async Task<AppDescriptor> ReadAsync(Stream stream, CancellationToken ct)
        {
            try
            {
                AppDescriptor? descriptor = await JsonSerializer.DeserializeAsync(stream, PackageManagerJsonContext.Default.AppDescriptor, ct);
                if (descriptor == null)
                {
                    throw new InvalidDataException("Descriptor JSON parsed to null; this is not a valid Hina app descriptor.");
                }
                return descriptor;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Content is not a valid Hina app descriptor (hina.app.json): {ex.Message}", ex);
            }
        }

        public static string Serialize(AppDescriptor descriptor, bool indented = true)
        {
            if (indented)
            {
                return JsonSerializer.Serialize(descriptor, PackageManagerIndentedJsonContext.Default.AppDescriptor);
            }
            return JsonSerializer.Serialize(descriptor, PackageManagerJsonContext.Default.AppDescriptor);
        }

        // Canonical bytes used for descriptor signing. Uses a strip-only approach: shallow-clone
        // the descriptor and null out only DescriptorSignature before serializing with the
        // canonical context (non-indented, WhenWritingNull). This ensures every field — including
        // Sandbox and any fields added in the future — is covered by the Ed25519 signature without
        // requiring a hand-maintained allowlist that can silently omit new fields (as it did with
        // Sandbox, which is what BUG-001 exploited). The shallow clone avoids mutating the shared
        // descriptor object, keeping this method safe to call from multiple threads.
        public static byte[] GetCanonicalBytes(AppDescriptor descriptor)
        {
            // Shallow copy: all fields share references with the original, which is safe here
            // because we only change DescriptorSignature on the clone and never write it back.
            AppDescriptor unsigned = new AppDescriptor
            {
                SchemaVersion = descriptor.SchemaVersion,
                Name = descriptor.Name,
                DisplayName = descriptor.DisplayName,
                Version = descriptor.Version,
                Publisher = descriptor.Publisher,
                Description = descriptor.Description,
                Homepage = descriptor.Homepage,
                License = descriptor.License,
                Icon = descriptor.Icon,
                MinHinaVersion = descriptor.MinHinaVersion,
                BaseUrl = descriptor.BaseUrl,
                Channel = descriptor.Channel,
                PublicKey = descriptor.PublicKey,
                Exec = descriptor.Exec,
                Platforms = descriptor.Platforms,
                Entries = descriptor.Entries,
                PostInstall = descriptor.PostInstall,
                Sandbox = descriptor.Sandbox,
                DescriptorSignature = null
            };
            return JsonSerializer.SerializeToUtf8Bytes(unsigned, PackageManagerCanonicalJsonContext.Default.AppDescriptor);
        }
    }
}
