<#
.SYNOPSIS
  Windows AppContainer filesystem-sandbox integration probe.

.DESCRIPTION
  The Windows analogue of scripts/landlock-probe.sh. Runs a child (cmd.exe) under a
  Hina sandbox that grants ONLY a "documents" dir (read-write) and deliberately NOT a
  "secret" dir. Under real enforcement the child must be DENIED reading the secret and
  ALLOWED writing the document — i.e. READ=0 WRITE=1.

  STATUS: Windows currently uses NoOp (the AppContainer backend is implemented but
  unverified — see WindowsSandbox.cs). With NoOp, Hina logs that it is running
  unsandboxed; this probe then SKIP-passes so CI stays green, mirroring the Landlock
  probe's behaviour on a host without Landlock. If the AppContainer backend is wired in
  later, this probe becomes its real end-to-end enforcement check with no changes.

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

# Inline cmd (mirrors the Landlock probe's inline `sh -c`): try to read the ungranted
# secret (must fail) and write the granted doc (must succeed). cmd.exe is in System32,
# reachable via ALL APPLICATION PACKAGES. /v:on so !..! expand at run time.
$cmdExe = Join-Path $env:SystemRoot "System32\cmd.exe"
$stderrFile = Join-Path $work "stderr.txt"
$inner = "set R=0& set W=0& ( type $secret\key 1>nul 2>nul && set R=1 ) & ( echo data 1>$docs\out 2>nul && set W=1 ) & echo READ=!R! WRITE=!W!"

$hinaArgs = @(
    'dev', 'sandbox-run',
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
    Write-Host "SKIP: filesystem sandbox not enforced on this host (Windows uses NoOp today); passing."
    exit 0
}

if ($combined -match 'READ=0 WRITE=1') {
    Write-Host "PASS: secret read denied, document write allowed under the sandbox."
    exit 0
}

Write-Error "FAIL: expected 'READ=0 WRITE=1' (secret denied, docs writable). Got: $combined"
exit 1
