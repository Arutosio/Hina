using System.Collections.Generic;
using System.Threading;

namespace Hina.PackageManager.Sandbox
{
    // Per-OS sandboxed process launcher. Mirrors IPlatformIntegration's "one impl
    // per OS, selected by a factory" shape. v1 enforces filesystem scope only.
    //
    // Launch semantics: the returned int is the app's exit code. A backend MAY
    // replace the current process image (execve) instead of spawning a child — in
    // that case Launch never returns on success and only throws on exec failure.
    public interface ISandboxLauncher
    {
        // True when this host can actually enforce the plan (e.g. kernel supports
        // Landlock). When false the launcher still runs the app, unsandboxed, after
        // warning once — it must never block a launch.
        bool IsSupported { get; }

        int Launch(string execAbs, IReadOnlyList<string> appArgs, SandboxPlan plan, CancellationToken ct);
    }
}
