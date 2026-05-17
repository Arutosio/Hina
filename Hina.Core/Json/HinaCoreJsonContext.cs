using System.Text.Json.Serialization;
using Hina.Core.Configuration;
using Hina.Core.Manifest;
using Hina.Core.Patching;

namespace Hina.Core.Json
{
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(Manifest.Manifest))]
    [JsonSerializable(typeof(ManifestFile))]
    [JsonSerializable(typeof(ManifestChunk))]
    [JsonSerializable(typeof(ManifestSignature))]
    [JsonSerializable(typeof(PatcherConfig))]
    [JsonSerializable(typeof(PatchJournal))]
    [JsonSerializable(typeof(PatchJournalEntry))]
    public partial class HinaCoreJsonContext : JsonSerializerContext { }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Manifest.Manifest))]
    [JsonSerializable(typeof(PatcherConfig))]
    [JsonSerializable(typeof(PatchJournal))]
    public partial class HinaCoreIndentedJsonContext : JsonSerializerContext { }

    [JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Manifest.Manifest))]
    public partial class HinaCoreCanonicalJsonContext : JsonSerializerContext { }
}
