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
            PatcherConfig? config = JsonSerializer.Deserialize(json, HinaCoreJsonContext.Default.PatcherConfig);
            return config ?? new PatcherConfig();
        }
    }
}
