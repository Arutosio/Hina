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

        // Canonical bytes used for descriptor signing. Strips DescriptorSignature so the signed
        // payload matches both at sign time (no signature yet) and verify time (we null it out).
        public static byte[] GetCanonicalBytes(AppDescriptor descriptor)
        {
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
                DescriptorSignature = null
            };
            return JsonSerializer.SerializeToUtf8Bytes(unsigned, PackageManagerCanonicalJsonContext.Default.AppDescriptor);
        }
    }
}
