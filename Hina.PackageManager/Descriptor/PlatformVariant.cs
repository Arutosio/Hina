namespace Hina.PackageManager.Descriptor
{
    // One per-platform build of the app. A descriptor that lists `platforms` ships each OS/arch
    // build as its own manifest (manifest.<os>[-<arch>].json) over a shared chunk store, so a
    // client downloads ONLY the variant matching its machine instead of every platform's files.
    //
    // Os is required; Arch is optional — a variant with no Arch is the universal build for that
    // OS. The client picks the most specific match (see PlatformSelector). Exec is the path to
    // the executable, relative to THIS variant's root.
    public sealed class PlatformVariant
    {
        // "windows" | "macos" | "linux".
        public string Os { get; set; } = string.Empty;

        // "x64" | "arm64" | "x86" | "arm", or null for "any architecture on this OS".
        public string? Arch { get; set; }

        // Executable path relative to the variant's install root.
        public string Exec { get; set; } = string.Empty;
    }
}
