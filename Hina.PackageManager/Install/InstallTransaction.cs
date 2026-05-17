using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Hooks;
using Hina.PackageManager.Platform;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Install
{
    // Journals every side-effect of an install so a mid-flight exception can unwind in
    // reverse order, leaving the system as it was before InstallAsync ran. Operations are
    // recorded *after* they succeed — never before — so the unwinder only touches state
    // it actually created.
    public sealed class InstallTransaction
    {
        private readonly IPlatformIntegration _platform;
        private readonly HookExecutor _hooks;
        private readonly Stack<Step> _steps = new Stack<Step>();

        public InstallTransaction(IPlatformIntegration platform, HookExecutor hooks)
        {
            _platform = platform;
            _hooks = hooks;
        }

        public void RecordAppDirCreated(string appDir)
        {
            _steps.Push(new Step { Kind = StepKind.AppDir, Evidence = appDir });
        }

        public void RecordShellEntry(string evidencePath)
        {
            _steps.Push(new Step { Kind = StepKind.Shell, Evidence = evidencePath });
        }

        public void RecordHook(HookEvidence evidence)
        {
            _steps.Push(new Step { Kind = StepKind.Hook, Evidence = evidence.Evidence, HookEvidence = evidence });
        }

        public async Task RollbackAsync(CancellationToken ct)
        {
            while (_steps.Count > 0)
            {
                Step step = _steps.Pop();
                try
                {
                    switch (step.Kind)
                    {
                        case StepKind.Hook:
                            if (step.HookEvidence != null)
                            {
                                await _hooks.UndoAsync(step.HookEvidence, ct);
                            }
                            break;
                        case StepKind.Shell:
                            await _platform.RemoveMenuShortcut(step.Evidence, ct);
                            break;
                        case StepKind.AppDir:
                            if (Directory.Exists(step.Evidence))
                            {
                                Directory.Delete(step.Evidence, recursive: true);
                            }
                            break;
                    }
                }
                catch
                {
                    // Rollback must be fail-soft; one stuck step cannot block the rest.
                }
            }
        }

        private enum StepKind { AppDir, Shell, Hook }

        private sealed class Step
        {
            public StepKind Kind;
            public string Evidence = string.Empty;
            public HookEvidence? HookEvidence;
        }
    }
}
