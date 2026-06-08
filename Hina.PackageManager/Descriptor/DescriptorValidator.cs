using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Hina.PackageManager.Descriptor
{
    // Validates an AppDescriptor against the wire-format invariants
    // documented in docs/PackageManager-Guide.md.
    public static class DescriptorValidator
    {
        public const int CurrentSchemaVersion = 1;

        private static readonly Regex NameRegex = new Regex(@"^[a-z][a-z0-9-]{1,63}$", RegexOptions.Compiled);
        private static readonly Regex SemVerRegex = new Regex(@"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z\.-]+)?$", RegexOptions.Compiled);
        // Channel becomes part of the manifest URL path (manifest.<channel>.json); constrain it
        // to a safe filename token so it can't inject path segments ("../") or URL controls.
        private static readonly Regex ChannelRegex = new Regex(@"^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
        // These fields are written verbatim into shell-integration artifacts (Linux .desktop keys,
        // macOS plist, Windows registry). Constrain them to safe charsets so an attacker-controlled
        // (but validly signed) descriptor can't inject extra .desktop keys / an autostart Exec= line.
        private static readonly Regex MimeTypeRegex = new Regex(@"^[a-zA-Z0-9][a-zA-Z0-9!#$&^_.+-]*/[a-zA-Z0-9][a-zA-Z0-9!#$&^_.+-]*$", RegexOptions.Compiled);
        private static readonly Regex SchemeRegex = new Regex(@"^[a-z][a-z0-9+.-]{0,63}$", RegexOptions.Compiled);
        private static readonly Regex ExtensionRegex = new Regex(@"^\.?[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
        private static readonly Regex CategoryRegex = new Regex(@"^[A-Za-z0-9-]{1,64}$", RegexOptions.Compiled);
        // entries[].id is written into the .desktop filename (SanitizeId) and, for sandboxed apps,
        // into the .desktop Exec= line via the `hina run <app> <id>` launchOverride. Constrain it to
        // a safe token so a signed-but-hostile descriptor can't inject metacharacters / path separators.
        private static readonly Regex EntryIdRegex = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

        public static ValidationResult Validate(AppDescriptor descriptor, ValidationContext? ctx = null)
        {
            ctx ??= ValidationContext.Default;
            List<string> errors = new List<string>();

            if (descriptor.SchemaVersion != CurrentSchemaVersion)
            {
                errors.Add($"schemaVersion {descriptor.SchemaVersion} is not supported (expected {CurrentSchemaVersion}).");
            }

            if (!NameRegex.IsMatch(descriptor.Name))
            {
                errors.Add($"name '{descriptor.Name}' must match {NameRegex}.");
            }

            if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            {
                errors.Add("displayName is required.");
            }
            else if (HasControlChars(descriptor.DisplayName))
            {
                errors.Add("displayName must not contain control characters.");
            }

            if (!SemVerRegex.IsMatch(descriptor.Version))
            {
                errors.Add($"version '{descriptor.Version}' is not valid SemVer.");
            }

            if (string.IsNullOrWhiteSpace(descriptor.Publisher))
            {
                errors.Add("publisher is required.");
            }
            else if (HasControlChars(descriptor.Publisher))
            {
                errors.Add("publisher must not contain control characters.");
            }

            if (string.IsNullOrEmpty(descriptor.Channel) || !ChannelRegex.IsMatch(descriptor.Channel))
            {
                errors.Add($"channel '{descriptor.Channel}' must match {ChannelRegex} (it becomes part of the manifest URL).");
            }

            ValidateUrl(descriptor.BaseUrl, "baseUrl", ctx.AllowInsecure, errors);

            if (descriptor.Homepage != null)
            {
                ValidateUrl(descriptor.Homepage, "homepage", allowInsecure: true, errors);
            }

            ValidateEd25519Key(descriptor.PublicKey, "publicKey", errors);

            if (descriptor.Exec.Windows == null && descriptor.Exec.Linux == null && descriptor.Exec.Macos == null)
            {
                errors.Add("exec must define at least one platform.");
            }
            ValidateRelativePath(descriptor.Exec.Windows, "exec.windows", errors);
            ValidateRelativePath(descriptor.Exec.Linux, "exec.linux", errors);
            ValidateRelativePath(descriptor.Exec.Macos, "exec.macos", errors);

            HashSet<string> entryIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < descriptor.Entries.Count; i++)
            {
                ShellEntry e = descriptor.Entries[i];
                string prefix = $"entries[{i}]";

                if (string.IsNullOrWhiteSpace(e.Id))
                {
                    errors.Add($"{prefix}.id is required.");
                }
                else if (!EntryIdRegex.IsMatch(e.Id))
                {
                    errors.Add($"{prefix}.id '{e.Id}' must match {EntryIdRegex}.");
                }
                else if (!entryIds.Add(e.Id))
                {
                    errors.Add($"{prefix}.id '{e.Id}' is duplicated.");
                }

                if (string.IsNullOrWhiteSpace(e.Name))
                {
                    errors.Add($"{prefix}.name is required.");
                }
                else if (HasControlChars(e.Name))
                {
                    errors.Add($"{prefix}.name must not contain control characters.");
                }

                foreach (string cat in e.Categories)
                {
                    if (!CategoryRegex.IsMatch(cat))
                    {
                        errors.Add($"{prefix}.categories entry '{cat}' must match {CategoryRegex}.");
                    }
                }

                ValidateRelativePath(e.Exec, $"{prefix}.exec", errors);
                ValidateRelativePath(e.Icon, $"{prefix}.icon", errors);
            }

            for (int i = 0; i < descriptor.PostInstall.Count; i++)
            {
                ValidateHook(descriptor.PostInstall[i], i, entryIds, errors);
            }

            ValidateRelativePath(descriptor.Icon, "icon", errors);

            if (descriptor.MinHinaVersion != null && !SemVerRegex.IsMatch(descriptor.MinHinaVersion))
            {
                errors.Add($"minHinaVersion '{descriptor.MinHinaVersion}' is not valid SemVer.");
            }

            ValidateSandbox(descriptor.Sandbox, errors);

            return new ValidationResult(errors);
        }

        private static void ValidateSandbox(SandboxSpec? sandbox, List<string> errors)
        {
            if (sandbox == null)
            {
                return;
            }

            for (int i = 0; i < sandbox.Filesystem.Count; i++)
            {
                FsRule rule = sandbox.Filesystem[i];
                string prefix = $"sandbox.filesystem[{i}]";

                if (!SandboxTokens.IsKnown(rule.Path))
                {
                    errors.Add($"{prefix}.path '{rule.Path}' is not a known sandbox path token.");
                }

                if (rule.Access != "ro" && rule.Access != "rw")
                {
                    errors.Add($"{prefix}.access '{rule.Access}' must be 'ro' or 'rw'.");
                }
            }
        }

        private static void ValidateHook(HookAction hook, int index, HashSet<string> entryIds, List<string> errors)
        {
            string prefix = $"postInstall[{index}]";

            switch (hook)
            {
                case AddToPathHook a:
                    if (string.IsNullOrWhiteSpace(a.Name)) errors.Add($"{prefix}.name is required.");
                    ValidateRelativePath(a.Target, $"{prefix}.target", errors);
                    break;
                case MimeTypeHook m:
                    if (string.IsNullOrWhiteSpace(m.MimeType)) errors.Add($"{prefix}.mimeType is required.");
                    else if (!MimeTypeRegex.IsMatch(m.MimeType)) errors.Add($"{prefix}.mimeType '{m.MimeType}' must be a valid type/subtype token.");
                    if (m.Extensions.Count == 0) errors.Add($"{prefix}.extensions must not be empty.");
                    foreach (string ext in m.Extensions)
                    {
                        if (!ExtensionRegex.IsMatch(ext)) errors.Add($"{prefix}.extensions entry '{ext}' must match {ExtensionRegex}.");
                    }
                    ValidateEntryId(m.EntryId, entryIds, $"{prefix}.entryId", errors);
                    break;
                case UrlSchemeHook u:
                    if (string.IsNullOrWhiteSpace(u.Scheme)) errors.Add($"{prefix}.scheme is required.");
                    else if (!SchemeRegex.IsMatch(u.Scheme)) errors.Add($"{prefix}.scheme '{u.Scheme}' must match {SchemeRegex}.");
                    ValidateEntryId(u.EntryId, entryIds, $"{prefix}.entryId", errors);
                    break;
                case InstallFontHook f:
                    if (f.Files.Count == 0) errors.Add($"{prefix}.files must not be empty.");
                    for (int i = 0; i < f.Files.Count; i++)
                    {
                        ValidateRelativePath(f.Files[i], $"{prefix}.files[{i}]", errors);
                    }
                    break;
                case AutostartHook au:
                    ValidateEntryId(au.EntryId, entryIds, $"{prefix}.entryId", errors);
                    if (au.Args != null)
                    {
                        for (int i = 0; i < au.Args.Count; i++)
                        {
                            if (HasControlChars(au.Args[i])) errors.Add($"{prefix}.args[{i}] must not contain control characters.");
                        }
                    }
                    break;
                default:
                    errors.Add($"{prefix} has unknown action type.");
                    break;
            }
        }

        private static void ValidateEntryId(string entryId, HashSet<string> entryIds, string field, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                errors.Add($"{field} is required.");
                return;
            }
            if (!entryIds.Contains(entryId))
            {
                errors.Add($"{field} '{entryId}' does not reference any entries[].id.");
            }
        }

        private static void ValidateUrl(string value, string field, bool allowInsecure, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{field} is required.");
                return;
            }
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            {
                errors.Add($"{field} '{value}' is not a valid absolute URL.");
                return;
            }
            if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecure && uri.Scheme == Uri.UriSchemeHttp))
            {
                errors.Add($"{field} must be HTTPS (got '{uri.Scheme}'). Pass --allow-insecure to permit HTTP.");
            }
        }

        private static void ValidateEd25519Key(string value, string field, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{field} is required.");
                return;
            }
            try
            {
                byte[] decoded = Convert.FromBase64String(value);
                if (decoded.Length != 32)
                {
                    errors.Add($"{field} must decode to 32 bytes (got {decoded.Length}).");
                }
            }
            catch (FormatException)
            {
                errors.Add($"{field} is not valid base64.");
            }
        }

        // Control characters (CR/LF/NUL/etc.) in a field that's written into a .desktop / plist /
        // registry artifact let an attacker break out of the intended key and inject a new one
        // (e.g. an autostart Exec= line). Reject them at validation time as defense in depth on top
        // of the per-writer stripping.
        private static bool HasControlChars(string value)
        {
            foreach (char c in value)
            {
                if (char.IsControl(c)) return true;
            }
            return false;
        }

        private static void ValidateRelativePath(string? value, string field, List<string> errors)
        {
            if (value == null) return;
            if (value.Length == 0)
            {
                errors.Add($"{field} must not be empty.");
                return;
            }
            if (Path.IsPathRooted(value))
            {
                errors.Add($"{field} '{value}' must be relative.");
                return;
            }
            // Reject path traversal AND no-op segments regardless of separator style.
            string normalized = value.Replace('\\', '/');
            foreach (string segment in normalized.Split('/'))
            {
                if (segment == "..")
                {
                    errors.Add($"{field} '{value}' must not contain '..' segments.");
                    return;
                }
                if (segment == ".")
                {
                    errors.Add($"{field} '{value}' must not contain '.' segments; use the plain relative path.");
                    return;
                }
            }
        }
    }

    public sealed class ValidationContext
    {
        public bool AllowInsecure { get; init; }

        public static ValidationContext Default { get; } = new ValidationContext();
    }

    public sealed class ValidationResult
    {
        public IReadOnlyList<string> Errors { get; }
        public bool IsValid => Errors.Count == 0;

        public ValidationResult(IReadOnlyList<string> errors)
        {
            Errors = errors;
        }

        public void EnsureValid()
        {
            if (!IsValid)
            {
                throw new DescriptorValidationException(Errors);
            }
        }
    }

    public sealed class DescriptorValidationException : Exception
    {
        public IReadOnlyList<string> Errors { get; }

        public DescriptorValidationException(IReadOnlyList<string> errors)
            : base("Descriptor validation failed:\n  - " + string.Join("\n  - ", errors))
        {
            Errors = errors;
        }
    }
}
