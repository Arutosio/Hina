using System.IO;
using System.Text.Json;

namespace Hina.Core.Configuration
{
    // Loads configuration from JSON file.
    public static class PatcherConfigLoader
    {
        public static PatcherConfig Load(string path)
        {
            string json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            PatcherConfig? config = JsonSerializer.Deserialize<PatcherConfig>(json, options);
            return config ?? new PatcherConfig();
        }
    }
}
