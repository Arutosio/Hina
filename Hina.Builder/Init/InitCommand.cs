using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Microsoft.Extensions.Logging;

namespace Hina.Builder.Init
{
    // `hina-builder init` — interactive wizard. Scans the app folder, asks a few questions with
    // smart defaults, then writes a signed hina.app.json plus the manifest/chunk store.
    //
    // Outputs (descriptor, keys, patch store) are written to a directory OUTSIDE the scanned
    // payload: the build manifest covers every file under --input, so writing the private key
    // or the chunk store inside it would ship them to users. Keeping outputs out of the payload
    // is a hard safety rule, not a preference.
    internal static class InitCommand
    {
        // The non-interactive (redirected-stdin) guard lives in Program for the real run; tests
        // drive this directly with a ScriptedPrompt, so it must not check the console here.
        public static async Task<int> RunAsync(string[] args, IPrompt prompt, ILogger logger, CancellationToken ct)
        {
            string inputArg = Args.GetArgValue(args, "--input") ?? ".";
            DirectoryInfo input = new DirectoryInfo(Path.GetFullPath(inputArg));
            if (!input.Exists)
            {
                logger.LogError("Folder not found: {Dir}", input.FullName);
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine($"Preparing a Hina package from: {input.FullName}");
            Console.WriteLine("Press Enter to accept the [default] shown for each question.");
            Console.WriteLine();

            IReadOnlyList<DetectedExecutable> detected = ExecutableDetector.Detect(input);
            ScaffoldDefaults defaults = DefaultsResolver.Resolve(input);

            // --- metadata ---
            string name = AskName(prompt, defaults.Name);
            string displayName = prompt.Ask("Display name", NonEmpty(defaults.DisplayName, name));
            string version = AskVersion(prompt, defaults.Version);
            string publisher = prompt.Ask("Publisher", defaults.Publisher);
            string description = prompt.Ask("Short description", defaults.Description);
            string homepage = prompt.Ask("Homepage URL (optional)", defaults.Homepage ?? "");
            string baseUrl = prompt.Ask("Patch server base URL (where you'll host the files)", defaults.BaseUrl);
            bool allowInsecure = baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

            // --- executables → exec map (+ entries when single-OS) ---
            (ExecMap exec, List<ShellEntry> entries) = CollectExecutables(prompt, detected, input, name, displayName, logger);
            if (exec.Windows == null && exec.Linux == null && exec.Macos == null)
            {
                logger.LogError("No executable was selected; cannot build a descriptor. Aborting.");
                return 1;
            }

            // --- sandbox (plain-language questions) ---
            SandboxSpec? sandbox = AskSandbox(prompt);

            // --- output dir for keys + patch (outside the payload) ---
            // The descriptor itself is written into the app folder (it's public + signed, safe to
            // ship). The signing KEY and the patch store must live OUTSIDE the scanned payload, or
            // the build manifest would bundle the private key and recurse into its own output.
            string outDir = AskOutputDir(prompt, input);
            if (IsInside(outDir, input.FullName))
            {
                logger.LogError("Keys/patch output must be OUTSIDE the app folder, or the private key and patch store would be shipped to users. Got: {Out}", outDir);
                return 2;
            }
            Directory.CreateDirectory(outDir);

            string? keyPath = FindOrGenerateKey(prompt, outDir, name, logger);
            if (keyPath == null)
            {
                logger.LogError("No signing key available; aborting (a Hina package must be signed).");
                return 1;
            }
            string publicKeyB64 = File.ReadAllText(PubKeyFor(keyPath)).Trim();

            // --- assemble + validate ---
            AppDescriptor descriptor;
            try
            {
                descriptor = DescriptorScaffolder.Build(new ScaffoldAnswers
                {
                    Name = name,
                    DisplayName = displayName,
                    Version = version,
                    Publisher = publisher,
                    Description = description,
                    Homepage = string.IsNullOrWhiteSpace(homepage) ? null : homepage,
                    BaseUrl = baseUrl,
                    Channel = defaults.Channel,
                    PublicKey = publicKeyB64,
                    Exec = exec,
                    Entries = entries,
                    Sandbox = sandbox,
                    AllowInsecure = allowInsecure
                });
            }
            catch (DescriptorValidationException ex)
            {
                logger.LogError("The answers don't form a valid descriptor:\n{Errors}", ex.Message);
                return 1;
            }

            // --- summary + confirm ---
            // Descriptor lives in the project/app folder so a re-run picks it up as defaults.
            string descriptorPath = Path.Combine(input.FullName, DefaultsResolver.DescriptorFileName);
            PrintSummary(descriptor, descriptorPath, outDir);
            if (File.Exists(descriptorPath) && !prompt.Confirm($"Overwrite existing {descriptorPath}?", true))
            {
                Console.WriteLine("Aborted; nothing written.");
                return 0;
            }

            // --- sign descriptor + write ---
            byte[] privateKey = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
            DescriptorSigner.AttachSignature(descriptor, privateKey);
            File.WriteAllText(descriptorPath, DescriptorParser.Serialize(descriptor, indented: true));
            logger.LogInformation("Wrote signed descriptor: {Path}", descriptorPath);

            // --- build manifest + chunks ---
            string patchDir = Path.Combine(outDir, "patch");
            int buildCode = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = input.FullName,
                Output = patchDir,
                BaseUrl = baseUrl,
                Version = version,
                SignKeyPath = keyPath,
                ChunkingMode = "cdc"
            }, logger, ct);
            if (buildCode != 0)
            {
                logger.LogError("Build step failed (the descriptor was written; fix the issue and re-run `build`).");
                return buildCode;
            }

            PrintNextSteps(descriptor, descriptorPath, patchDir, baseUrl);
            return 0;
        }

        // ---- metadata prompts ----

        private static string AskName(IPrompt prompt, string def)
        {
            while (true)
            {
                string raw = prompt.Ask("App id (lowercase, a-z 0-9 -)", def);
                string slug = DefaultsResolver.Slugify(raw);
                AppDescriptor probe = new AppDescriptor { Name = slug };
                bool nameOk = DescriptorValidator.Validate(probe).Errors.All(e => !e.StartsWith("name "));
                if (nameOk && slug.Length > 0)
                {
                    if (!string.Equals(slug, raw, StringComparison.Ordinal))
                    {
                        Console.WriteLine($"  → using '{slug}'");
                    }
                    return slug;
                }
                Console.WriteLine("  Invalid id. Use letters, digits and dashes; must start with a letter.");
                def = slug;
            }
        }

        private static string AskVersion(IPrompt prompt, string def)
        {
            while (true)
            {
                string v = prompt.Ask("Version (SemVer x.y.z)", def);
                AppDescriptor probe = new AppDescriptor { Version = v };
                if (DescriptorValidator.Validate(probe).Errors.All(e => !e.StartsWith("version ")))
                {
                    return v;
                }
                Console.WriteLine("  Not valid SemVer (expected x.y.z). Try again.");
                def = "1.0.0";
            }
        }

        // ---- executables ----

        private static (ExecMap, List<ShellEntry>) CollectExecutables(
            IPrompt prompt, IReadOnlyList<DetectedExecutable> detected, DirectoryInfo input,
            string name, string displayName, ILogger logger)
        {
            ExecMap exec = new ExecMap();
            List<DetectedExecutable> chosen = new List<DetectedExecutable>();

            if (detected.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Found {detected.Count} executable candidate(s):");
            }

            foreach (TargetOs os in new[] { TargetOs.Windows, TargetOs.Linux, TargetOs.Macos })
            {
                DetectedExecutable? best = detected.FirstOrDefault(d => d.Os == os);
                string label = os.ToString().ToLowerInvariant();
                if (best != null)
                {
                    if (prompt.Confirm($"Use '{best.RelPath}' as the {label} executable?", true))
                    {
                        SetExec(exec, os, best.RelPath);
                        chosen.Add(best);
                        continue;
                    }
                }
                string manual = prompt.Ask($"Path to the {label} executable (relative, blank = none)", "");
                if (!string.IsNullOrWhiteSpace(manual))
                {
                    string rel = manual.Replace('\\', '/').Trim();
                    SetExec(exec, os, rel);
                    chosen.Add(new DetectedExecutable { RelPath = rel, Os = os });
                }
            }

            // Entries (menu launchers) are not per-OS in the schema, so only create them for a
            // single-OS package. Multi-OS packages rely on the exec map and skip entries.
            List<ShellEntry> entries = new List<ShellEntry>();
            int osCount = new[] { exec.Windows, exec.Linux, exec.Macos }.Count(p => p != null);
            if (osCount == 1)
            {
                if (prompt.Confirm("Create a desktop / Start-menu launcher for this app?", true))
                {
                    string entryExec = exec.Windows ?? exec.Linux ?? exec.Macos!;
                    string entryName = prompt.Ask("Launcher name", displayName);
                    string icon = prompt.Ask("Icon path (relative, optional)", "");
                    entries.Add(new ShellEntry
                    {
                        Id = name,
                        Name = entryName,
                        Exec = entryExec,
                        Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Replace('\\', '/').Trim()
                    });
                }
            }
            else if (osCount > 1)
            {
                logger.LogInformation(
                    "Multi-OS package: per-OS menu launchers aren't supported by the descriptor schema yet, so no launcher entry was added. The app still launches via the exec map / `hina run`.");
            }

            return (exec, entries);
        }

        private static void SetExec(ExecMap exec, TargetOs os, string rel)
        {
            switch (os)
            {
                case TargetOs.Windows: exec.Windows = rel; break;
                case TargetOs.Linux: exec.Linux = rel; break;
                case TargetOs.Macos: exec.Macos = rel; break;
            }
        }

        // ---- sandbox ----

        private static SandboxSpec? AskSandbox(IPrompt prompt)
        {
            Console.WriteLine();
            if (!prompt.Confirm("Run the app in a filesystem sandbox? (recommended)", true))
            {
                return new SandboxAnswers { Enabled = false }.ToSpec();
            }

            bool network = prompt.Confirm("Does the app need internet access?", true);
            int loc = prompt.Choose(
                "Where does the app save its data?",
                new[]
                {
                    "Only inside itself (no extra access)",
                    "A config folder",
                    "The Documents folder",
                    "Anywhere on disk (full access)"
                },
                defaultIndex: 1);

            DataLocation dataLocation = loc switch
            {
                1 => DataLocation.Config,
                2 => DataLocation.Documents,
                3 => DataLocation.Anywhere,
                _ => DataLocation.SelfOnly
            };

            return new SandboxAnswers { Enabled = true, NeedsNetwork = network, DataLocation = dataLocation }.ToSpec();
        }

        // ---- output dir + keys ----

        private static string AskOutputDir(IPrompt prompt, DirectoryInfo input)
        {
            // Default to a sibling so it's never inside the scanned payload.
            string def = Path.GetFullPath(Path.Combine(input.FullName, "..", input.Name + "-hina-dist"));
            string answer = prompt.Ask("Output folder for keys + patch store (must be outside the app folder)", def);
            return Path.GetFullPath(answer);
        }

        // Looks for an existing <outDir>/keys/*.key.b64; offers to generate one if absent.
        // Returns the private-key path, or null if the user declined and none exist.
        private static string? FindOrGenerateKey(IPrompt prompt, string outDir, string name, ILogger logger)
        {
            string keysDir = Path.Combine(outDir, "keys");
            if (Directory.Exists(keysDir))
            {
                string? existing = Directory.GetFiles(keysDir, "*.key.b64").FirstOrDefault();
                if (existing != null && File.Exists(PubKeyFor(existing)))
                {
                    Console.WriteLine($"Using existing signing key: {existing}");
                    return existing;
                }
            }

            if (!prompt.Confirm("No signing key found. Generate a new Ed25519 key pair?", true))
            {
                return null;
            }
            (string priv, _) = KeygenCommand.Generate(keysDir, name, logger);
            Console.WriteLine($"  Keep the private key secret: {priv}");
            return priv;
        }

        private static string PubKeyFor(string privateKeyPath)
            => privateKeyPath.EndsWith(".key.b64", StringComparison.Ordinal)
                ? privateKeyPath[..^".key.b64".Length] + ".pub.b64"
                : privateKeyPath + ".pub.b64";

        // ---- output ----

        private static void PrintSummary(AppDescriptor d, string descriptorPath, string outDir)
        {
            Console.WriteLine();
            Console.WriteLine("Summary");
            Console.WriteLine($"  id:         {d.Name}");
            Console.WriteLine($"  name:       {d.DisplayName}");
            Console.WriteLine($"  version:    {d.Version}");
            Console.WriteLine($"  publisher:  {d.Publisher}");
            Console.WriteLine($"  baseUrl:    {d.BaseUrl}");
            Console.WriteLine($"  exec:       win={d.Exec.Windows ?? "-"}  linux={d.Exec.Linux ?? "-"}  macos={d.Exec.Macos ?? "-"}");
            Console.WriteLine($"  launchers:  {d.Entries.Count}");
            Console.WriteLine($"  sandbox:    {(d.Sandbox?.Enabled == true ? "on" : "off")}");
            Console.WriteLine($"  descriptor: {descriptorPath}");
            Console.WriteLine($"  keys+patch: {outDir}");
            Console.WriteLine();
        }

        private static void PrintNextSteps(AppDescriptor d, string descriptorPath, string patchDir, string baseUrl)
        {
            Console.WriteLine();
            Console.WriteLine("Done. Next steps:");
            Console.WriteLine($"  1. Upload the patch store to your server so it's reachable at: {baseUrl}");
            Console.WriteLine($"     (contents of: {patchDir})");
            Console.WriteLine($"  2. Host the descriptor and share its URL, e.g. {baseUrl.TrimEnd('/')}/hina.app.json");
            Console.WriteLine($"     (file: {descriptorPath})");
            Console.WriteLine($"  3. Users install with:  hina install <url-to-hina.app.json>");
            Console.WriteLine();
        }

        // ---- helpers ----

        private static string NonEmpty(string preferred, string fallback)
            => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

        private static bool IsInside(string child, string parent)
        {
            string c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return c.StartsWith(p, StringComparison.Ordinal);
        }
    }
}
