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
$docs = Join-Path $work "docs"        # granted by hina (specific package SID) — baseline
$docsLow = Join-Path $work "docslow"  # granted by hina (package SID) + we set its integrity label to Low
$docsAap = Join-Path $work "docsaap"  # we grant ALL APPLICATION PACKAGES (S-1-15-2-1) via icacls
$secret = Join-Path $work "secret"
New-Item -ItemType Directory -Force -Path $app, $docs, $docsLow, $docsAap, $secret | Out-Null
Set-Content -Path (Join-Path $secret "key") -Value "topsecret" -NoNewline

# DACLs are provably correct yet the lowbox is denied. Test the two remaining
# hypotheses in one run:
#   WPKG -> write a dir granted the specific package SID (baseline; currently fails)
#   WAAP -> write a dir granted ALL APPLICATION PACKAGES (every lowbox has that group;
#           if this works but WPKG doesn't, the token's package SID isn't matching)
#   WLOW -> write a dir granted the package SID but whose integrity label is lowered to
#           Low (if this works, Mandatory Integrity write-up was the blocker)
$aap = '*S-1-15-2-1'
# ALL APPLICATION PACKAGES: modify on docsAap + traverse (RX, this-folder-only so secret
# doesn't inherit) on every ancestor up the chain.
icacls $docsAap /grant "${aap}:(OI)(CI)(M)" /Q | Out-Null
$d = $work
while ($d) { icacls $d /grant "${aap}:(RX)" /Q | Out-Null; $p = Split-Path $d -Parent; if (-not $p -or $p -eq $d) { break }; $d = $p }
# Lower docsLow's integrity label to Low (inheritable) so a low-IL container can write it.
icacls $docsLow /setintegritylevel "(OI)(CI)L" /Q | Out-Null

$cmdExe = Join-Path $env:SystemRoot "System32\cmd.exe"
$stderrFile = Join-Path $work "stderr.txt"
$inner = "set RS=0& set WP=0& set WA=0& set WL=0& ( type $secret\key 1>nul 2>nul && set RS=1 ) & ( echo x 1>$docs\out 2>nul && set WP=1 ) & ( echo x 1>$docsAap\out 2>nul && set WA=1 ) & ( echo x 1>$docsLow\out 2>nul && set WL=1 ) & echo RSEC=!RS! WPKG=!WP! WAAP=!WA! WLOW=!WL!"

$hinaArgs = @(
    'dev', 'sandbox-run', '--verbose',
    '--app-dir', $app,
    '--allow', ($docs + ':rw'),
    '--allow', ($docsLow + ':rw'),
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

Write-Host "---- icacls docs (package SID) ----"
icacls $docs 2>&1 | Out-String | Write-Host
Write-Host "---- icacls docsaap (ALL APP PACKAGES) ----"
icacls $docsAap 2>&1 | Out-String | Write-Host
Write-Host "---- icacls docslow (Low integrity) ----"
icacls $docsLow 2>&1 | Out-String | Write-Host

$combined = "$out`n$err"

try { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue } catch { }

if ($combined -match 'cannot enforce' -or $combined -match 'running unsandboxed') {
    Write-Host "SKIP: AppContainer not available on this host; passing."
    exit 0
}

# Parse the four measurements.
$rsec = if ($combined -match 'RSEC=(\d)') { $matches[1] } else { '?' }
$wpkg = if ($combined -match 'WPKG=(\d)') { $matches[1] } else { '?' }
$waap = if ($combined -match 'WAAP=(\d)') { $matches[1] } else { '?' }
$wlow = if ($combined -match 'WLOW=(\d)') { $matches[1] } else { '?' }
Write-Host "---- diagnosis ---- RSEC=$rsec WPKG=$wpkg WAAP=$waap WLOW=$wlow"
if ($waap -eq '1' -and $wpkg -eq '0') {
    Write-Host "DIAGNOSIS: ALL APPLICATION PACKAGES works but the specific package SID does not -> token package SID is not matching the granted SID."
}
if ($wlow -eq '1' -and $wpkg -eq '0') {
    Write-Host "DIAGNOSIS: lowering the integrity label let the write through -> Mandatory Integrity (write-up) was the blocker."
}
if ($wpkg -eq '0' -and $waap -eq '0' -and $wlow -eq '0') {
    Write-Host "DIAGNOSIS: nothing worked -> the blocker is deeper than SID matching or integrity (capability-gated FS?)."
}

if ($rsec -eq '0' -and $wpkg -eq '1') {
    Write-Host "PASS: secret read denied, package-SID-granted write allowed under AppContainer."
    exit 0
}

Write-Error "FAIL (diagnostic run): RSEC=$rsec WPKG=$wpkg WAAP=$waap WLOW=$wlow"
exit 1
