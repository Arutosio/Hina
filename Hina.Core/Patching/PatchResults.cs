using System.Collections.Generic;

namespace Hina.Core.Patching
{
    public sealed class CheckResult
    {
        public bool IsUpdateAvailable { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public sealed class PatchResult
    {
        public bool Success { get; set; }
        public List<string> AppliedFiles { get; set; } = new List<string>();
        public string Message { get; set; } = string.Empty;
    }

    public sealed class VerifyResult
    {
        public bool Success { get; set; }
        public List<string> BrokenFiles { get; set; } = new List<string>();
        public string Message { get; set; } = string.Empty;
    }
}
