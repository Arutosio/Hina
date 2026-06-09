using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hina.Builder.Init
{
    // Best-effort metadata harvested from common project files, used to pre-fill the wizard's
    // defaults so the publisher mostly presses Enter. Every field is optional; any parse
    // failure fails soft to null (the wizard just won't have that default).
    internal sealed class ProjectMetadata
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Publisher { get; set; }
        public string? Description { get; set; }
        public string? Homepage { get; set; }

        public bool IsEmpty => Name == null && Version == null && Publisher == null
            && Description == null && Homepage == null;
    }

    internal static class ProjectMetadataReader
    {
        public static ProjectMetadata Read(DirectoryInfo root)
        {
            ProjectMetadata m = new ProjectMetadata();
            if (!root.Exists)
            {
                return m;
            }

            // Order = priority. First non-null wins per field (Fill never overwrites).
            TryCsproj(root, m);
            TryPackageJson(root, m);
            TryUnity(root, m);
            TryGodot(root, m);
            return m;
        }

        private static void Fill(ref string? field, string? value)
        {
            if (field == null && !string.IsNullOrWhiteSpace(value))
            {
                field = value!.Trim();
            }
        }

        private static void TryCsproj(DirectoryInfo root, ProjectMetadata m)
        {
            FileInfo? csproj = root.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (csproj == null)
            {
                return;
            }
            try
            {
                XDocument doc = XDocument.Load(csproj.FullName);
                string? Prop(string name) => doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == name)?.Value;

                string? name = m.Name; Fill(ref name, Prop("AssemblyName") ?? Prop("Title") ?? Prop("Product")); m.Name = name;
                string? version = m.Version; Fill(ref version, Prop("Version")); m.Version = version;
                string? publisher = m.Publisher; Fill(ref publisher, Prop("Authors") ?? Prop("Company")); m.Publisher = publisher;
                string? description = m.Description; Fill(ref description, Prop("Description")); m.Description = description;
                string? homepage = m.Homepage; Fill(ref homepage, Prop("PackageProjectUrl") ?? Prop("RepositoryUrl")); m.Homepage = homepage;
            }
            catch (Exception)
            {
                // fail soft — leave whatever we already had
            }
        }

        private static void TryPackageJson(DirectoryInfo root, ProjectMetadata m)
        {
            string path = Path.Combine(root.FullName, "package.json");
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement r = doc.RootElement;
                string? Str(string prop) => r.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

                string? author = Str("author");
                if (author == null && r.TryGetProperty("author", out JsonElement ae) && ae.ValueKind == JsonValueKind.Object
                    && ae.TryGetProperty("name", out JsonElement an) && an.ValueKind == JsonValueKind.String)
                {
                    author = an.GetString();
                }

                string? name = m.Name; Fill(ref name, Str("name")); m.Name = name;
                string? version = m.Version; Fill(ref version, Str("version")); m.Version = version;
                string? publisher = m.Publisher; Fill(ref publisher, author); m.Publisher = publisher;
                string? description = m.Description; Fill(ref description, Str("description")); m.Description = description;
                string? homepage = m.Homepage; Fill(ref homepage, Str("homepage")); m.Homepage = homepage;
            }
            catch (Exception)
            {
                // fail soft
            }
        }

        private static void TryUnity(DirectoryInfo root, ProjectMetadata m)
        {
            string path = Path.Combine(root.FullName, "ProjectSettings", "ProjectSettings.asset");
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                string text = File.ReadAllText(path);
                string? name = m.Name; Fill(ref name, YamlScalar(text, "productName")); m.Name = name;
                string? publisher = m.Publisher; Fill(ref publisher, YamlScalar(text, "companyName")); m.Publisher = publisher;
                string? version = m.Version; Fill(ref version, YamlScalar(text, "bundleVersion")); m.Version = version;
            }
            catch (Exception)
            {
                // fail soft
            }
        }

        private static void TryGodot(DirectoryInfo root, ProjectMetadata m)
        {
            string path = Path.Combine(root.FullName, "project.godot");
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                string text = File.ReadAllText(path);
                string? name = m.Name; Fill(ref name, IniQuoted(text, "config/name")); m.Name = name;
                string? version = m.Version; Fill(ref version, IniQuoted(text, "config/version")); m.Version = version;
                string? description = m.Description; Fill(ref description, IniQuoted(text, "config/description")); m.Description = description;
            }
            catch (Exception)
            {
                // fail soft
            }
        }

        // `  key: value` (Unity's YAML-ish asset). Returns the trimmed scalar, unquoted.
        private static string? YamlScalar(string text, string key)
        {
            Match mm = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + @":\s*(.+?)\s*$");
            if (!mm.Success)
            {
                return null;
            }
            return mm.Groups[1].Value.Trim().Trim('"');
        }

        // `key="value"` (Godot's INI-ish project file).
        private static string? IniQuoted(string text, string key)
        {
            Match mm = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + @"\s*=\s*""(.*?)""\s*$");
            return mm.Success ? mm.Groups[1].Value : null;
        }
    }
}
