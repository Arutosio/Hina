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
# Pre-create a file INSIDE the granted docs dir so we can measure a granted-dir READ
# separately from a granted-dir WRITE. Integrity (MIC) only blocks write-up, never
# read-up — so if the container can READ this but not WRITE docs\out, the blocker is
# integrity, not traversal; if it can't even read it, the blocker is traversal.
Set-Content -Path (Join-Path $docs "readme") -Value "hello" -NoNewline

# Inline cmd command (mirrors the Landlock probe's inline `sh -c`). Three measurements:
#   RSEC = read the ungranted secret (must be 0 — isolation)
#   RDOC = read a granted-dir file   (traverse test: 1 = reached docs)
#   WDOC = write a granted-dir file  (must be 1 — the grant works end to end)
# cmd.exe is in System32, reachable via ALL APPLICATION PACKAGES. /v:on so !..! expand
# at run time. Temp paths on the runner have no spaces, so no inner quoting is needed.
$cmdExe = Join-Path $env:SystemRoot "System32\cmd.exe"
$stderrFile = Join-Path $work "stderr.txt"
$inner = "set RS=0& set RD=0& set W=0& ( type $secret\key 1>nul 2>nul && set RS=1 ) & ( type $docs\readme 1>nul 2>nul && set RD=1 ) & ( echo data 1>$docs\out 2>nul && set W=1 ) & echo RSEC=!RS! RDOC=!RD! WDOC=!W!"

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

# Diagnostics: did the container-SID ACE actually land on docs, and is docs writable
# at all from a normal (unsandboxed) context? Separates "grant code broken" from
# "container cannot traverse / leaf ACE ineffective".
Write-Host "---- icacls docs ----"
icacls $docs 2>&1 | Out-String | Write-Host
Write-Host "---- icacls work (ancestor) ----"
icacls $work 2>&1 | Out-String | Write-Host
Write-Host "---- icacls temp (ancestor) ----"
icacls (Split-Path $work -Parent) 2>&1 | Out-String | Write-Host
Write-Host "---- unsandboxed write sanity ----"
try { Set-Content -Path (Join-Path $docs "sanity") -Value "ok"; Write-Host "unsandboxed docs write OK" }
catch { Write-Host "unsandboxed docs write FAILED: $_" }

$combined = "$out`n$err"

try { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue } catch { }

if ($combined -match 'cannot enforce' -or $combined -match 'running unsandboxed') {
    Write-Host "SKIP: AppContainer not available on this host; passing."
    exit 0
}

# Parse the three measurements for the diagnosis.
$rsec = if ($combined -match 'RSEC=(\d)') { $matches[1] } else { '?' }
$rdoc = if ($combined -match 'RDOC=(\d)') { $matches[1] } else { '?' }
$wdoc = if ($combined -match 'WDOC=(\d)') { $matches[1] } else { '?' }
Write-Host "---- diagnosis ---- RSEC=$rsec RDOC=$rdoc WDOC=$wdoc"
if ($rsec -eq '0' -and $rdoc -eq '1' -and $wdoc -eq '0') {
    Write-Host "DIAGNOSIS: container reached+read the granted dir but write was blocked -> INTEGRITY (write-up), not traversal."
} elseif ($rsec -eq '0' -and $rdoc -eq '0') {
    Write-Host "DIAGNOSIS: container could not even read the granted dir -> TRAVERSAL is the blocker."
}

if ($rsec -eq '0' -and $wdoc -eq '1') {
    Write-Host "PASS: secret read denied, document write allowed under AppContainer."
    exit 0
}

Write-Error "FAIL: expected RSEC=0 WDOC=1 (secret denied, docs writable). Got: RSEC=$rsec RDOC=$rdoc WDOC=$wdoc"
exit 1
