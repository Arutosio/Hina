using System;
using System.Collections.Generic;

namespace Hina.PackageManager.Descriptor
{
    // The closed set of abstract path tokens a sandbox FsRule may name. Kept here
    // (next to the wire model) so both DescriptorValidator and the runtime path
    // resolver agree on what's legal. Unknown tokens are rejected at validation —
    // fail closed, never silently grant.
    public static class SandboxTokens
    {
        // App install directory. Always implicitly granted (ro+exec); listing it is harmless.
        public const string App = "app";
        public const string Home = "home";
        public const string XdgDocuments = "xdg-documents";
        public const string XdgDownload = "xdg-download";
        public const string XdgConfig = "xdg-config";
        public const string Tmp = "tmp";
        // Escape hatch: no filesystem restriction. Must be explicit and surfaced loudly.
        public const string Host = "host";

        public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
        {
            App, Home, XdgDocuments, XdgDownload, XdgConfig, Tmp, Host,
        };

        public static bool IsKnown(string token) => Known.Contains(token);
    }
}
