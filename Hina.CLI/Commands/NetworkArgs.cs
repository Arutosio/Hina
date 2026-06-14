using Hina.PackageManager.Install;

namespace Hina.CLI.Commands
{
    // Centralises parsing of the network-tuning flags exposed on `hina install`
    // and `hina update`:
    //   --retries N              (default 8)
    //   --connect-timeout SEC    (default 10)
    //   --request-timeout SEC    (default 60)
    // Useful on flaky / mobile / IP-changing connections where the engine's
    // defaults aren't aggressive enough.
    internal static class NetworkArgs
    {
        public static NetworkOptions FromArgs(string[] args)
        {
            NetworkOptions defaults = new NetworkOptions();
            int retries = ParseInt(args, "--retries", defaults.MaxRetries);
            int connectSec = ParseInt(args, "--connect-timeout", defaults.ConnectTimeoutMs / 1000);
            int requestSec = ParseInt(args, "--request-timeout", defaults.RequestTimeoutMs / 1000);

            return new NetworkOptions
            {
                MaxRetries = retries,
                RetryBaseDelayMs = defaults.RetryBaseDelayMs,
                MaxRetryDelayMs = defaults.MaxRetryDelayMs,
                ConnectTimeoutMs = connectSec * 1000,
                RequestTimeoutMs = requestSec * 1000
            };
        }

        private static int ParseInt(string[] args, string flag, int fallback)
        {
            string? raw = Args.GetValue(args, flag);
            // Flag absent → use the engine default. Flag PRESENT but not a positive integer →
            // fail loudly: silently falling back to the default would make `--retries 0`,
            // `--retries -5` or `--retries abc` look honored while the engine ignored them.
            if (raw == null)
            {
                // GetValue can't see a value after a flag that is the LAST token; without this
                // check `--retries` (number forgotten) silently ran with the default.
                if (Args.HasFlag(args, flag))
                {
                    throw new System.FormatException($"Missing value for {flag}. Expected a positive integer.");
                }
                return fallback;
            }
            if (int.TryParse(raw, out int parsed) && parsed > 0)
            {
                // --connect-timeout/--request-timeout are SECONDS converted to ms (sec*1000).
                // Without an upper bound, sec >= 2_147_484 overflows int and yields a negative
                // timeout (ArgumentOutOfRange downstream) or, in a narrow band, a tiny positive
                // timeout — the opposite of the intent, silently (BUG-033). --retries isn't scaled.
                const int MaxTimeoutSeconds = 86_400; // 24h; *1000 = 86.4M << int.MaxValue
                if (flag != "--retries" && parsed > MaxTimeoutSeconds)
                {
                    throw new System.FormatException(
                        $"Value for {flag} is too large: '{raw}'. Expected 1..{MaxTimeoutSeconds} seconds.");
                }
                return parsed;
            }
            throw new System.FormatException($"Invalid value for {flag}: '{raw}'. Expected a positive integer.");
        }
    }
}
