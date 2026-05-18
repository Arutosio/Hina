using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hina.PackageManager.Descriptor
{
    // Whitelisted declarative actions a publisher can request at install/uninstall time.
    // Polymorphic on the "action" JSON discriminator.
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
    [JsonDerivedType(typeof(AddToPathHook), "addToPath")]
    [JsonDerivedType(typeof(MimeTypeHook), "registerMimeType")]
    [JsonDerivedType(typeof(UrlSchemeHook), "registerUrlScheme")]
    [JsonDerivedType(typeof(InstallFontHook), "installFont")]
    [JsonDerivedType(typeof(AutostartHook), "registerAutostart")]
    public abstract class HookAction
    {
    }

    public sealed class AddToPathHook : HookAction
    {
        public string Name { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
    }

    public sealed class MimeTypeHook : HookAction
    {
        public string MimeType { get; set; } = string.Empty;
        public List<string> Extensions { get; set; } = new List<string>();
        public string EntryId { get; set; } = string.Empty;
    }

    public sealed class UrlSchemeHook : HookAction
    {
        public string Scheme { get; set; } = string.Empty;
        public string EntryId { get; set; } = string.Empty;
    }

    public sealed class InstallFontHook : HookAction
    {
        public List<string> Files { get; set; } = new List<string>();
    }

    public sealed class AutostartHook : HookAction
    {
        public string EntryId { get; set; } = string.Empty;
        public List<string>? Args { get; set; }
    }
}
