namespace Hina.PackageManager.Descriptor
{
    // Per-OS path to the app's main executable, relative to the install dir.
    public sealed class ExecMap
    {
        public string? Windows { get; set; }
        public string? Linux { get; set; }
        public string? Macos { get; set; }
    }
}
