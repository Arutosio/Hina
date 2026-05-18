using System;
using System.Reflection;

namespace Hina.PackageManager.Install
{
    // Single source of truth for "what version of Hina is the user running".
    // Used by InstallService / UpdateService to enforce descriptor.minHinaVersion
    // so a publisher requiring features from a newer release can't trick an older
    // Hina into installing an app it can't actually drive.
    public static class HinaVersion
    {
        // Bumped manually with each feature release. Source-gen / AOT friendly.
        public const string Current = "0.1.0";

        // True if running version >= required minimum. Both sides parsed as SemVer
        // major.minor.patch; pre-release / build-metadata suffix is tolerated but
        // not compared (an alpha against an alpha of the same MMP compares equal).
        public static bool IsSatisfiedBy(string minVersion)
        {
            if (string.IsNullOrWhiteSpace(minVersion)) return true;
            if (!TryParse(minVersion, out (int Major, int Minor, int Patch) min)) return true;
            if (!TryParse(Current, out (int Major, int Minor, int Patch) cur)) return false;

            if (cur.Major != min.Major) return cur.Major > min.Major;
            if (cur.Minor != min.Minor) return cur.Minor > min.Minor;
            return cur.Patch >= min.Patch;
        }

        private static bool TryParse(string value, out (int, int, int) parsed)
        {
            parsed = default;
            // Strip pre-release / build-metadata tail.
            int cut = value.IndexOfAny(new[] { '-', '+' });
            string core = cut >= 0 ? value.Substring(0, cut) : value;
            string[] parts = core.Split('.');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out int major)) return false;
            if (!int.TryParse(parts[1], out int minor)) return false;
            if (!int.TryParse(parts[2], out int patch)) return false;
            parsed = (major, minor, patch);
            return true;
        }
    }
}
