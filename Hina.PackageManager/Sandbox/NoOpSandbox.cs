using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Hina.PackageManager.Sandbox
{
    // Fallback launcher for hosts without a real sandbox backend (macOS, Windows,
    // or a Linux kernel too old for Landlock). Runs the app with no restriction,
    // warning once if the plan asked for any. Never blocks a launch.
    public sealed class NoOpSandbox : ISandboxLauncher
    {
        private readonly ILogger _logger;

        public NoOpSandbox(ILogger logger)
        {
            _logger = logger;
        }

        public bool IsSupported => false;

        public int Launch(string execAbs, IReadOnlyList<string> appArgs, SandboxPlan plan, CancellationToken ct)
        {
            if (!plan.Unrestricted && plan.Rules.Count > 1)
            {
                // >1 because the app dir is always present; more than that means the
                // descriptor requested real scoping this host cannot enforce.
                _logger.LogWarning(
                    "This app requested filesystem sandboxing, but this platform cannot enforce it. Running unsandboxed.");
            }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = execAbs,
                UseShellExecute = false,
            };
            foreach (string a in appArgs)
            {
                psi.ArgumentList.Add(a);
            }

            using Process proc = Process.Start(psi)!;
            proc.WaitForExit();
            return proc.ExitCode;
        }
    }
}
