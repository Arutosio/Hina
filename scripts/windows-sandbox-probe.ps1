<#
.SYNOPSIS
  Windows AppContainer filesystem-sandbox integration probe.

.DESCRIPTION
  The Windows analogue of scripts/landlock-probe.sh. Runs a child (cmd.exe) under a
  Hina AppContainer sandbox that grants ONLY:
    - the app dir (read+execute, so the container can read its own files)
    - a "documents" dir (read-write)
  and NOTHING else. It deliberately does NOT grant a "secret" dir.

  Under real AppContainer enforcement the child must be DENIED reading the secret
  (the secret dir's DACL grants no AppContainer/package SID) and ALLOWED writing the
  document (its DACL has an explicit container-SID ACE) — i.e. READ=0 WRITE=1.

  System DLL dirs (System32, where cmd.exe lives) are reachable because they already
  carry an ALL APPLICATION PACKAGES ACE — which is exactly why we don't ACL them.

  On a host where AppContainer is unavailable Hina logs that it is running unsandboxed
  and the child sees READ=1 WRITE=1; the probe then SKIP-passes so CI stays green on
  runners without AppContainer (mirrors the Landlock probe's behaviour on old kernels).

.PARAMETER HinaExe
  Path to the built Hina.CLI executable.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$HinaExe
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $HinaExe)) {
    Write-Error "hina binary not found: $HinaExe"
    exit 1
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("hina-sbx-" + [System.Guid]::NewGuid().ToString("N"))
$app = Join-Path $work "app"
$docs = Join-Path $work "docs"
$secret = Join-Path $work "secret"
New-Item -ItemType Directory -Force -Path $app, $docs, $secret | Out-Null
Set-Content -Path (Join-Path $secret "key") -Value "topsecret" -NoNewline

# Inline cmd command (mirrors the Landlock probe's inline `sh -c`): try to read the
# secret (must fail — secret dir is NOT granted) and write the doc (must succeed —
# docs is granted rw), then print the verdict. cmd.exe itself is in System32, reachable
# via the ALL APPLICATION PACKAGES ACE. /v:on so !R!/!W! expand at run time, not parse
# time. Temp paths on the runner have no spaces, so no inner quoting is needed.
$cmdExe = Join-Path $env:SystemRoot "System32\cmd.exe"
$stderrFile = Join-Path $work "stderr.txt"
$inner = "set R=0& set W=0& ( type $secret\key 1>nul 2>nul && set R=1 ) & ( echo data 1>$docs\out 2>nul && set W=1 ) & echo READ=!R! WRITE=!W!"

$hinaArgs = @(
    'dev', 'sandbox-run', '--verbose',
    '--app-dir', $app,
    '--allow', ($docs + ':rw'),
    '--',
    $cmdExe, '/v:on', '/c', $inner
)

Write-Host "Using hina: $HinaExe"
Write-Host "OS: $([System.Environment]::OSVersion.VersionString)"

$out = & $HinaExe @hinaArgs 2>$stderrFile
$err = (Test-Path $stderrFile) ? (Get-Content $stderrFile -Raw) : ""

Write-Host "---- probe stdout ----"
Write-Host $out
Write-Host "---- probe stderr ----"
Write-Host $err

$combined = "$out`n$err"

try { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue } catch { }

if ($combined -match 'cannot enforce' -or $combined -match 'running unsandboxed') {
    Write-Host "SKIP: AppContainer not available on this host; passing."
    exit 0
}

if ($combined -match 'READ=0 WRITE=1') {
    Write-Host "PASS: secret read denied, document write allowed under AppContainer."
    exit 0
}

Write-Error "FAIL: expected 'READ=0 WRITE=1' (secret denied, docs writable). Got: $combined"
exit 1
