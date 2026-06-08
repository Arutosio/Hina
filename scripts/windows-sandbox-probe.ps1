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

# Even a shallow C:\ dir granted the package SID was denied — only system32 (system
# ALL-APP-PACKAGES baseline) is reachable. Final decisive test, shallow under C:\ (which
# is provably traversable since system32 works), using ALL APPLICATION PACKAGES (the
# group the container demonstrably has):
#   WAR    -> write a C:\ dir granted ALL APPLICATION PACKAGES
#   WARLOW -> same, but with the integrity label lowered to Low
# WAR=1 -> the mechanism works and the earlier failures were SID-matching/traversal.
# WARLOW=1 only -> Mandatory Integrity was the blocker. Both 0 -> fundamental token
# restriction; the AppContainer cannot honor any runtime grant -> ship NoOp + document.
$aap = '*S-1-15-2-1'
$rootDir = "C:\hina-sbx-" + [System.Guid]::NewGuid().ToString("N")
$rootA = Join-Path $rootDir "a"
$rootB = Join-Path $rootDir "b"
New-Item -ItemType Directory -Force -Path $rootA, $rootB | Out-Null
icacls $rootDir /grant "${aap}:(RX)" /Q | Out-Null            # traverse the C:\ test root
icacls $rootA /grant "${aap}:(OI)(CI)(M)" /Q | Out-Null
icacls $rootB /grant "${aap}:(OI)(CI)(M)" /Q | Out-Null
icacls $rootB /setintegritylevel "(OI)(CI)L" /Q | Out-Null

$cmdExe = Join-Path $env:SystemRoot "System32\cmd.exe"
$stderrFile = Join-Path $work "stderr.txt"
$inner = "set RS=0& set WP=0& set WA=0& set WB=0& ( type $secret\key 1>nul 2>nul && set RS=1 ) & ( echo x 1>$docs\out 2>nul && set WP=1 ) & ( echo x 1>$rootA\out 2>nul && set WA=1 ) & ( echo x 1>$rootB\out 2>nul && set WB=1 ) & echo RSEC=!RS! WPKG=!WP! WAR=!WA! WARLOW=!WB!"

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

Write-Host "---- icacls rootA (C:\ ALL APP PACKAGES) ----"
icacls $rootA 2>&1 | Out-String | Write-Host
Write-Host "---- icacls rootB (C:\ ALL APP PACKAGES + Low IL) ----"
icacls $rootB 2>&1 | Out-String | Write-Host

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
$war = if ($combined -match 'WAR=(\d)') { $matches[1] } else { '?' }
$warlow = if ($combined -match 'WARLOW=(\d)') { $matches[1] } else { '?' }
Write-Host "---- diagnosis ---- RSEC=$rsec WPKG=$wpkg WAR=$war WARLOW=$warlow"
if ($war -eq '1') {
    Write-Host "DIAGNOSIS: ALL APPLICATION PACKAGES write works on a shallow C:\ dir -> mechanism is fine; earlier failures were SID-matching/traversal under the profile."
} elseif ($warlow -eq '1') {
    Write-Host "DIAGNOSIS: only the Low-integrity dir was writable -> Mandatory Integrity (write-up) is the blocker."
} else {
    Write-Host "DIAGNOSIS: even ALL APPLICATION PACKAGES on a shallow C:\ dir is denied -> fundamental token restriction; the AppContainer honors no runtime grant. Recommend NoOp + documented gap."
}

if ($rsec -eq '0' -and $wpkg -eq '1') {
    Write-Host "PASS: secret read denied, granted write allowed under AppContainer."
    exit 0
}

Write-Error "FAIL (diagnostic run): RSEC=$rsec WPKG=$wpkg WAR=$war WARLOW=$warlow"
exit 1
