using System;
using System.IO;
using System.Text;
using Hina.PackageManager.Descriptor;

namespace Hina.Builder.Init
{
    // Pre-filled answers shown as [default] at each prompt. Built by merging, in priority
    // order: an existing hina.app.json (re-run = edit) > project-file metadata > folder-name
    // fallback. Null means "no good default; the wizard will ask without one".
    internal sealed class ScaffoldDefaults
    {
        public string Name { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Version { get; init; } = "1.0.0";
        public string Publisher { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? Homepage { get; init; }
        public string BaseUrl { get; init; } = string.Empty;
        public string Channel { get; init; } = "stable";

        // The full existing descriptor, when re-running over a hina.app.json. Lets the wizard
        // reuse entries/sandbox/publicKey as defaults too.
        public AppDescriptor? Existing { get; init; }
    }

    internal static class DefaultsResolver
    {
        public const string DescriptorFileName = "hina.app.json";

        public static ScaffoldDefaults Resolve(DirectoryInfo root)
        {
            AppDescriptor? existing = TryLoadExisting(root);
            ProjectMetadata meta = ProjectMetadataReader.Read(root);

            string folderSlug = Slugify(root.Name);

            string name = First(existing?.Name, Slugify(meta.Name), folderSlug);
            string displayName = First(existing?.DisplayName, meta.Name, root.Name);
            string version = First(existing?.Version, NormalizeVersion(meta.Version), "1.0.0");
            string publisher = First(existing?.Publisher, meta.Publisher, "");
            string description = First(existing?.Description, meta.Description, "");
            string? homepage = FirstOrNull(existing?.Homepage, meta.Homepage);
            string baseUrl = First(existing?.BaseUrl, "");
            string channel = First(existing?.Channel, "stable");

            return new ScaffoldDefaults
            {
                Name = name,
                DisplayName = displayName,
                Version = version,
                Publisher = publisher,
                Description = description,
                Homepage = homepage,
                BaseUrl = baseUrl,
                Channel = channel,
                Existing = existing
            };
        }

        private static AppDescriptor? TryLoadExisting(DirectoryInfo root)
        {
            string path = Path.Combine(root.FullName, DescriptorFileName);
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                return DescriptorParser.Parse(File.ReadAllText(path));
            }
            catch (Exception)
            {
                // Corrupt/partial existing descriptor: ignore it as a source of defaults rather
                // than failing the whole wizard.
                return null;
            }
        }

        private static string First(params string?[] candidates)
        {
            foreach (string? c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c))
                {
                    return c!.Trim();
                }
            }
            return string.Empty;
        }

        private static string? FirstOrNull(params string?[] candidates)
        {
            foreach (string? c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c))
                {
                    return c!.Trim();
                }
            }
            return null;
        }

        // Lowercase, spaces/underscores → '-', drop anything outside [a-z0-9-], collapse and trim
        // dashes, ensure it starts with a letter so it satisfies the name regex
        // (^[a-z][a-z0-9-]{1,63}$). Returns "" if nothing usable (the wizard then asks).
        public static string Slugify(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder(raw.Length);
            foreach (char ch in raw.Trim().ToLowerInvariant())
            {
                if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
                {
                    sb.Append(ch);
                }
                else if (ch == ' ' || ch == '_' || ch == '-' || ch == '.')
                {
                    sb.Append('-');
                }
            }
            string s = sb.ToString().Trim('-');
            while (s.Contains("--"))
            {
                s = s.Replace("--", "-");
            }
            if (s.Length == 0)
            {
                return string.Empty;
            }
            if (s[0] is < 'a' or > 'z')
            {
                s = "app-" + s;
            }
            return s.Length > 64 ? s.Substring(0, 64).Trim('-') : s;
        }

        // Coerce a metadata version into SemVer-ish (x.y.z). "1.0" → "1.0.0", "1" → "1.0.0".
        // Returns null when it can't, so the next default ("1.0.0") applies.
        private static string? NormalizeVersion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }
            string v = raw.Trim();
            string core = v;
            int sep = v.IndexOfAny(new[] { '-', '+' });
            if (sep >= 0)
            {
                core = v.Substring(0, sep);
            }
            string[] parts = core.Split('.');
            foreach (string p in parts)
            {
                if (!int.TryParse(p, out _))
                {
                    return null;
                }
            }
            return parts.Length switch
            {
                1 => $"{parts[0]}.0.0",
                2 => $"{parts[0]}.{parts[1]}.0",
                _ => v
            };
        }
    }
}
