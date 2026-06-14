using System.Linq;

namespace Hina.Host
{
    // Sanitizes attacker-controlled strings (request path, User-Agent) before they are written to
    // the log. Kestrel rejects raw CR/LF in the request line and headers, but percent-encoded
    // newlines (%0A/%0D) are decoded into ctx.Request.Path.Value, so a crafted path could forge a
    // log line under the default console formatter (BUG-040). Replace every control character with
    // '_' and cap the length to bound a single record.
    public static class LogSanitizer
    {
        public static string SanitizeForLog(string? s, int max = 256)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            return new string(s.Take(max).Select(c => char.IsControl(c) ? '_' : c).ToArray());
        }
    }
}
