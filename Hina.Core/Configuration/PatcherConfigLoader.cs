using System.IO;
using System.Text.Json;
using Hina.Core.Json;

namespace Hina.Core.Configuration
{
    // Loads configuration from JSON file.
    public static class PatcherConfigLoader
    {
        public static PatcherConfig Load(string path)
        {
            string json = File.ReadAllText(path);
            try
            {
                PatcherConfig? config = JsonSerializer.Deserialize(json, HinaCoreJsonContext.Default.PatcherConfig);
                return config ?? new PatcherConfig();
            }
            catch (JsonException ex)
            {
                // Name the file: the caller may be reading an explicit --config or the
                // implicit ./hina.config.json — a raw parser error doesn't say which.
                throw new InvalidDataException(
                    $"Config file '{path}' is not valid JSON: {ex.Message}", ex);
            }
        }
    }
}
