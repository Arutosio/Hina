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

            if (!SemVerRegex.IsMatch(descriptor.Version))
            {
                errors.Add($"version '{descriptor.Version}' is not valid SemVer.");
            }

            if (string.IsNullOrWhiteSpace(descriptor.Publisher))
            {
                errors.Add("publisher is required.");
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
                else if (!entryIds.Add(e.Id))
                {
                    errors.Add($"{prefix}.id '{e.Id}' is duplicated.");
                }

                if (string.IsNullOrWhiteSpace(e.Name))
                {
                    errors.Add($"{prefix}.name is required.");
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

            return new ValidationResult(errors);
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
                    if (m.Extensions.Count == 0) errors.Add($"{prefix}.extensions must not be empty.");
                    ValidateEntryId(m.EntryId, entryIds, $"{prefix}.entryId", errors);
                    break;
                case UrlSchemeHook u:
                    if (string.IsNullOrWhiteSpace(u.Scheme)) errors.Add($"{prefix}.scheme is required.");
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
