using System.Text.Json.Serialization;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Json
{
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(AppDescriptor))]
    [JsonSerializable(typeof(HookAction))]
    [JsonSerializable(typeof(AddToPathHook))]
    [JsonSerializable(typeof(MimeTypeHook))]
    [JsonSerializable(typeof(UrlSchemeHook))]
    [JsonSerializable(typeof(InstallFontHook))]
    [JsonSerializable(typeof(AutostartHook))]
    [JsonSerializable(typeof(ShellEntry))]
    [JsonSerializable(typeof(ExecMap))]
    [JsonSerializable(typeof(DescriptorSignature))]
    [JsonSerializable(typeof(Registry.Registry))]
    [JsonSerializable(typeof(Registry.InstalledApp))]
    [JsonSerializable(typeof(Registry.HookEvidence))]
    [JsonSerializable(typeof(Registry.ShellEntryRecord))]
    public partial class PackageManagerJsonContext : JsonSerializerContext { }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(AppDescriptor))]
    [JsonSerializable(typeof(Registry.Registry))]
    public partial class PackageManagerIndentedJsonContext : JsonSerializerContext { }

    [JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(AppDescriptor))]
    public partial class PackageManagerCanonicalJsonContext : JsonSerializerContext { }
}
