using System.Text.Json;

namespace Hina.Host
{
    // First-run interactive setup: writes hina.host.json. The caller decides whether a
    // terminal is attached; this class only owns the config-file logic and the prompts.
    internal static class SetupWizard
    {
        // True when the wizard should be offered: no config yet, an empty JSON object, or
        // an unparseable file.
        public static bool IsConfigMissingOrEmpty(string path)
        {
            if (!File.Exists(path)) return true;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;
                int props = 0;
                foreach (var _ in doc.RootElement.EnumerateObject()) { props++; break; }
                return props == 0;
            }
            catch { return true; }
        }

        public static bool Run(string outPath, bool force)
        {
            Console.WriteLine();
            Console.WriteLine("=== Hina.Host first-run setup ===");
            if (File.Exists(outPath) && force)
            {
                Console.Write($"'{outPath}' already exists. Overwrite? [y/N]: ");
                var confirm = Console.ReadLine();
                if (!string.Equals(confirm?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (!File.Exists(outPath))
            {
                Console.WriteLine($"No config found at '{outPath}'. Let's create one.");
            }
            else
            {
                Console.WriteLine($"Config '{outPath}' looks empty. Let's populate it.");
            }

            string port = AskPort("Listen port (default 49876 is in the dynamic/private range)", "49876");
            string bindAll = Ask("Bind on all interfaces (0.0.0.0)? [Y/n]", "y").Trim().ToLowerInvariant();
            string host = bindAll is "" or "y" or "yes" ? "0.0.0.0" : "127.0.0.1";
            string urls = $"http://{host}:{port}";

            string mode = Ask("Mode: [s]ingle-app or [m]ulti-app?", "s").Trim().ToLowerInvariant();
            var json = new Dictionary<string, object> { ["urls"] = urls };

            if (mode.StartsWith("m"))
            {
                var apps = new Dictionary<string, string>();
                Console.WriteLine("Add apps. Leave app name blank to finish.");
                while (true)
                {
                    string name = Ask("  App name (blank to stop)", "").Trim();
                    if (string.IsNullOrEmpty(name)) break;
                    string path = Ask($"  Path for '{name}'", $"./patches/{name}").Trim();
                    apps[name] = path;
                }
                if (apps.Count == 0)
                {
                    Console.WriteLine("No apps defined; falling back to single-app mode.");
                    json["root"] = Ask("Patch root directory", "patch").Trim();
                }
                else
                {
                    json["apps"] = apps;
                }
            }
            else
            {
                json["root"] = Ask("Patch root directory", "patch").Trim();
            }

            if (int.TryParse(Ask("Max requests/min per (IP,App)", "600"), out int rl) && rl > 0)
                json["requestsPerMinutePerIp"] = rl;
            if (int.TryParse(Ask("Abuse warning threshold/min", "300"), out int at) && at > 0)
                json["abuseThresholdPerMinute"] = at;

            string cors = Ask("CORS origins (comma-separated, blank for none)", "").Trim();
            if (!string.IsNullOrEmpty(cors))
                json["cors"] = cors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            string serialized = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outPath, serialized);
            Console.WriteLine();
            Console.WriteLine($"Wrote {Path.GetFullPath(outPath)}:");
            Console.WriteLine(serialized);
            Console.WriteLine("=== Setup complete. Starting host... ===");
            Console.WriteLine();
            return true;
        }

        // A typo'd port would be persisted into hina.host.json and crash the host on every
        // start — and the wizard won't re-run over a non-empty config. Reject it up front.
        internal static bool IsValidPort(string value)
            => int.TryParse(value.Trim(), out int p) && p >= 1 && p <= 65535;

        static string AskPort(string prompt, string def)
        {
            while (true)
            {
                string value = Ask(prompt, def).Trim();
                if (IsValidPort(value)) return value;
                Console.WriteLine($"  '{value}' is not a valid port (1-65535). Try again, or press Enter for {def}.");
            }
        }

        static string Ask(string prompt, string def)
        {
            Console.Write(string.IsNullOrEmpty(def) ? $"{prompt}: " : $"{prompt} [{def}]: ");
            string? line = Console.ReadLine();
            return string.IsNullOrWhiteSpace(line) ? def : line;
        }
    }
}
