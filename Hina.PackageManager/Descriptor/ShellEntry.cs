using System.Collections.Generic;

namespace Hina.PackageManager.Descriptor
{
    // A single shell-visible entry (Start Menu item, .desktop file, macOS bundle alias).
    public sealed class ShellEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Exec { get; set; } = string.Empty;
        public string? Icon { get; set; }
        public List<string> Categories { get; set; } = new List<string>();
        public bool Terminal { get; set; }
    }
}
