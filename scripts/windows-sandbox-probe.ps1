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
$docs = Join-Path $work "docs"        # under the deep user-profile temp tree (baseline)
$secret = Join-Path $work "secret"
New-Item -ItemType Directory -Force -Path $app, $docs, $secret | Out-Null
Set-Content -Path (Join-Path $secret "key") -Value "topsecret" -NoNewline

# Nothing under the profile worked (package SID / ALL APP PACKAGES / Low integrity all
# denied) though system32 is reachable. Test whether the PATH LOCATION is the blocker:
# grant a SHALLOW dir directly under C:\ (off the user profile, short traverse chain).
#   WPKG  -> write the deep profile dir (baseline; currently fails)
#   WROOT -> write the shallow C:\ dir
# If WROOT=1 while WPKG=0, the user-profile path (runner ACLs) is the blocker, not the
# AppContainer mechanism.
$rootDir = "C:\hina-sbx-" + [System.Guid]::NewGuid().ToString("N")
$rootDocs = Join-Path $rootDir "docs"
New-Item -ItemType Directory -Force -Path $rootDocs | Out-Null

$cmdExe = Join-Path $env:SystemRoot "System32\cmd.exe"
$stderrFile = Join-Path $work "stderr.txt"
$inner = "set RS=0& set WP=0& set WR=0& ( type $secret\key 1>nul 2>nul && set RS=1 ) & ( echo x 1>$docs\out 2>nul && set WP=1 ) & ( echo x 1>$rootDocs\out 2>nul && set WR=1 ) & echo RSEC=!RS! WPKG=!WP! WROOT=!WR!"

$hinaArgs = @(
    'dev', 'sandbox-run', '--verbose',
    '--app-dir', $app,
    '--allow', ($docs + ':rw'),
    '--allow', ($rootDocs + ':rw'),
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

Write-Host "---- icacls docs (deep profile) ----"
icacls $docs 2>&1 | Out-String | Write-Host
Write-Host "---- icacls rootDocs (shallow C:\) ----"
icacls $rootDocs 2>&1 | Out-String | Write-Host

$combined = "$out`n$err"

try { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue } catch { }
try { Remove-Item -Recurse -Force $rootDir -ErrorAction SilentlyContinue } catch { }

if ($combined -match 'cannot enforce' -or $combined -match 'running unsandboxed') {
    Write-Host "SKIP: AppContainer not available on this host; passing."
    exit 0
}

# Parse the measurements.
$rsec = if ($combined -match 'RSEC=(\d)') { $matches[1] } else { '?' }
$wpkg = if ($combined -match 'WPKG=(\d)') { $matches[1] } else { '?' }
$wroot = if ($combined -match 'WROOT=(\d)') { $matches[1] } else { '?' }
Write-Host "---- diagnosis ---- RSEC=$rsec WPKG=$wpkg WROOT=$wroot"
if ($wroot -eq '1' -and $wpkg -eq '0') {
    Write-Host "DIAGNOSIS: shallow C:\ dir works but the deep profile dir does not -> the user-profile path (runner ACLs) is the blocker, not the AppContainer mechanism."
}
if ($wroot -eq '0' -and $wpkg -eq '0') {
    Write-Host "DIAGNOSIS: even a shallow C:\ dir is denied -> the blocker is fundamental, not path-specific."
}

# Real success: the deep profile dir is what production uses, so require WPKG=1.
if ($rsec -eq '0' -and $wpkg -eq '1') {
    Write-Host "PASS: secret read denied, granted write allowed under AppContainer."
    exit 0
}

Write-Error "FAIL (diagnostic run): RSEC=$rsec WPKG=$wpkg WROOT=$wroot"
exit 1
