using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Install;

namespace Hina.PackageManager
{
    public enum UpdateAvailability
    {
        UpToDate,
        UpdateAvailable,
        Unknown
    }

    public sealed class UpdateCheckResult
    {
        public string CurrentVersion { get; init; } = string.Empty;
        public string? LatestVersion { get; init; }
        public UpdateAvailability Status { get; init; }
        public string? Error { get; init; }
    }

    // Checks whether a newer Hina release exists on GitHub. Network + JSON parsing are kept
    // AOT-safe (no reflection serializer — the tag is extracted with a regex) and the fetch is
    // injectable so the comparison logic is unit-testable without hitting the network.
    public static class SelfUpdate
    {
        // GitHub "latest release" API for the Hina repository.
        public const string LatestReleaseApiUrl = "https://api.github.com/repos/Arutosio/Hina/releases/latest";

        private static readonly Regex TagNameRegex =
            new Regex("\"tag_name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);

        // Pull "tag_name" out of a GitHub release JSON payload. Returns null if absent.
        public static string? ParseTagName(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Match m = TagNameRegex.Match(json);
            return m.Success ? m.Groups[1].Value : null;
        }

        // Compare the running version against a release tag (e.g. "v1.2.0" or "1.2.0").
        public static UpdateCheckResult Evaluate(string current, string? latestTag)
        {
            if (string.IsNullOrWhiteSpace(latestTag))
            {
                return new UpdateCheckResult { CurrentVersion = current, Status = UpdateAvailability.Unknown, Error = "no release tag found" };
            }

            string latest = latestTag.TrimStart('v', 'V').Trim();
            if (!HinaVersion.TryCompare(current, latest, out int sign))
            {
                return new UpdateCheckResult { CurrentVersion = current, LatestVersion = latest, Status = UpdateAvailability.Unknown, Error = $"unparseable version (current '{current}', latest '{latest}')" };
            }

            return new UpdateCheckResult
            {
                CurrentVersion = current,
                LatestVersion = latest,
                Status = sign < 0 ? UpdateAvailability.UpdateAvailable : UpdateAvailability.UpToDate
            };
        }

        // Fetch the latest release JSON via the injected delegate, parse the tag, and evaluate.
        // Any failure (network, missing tag) collapses to Unknown with an error message.
        public static async Task<UpdateCheckResult> CheckAsync(
            string current,
            Func<CancellationToken, Task<string>> fetchReleaseJson,
            CancellationToken ct)
        {
            try
            {
                string json = await fetchReleaseJson(ct);
                return Evaluate(current, ParseTagName(json));
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult { CurrentVersion = current, Status = UpdateAvailability.Unknown, Error = ex.Message };
            }
        }
    }
}
