using System;
using System.Collections.Generic;

namespace Hina.Core.Manifest
{
    // Root manifest describing a release.
    public sealed class Manifest
    {
        public string Version { get; set; } = "0.0.0";
        public DateTimeOffset BuildId { get; set; } = DateTimeOffset.UtcNow;
        public string BaseUrl { get; set; } = string.Empty;
        public List<ManifestFile> Files { get; set; } = new List<ManifestFile>();
        public ManifestSignature? Signature { get; set; }
    }
}
