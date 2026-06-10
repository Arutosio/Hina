<#
.SYNOPSIS
    Hina one-liner installer for Windows.

.DESCRIPTION
    Downloads the latest Hina CLI release zip, extracts hina.exe to
    %LOCALAPPDATA%\Hina\bin (or $env:HINA_INSTALL_DIR), and prepends the
    install directory to the user PATH. Hardened for power-loss,
    network drop-out, and existing-install scenarios.

.EXAMPLE
    iwr -useb https://raw.githubusercontent.com/Arutosio/Hina/master/scripts/install.ps1 | iex

.NOTES
    Environment overrides:
        HINA_VERSION         pin a release tag (e.g. v1.2.3). default: latest
        HINA_INSTALL_DIR     destination directory.            default: %LOCALAPPDATA%\Hina\bin
        HINA_NO_MODIFY_PATH  set to 1 to skip editing user PATH
        HINA_ACTION          bypass menu when an existing install is detected. one of:
                             reinstall | purge | verify | exit
                             uninstall-full | uninstall-configs | uninstall-binary
        HINA_PURGE_YES       set to 1 to confirm clean reinstall non-interactively
        HINA_UNINSTALL_YES   set to 1 to confirm uninstall-full / uninstall-configs
                             non-interactively (mode 'uninstall-binary' is unaffected)
        HINA_NO_CHECKSUM     set to 1 to skip SHA-256 verification (debug only)
#>

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

function Invoke-Main {
    $Repo            = 'Arutosio/Hina'
    $Tag             = $env:HINA_VERSION
    $Dest            = if ($env:HINA_INSTALL_DIR) { $env:HINA_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Hina\bin' }
    $RegistryDir     = Join-Path $env:LOCALAPPDATA 'Hina'
    $NoModifyPath    = $env:HINA_NO_MODIFY_PATH -eq '1'
    $ActionOverride  = $env:HINA_ACTION
    $PurgeYes        = $env:HINA_PURGE_YES -eq '1'
    $UninstallYes    = $env:HINA_UNINSTALL_YES -eq '1'
    $NoChecksum      = $env:HINA_NO_CHECKSUM -eq '1'

    # Auto-prefix bare version (e.g. '1.2.3' -> 'v1.2.3'). GitHub release tags
    # use the v-prefixed form.
    if ($Tag -and $Tag -match '^[0-9]') { $Tag = "v$Tag" }

    function Write-Info($msg) { Write-Host "hina-install: $msg" }
    function Stop-Err($msg)   { Write-Host "hina-install: $msg" -ForegroundColor Red; exit 1 }

    function Get-HinaArch {
        # RuntimeInformation requires .NET Framework 4.7.1+. Older Windows 10
        # builds (LTSB 2016, ship with 4.6.2) lack this type. Fall back to the
        # PROCESSOR_ARCHITECTURE env var, which is always populated.
        $a = $null
        try {
            $a = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        } catch {
            $a = $env:PROCESSOR_ARCHITECTURE
        }
        switch -Regex ($a) {
            '^(X64|AMD64)$'    { return 'x64' }
            '^(Arm64|ARM64)$'  { return 'arm64' }
            default            { Stop-Err "unsupported architecture: $a. Download a release manually from https://github.com/$Repo/releases" }
        }
    }

    # Extract a bare X.Y.Z from arbitrary text (e.g. "hina 1.0.0" or "v1.0.0").
    function Get-VerNum([string]$s) {
        if ($s -and $s -match '(\d+\.\d+\.\d+)') { return $Matches[1] }
        return $null
    }

    # Compare two X.Y.Z versions numerically: -1 if $a<$b, 0 if equal, 1 if $a>$b.
    function Compare-SemVer([string]$a, [string]$b) {
        $pa = $a -split '\.'
        $pb = $b -split '\.'
        for ($i = 0; $i -lt 3; $i++) {
            if ([int]$pa[$i] -lt [int]$pb[$i]) { return -1 }
            if ([int]$pa[$i] -gt [int]$pb[$i]) { return 1 }
        }
        return 0
    }

    function Resolve-LatestTag {
        try {
            $rel = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/$Repo/releases/latest"
            if ($rel.tag_name) { return $rel.tag_name }
        } catch {
            Write-Info "GitHub API unreachable, falling back to redirect lookup..."
        }
        $req = [System.Net.HttpWebRequest]::Create("https://github.com/$Repo/releases/latest")
        $req.AllowAutoRedirect = $false
        $resp = $req.GetResponse()
        try {
            $loc = $resp.Headers['Location']
            if ($loc) { return $loc.Split('/')[-1] }
        } finally {
            $resp.Close()
        }
        Stop-Err "could not resolve latest release tag. Set `$env:HINA_VERSION = 'v...' manually."
    }

    function Invoke-Download {
        param([string]$Url, [string]$OutFile)
        # Invoke-WebRequest -OutFile overwrites (it doesn't append) when a Range
        # header is set, which silently truncates the file on resume and breaks
        # retries. Re-download from scratch on each attempt instead. Failed
        # partials are cleaned up before each try.
        $partial = "$OutFile.partial"
        for ($i = 1; $i -le 5; $i++) {
            try {
                if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
                Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $partial -TimeoutSec 600
                Move-Item -LiteralPath $partial -Destination $OutFile -Force
                return
            } catch {
                Write-Info "download attempt $i failed: $($_.Exception.Message). Retrying in ${i}s..."
                Start-Sleep -Seconds $i
            }
        }
        Stop-Err "download failed after 5 attempts: $Url"
    }

    function Get-Sha256 {
        param([string]$Path)
        (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    }

    function Test-ArchiveChecksum {
        param([string]$Archive, [string]$ShaUrl)
        if ($NoChecksum) {
            Write-Info 'HINA_NO_CHECKSUM=1, skipping SHA-256 verify'
            return
        }
        $shaFile = "$Archive.sha256"
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $ShaUrl -OutFile $shaFile -TimeoutSec 60
        } catch {
            # Only a real 404 (checksum genuinely not published, i.e. an old
            # release) may skip verification. A transient network error - or a
            # MITM blocking just the .sha256 - must NOT silently downgrade the
            # install to unverified.
            $status = 0
            try { $status = [int]$_.Exception.Response.StatusCode } catch { }
            if ($status -eq 404) {
                Write-Info "no SHA-256 published for $Tag (older release). Falling back to structural zip check only."
                return
            }
            Stop-Err "could not download the SHA-256 checksum for $Tag (network or server error, not a missing file). Refusing to install an unverified archive; re-run the installer, or set HINA_NO_CHECKSUM=1 to bypass (not recommended)."
        }
        $expected = ((Get-Content -LiteralPath $shaFile -TotalCount 1) -split '\s+')[0].ToLowerInvariant()
        if (-not $expected) { Stop-Err 'downloaded .sha256 is empty or malformed' }
        $actual = Get-Sha256 -Path $Archive
        if ($expected -ne $actual) {
            Stop-Err "SHA-256 mismatch for $(Split-Path -Leaf $Archive) - expected $expected, got $actual. Archive may be corrupt or tampered."
        }
        Write-Info 'checksum verified (SHA-256)'
    }

    function Test-Zip {
        param([string]$Path)
        try {
            $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
            $zip.Dispose()
            return $true
        } catch {
            return $false
        }
    }

    function Test-Interactive {
        # Heuristic: if [Console]::In is redirected (curl piped to iex) or no
        # window is attached, treat as non-interactive.
        try {
            return -not [Console]::IsInputRedirected -and $Host.UI.RawUI -ne $null
        } catch {
            return $false
        }
    }

    function Read-Prompt($prompt) {
        Write-Host -NoNewline $prompt
        return [Console]::ReadLine()
    }

    function Acquire-Lock {
        param([string]$LockDir)
        $parent = Split-Path -Parent $LockDir
        if ($parent -and -not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        try {
            New-Item -ItemType Directory -Path $LockDir -ErrorAction Stop | Out-Null
            "$PID" | Out-File -LiteralPath (Join-Path $LockDir 'pid') -Encoding ascii
            return
        } catch {
            $pidFile = Join-Path $LockDir 'pid'
            $oldPid = $null
            if (Test-Path -LiteralPath $pidFile) {
                $oldPid = (Get-Content -LiteralPath $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
            }
            $alive = $false
            if ($oldPid) {
                try { Get-Process -Id ([int]$oldPid) -ErrorAction Stop | Out-Null; $alive = $true } catch { }
            }
            if ($alive) {
                Stop-Err "another install is in progress (pid=$oldPid). If this is wrong: Remove-Item -Recurse -Force '$LockDir'"
            }
            Write-Info "stale lock from pid=$oldPid found, overriding"
            Remove-Item -LiteralPath $LockDir -Recurse -Force
            New-Item -ItemType Directory -Path $LockDir -ErrorAction Stop | Out-Null
            "$PID" | Out-File -LiteralPath (Join-Path $LockDir 'pid') -Encoding ascii
        }
    }

    # --- platform + version ---------------------------------------------------
    $arch = Get-HinaArch

    if (-not $Tag) {
        Write-Info 'resolving latest release...'
        $Tag = Resolve-LatestTag
    }
    Write-Info "target: hina $Tag (windows-$arch)"

    $archiveName = "Hina-windows-$arch.zip"
    $url         = "https://github.com/$Repo/releases/download/$Tag/$archiveName"
    $shaUrl      = "$url.sha256"

    # --- existing install detection + menu -----------------------------------
    $existingExe = Join-Path $Dest 'hina.exe'
    $existingVer = $null
    if (Test-Path -LiteralPath $existingExe) {
        try {
            $existingVer = (& $existingExe --version 2>$null | Select-Object -First 1)
            if (-not $existingVer) { $existingVer = 'unknown' }
        } catch { $existingVer = 'unknown' }
    }

    $action = $null
    $validActions = @('reinstall','purge','verify','exit','uninstall-full','uninstall-configs','uninstall-binary')
    if ($ActionOverride) {
        if ($validActions -notcontains $ActionOverride) {
            Stop-Err "invalid HINA_ACTION=$ActionOverride (use: $($validActions -join '|'))"
        }
        $action = $ActionOverride
        if ($existingVer) { Write-Info "existing install detected ($existingVer); HINA_ACTION=$action" }
    } elseif ($existingVer) {
        if (Test-Interactive) {
            Write-Host ""
            Write-Host "Hina is already installed."
            Write-Host "  Installed: $existingVer"
            Write-Host "  Location:  $existingExe"
            Write-Host "  Target:    $Tag"
            Write-Host ""
            Write-Host "What would you like to do?"
            Write-Host "  [1] Reinstall       - replace binary; keep installed apps and registry"
            Write-Host "  [2] Clean reinstall - WIPES registry, apps, and pinned publisher keys"
            Write-Host "  [3] Integrity check - verify installed binary against release SHA-256"
            Write-Host "  [4] Exit            - leave installation as-is"
            Write-Host "  [5] Uninstall       - remove hina (with options)"
            Write-Host ""
            $choice = Read-Prompt 'Choice [1-5] (default 4): '
            switch -Regex ($choice) {
                '^(1|reinstall)$'   { $action = 'reinstall' }
                '^(2|clean|purge)$' { $action = 'purge' }
                '^(3|verify|check)$'{ $action = 'verify' }
                '^(4|exit|q|quit|)$'{ $action = 'exit' }
                '^(5|uninstall|remove)$' {
                    Write-Host ""
                    Write-Host "Uninstall options:"
                    Write-Host "  [a] Full     - remove hina + all installed apps + configs."
                    Write-Host "                 Per-app teardown via 'hina uninstall <name>' first so"
                    Write-Host "                 side-effects (shortcuts, shims, HKCU MIME) are cleaned."
                    Write-Host "  [b] Configs  - remove hina + configs (registry, keys, descriptors)."
                    Write-Host "                 KEEPS app binaries on disk as orphan files."
                    Write-Host "  [c] Binary   - remove only hina.exe. KEEPS apps + configs intact."
                    Write-Host "                 Reinstalling hina resumes the previous state."
                    Write-Host ""
                    $submode = Read-Prompt 'Choice [a/b/c]: '
                    switch -Regex ($submode) {
                        '^(a|full|A)$'        { $action = 'uninstall-full' }
                        '^(b|configs|B)$'     { $action = 'uninstall-configs' }
                        '^(c|binary|bin|C)$'  { $action = 'uninstall-binary' }
                        default               { Stop-Err "invalid uninstall option: $submode" }
                    }
                }
                default             { Stop-Err "invalid choice: $choice" }
            }
        } else {
            # The old comparison ($existingVer -like "*$Tag*") could never match:
            # `hina --version` prints "hina 1.4.2" while the tag is "v1.4.2", so
            # every non-interactive re-run re-downloaded and reinstalled the same
            # version - and silently downgraded an installed build newer than the
            # target. Mirror install.sh: numeric compare, upgrade only when behind.
            $existingNum = Get-VerNum $existingVer
            $targetNum   = Get-VerNum $Tag
            if ($existingNum -and $targetNum) {
                switch (Compare-SemVer $existingNum $targetNum) {
                    -1 {
                        Write-Info "non-interactive: $existingNum is behind $targetNum, upgrading (set HINA_ACTION to override)"
                        $action = 'reinstall'
                    }
                    0 {
                        Write-Info "non-interactive: already at $targetNum, nothing to do (set HINA_ACTION to override)"
                        $action = 'exit'
                    }
                    1 {
                        Write-Info "non-interactive: installed $existingNum is newer than target $targetNum, leaving as-is (set HINA_ACTION=reinstall to force)"
                        $action = 'exit'
                    }
                }
            } else {
                Write-Info 'non-interactive: could not determine installed version, upgrading (set HINA_ACTION to override)'
                $action = 'reinstall'
            }
        }
    } else {
        $action = 'install'
    }

    switch ($action) {
        'exit'   { Write-Info 'no changes made'; return }
        'verify' { Invoke-VerifyAction -Dest $Dest -Tag $Tag -ArchiveUrl $url -ShaUrl $shaUrl -ArchiveName $archiveName; return }
    }

    # --- acquire lock for filesystem-modifying actions -----------------------
    New-Item -ItemType Directory -Force -Path $Dest | Out-Null
    $lockDir = Join-Path $Dest '.hina-install.lock'
    Acquire-Lock -LockDir $lockDir

    # uninstall short-circuits before tmpdir is created (no download needed)
    if ($action -like 'uninstall-*') {
        try {
            Invoke-UninstallAction `
                -Mode $action `
                -Dest $Dest `
                -ExistingExe $existingExe `
                -RegistryDir $RegistryDir `
                -UninstallYes $UninstallYes `
                -NoModifyPath $NoModifyPath
        }
        finally {
            Remove-Item -LiteralPath $lockDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        return
    }

    $tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("hina-install-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null

    try {
        # --- purge -----------------------------------------------------------
        if ($action -eq 'purge') {
            Write-Host ""
            Write-Host "WARNING - clean reinstall will permanently delete:"
            Write-Host "  - $RegistryDir"
            Write-Host "      (registry.json, Apps\, descriptors\, pinned publisher keys)"
            Write-Host "  - $existingExe"
            Write-Host ""
            Write-Host "All previously installed Hina apps will become orphaned files"
            Write-Host "(Start Menu shortcuts, .cmd shims, fonts, HKCU MIME entries)"
            Write-Host "that you'll need to clean up manually."
            Write-Host ""

            $interactive = Test-Interactive
            if ($interactive -and -not $PurgeYes) {
                $confirm = Read-Prompt 'Type "yes" to confirm clean reinstall: '
                if ($confirm -ne 'yes') { Stop-Err 'aborted by user' }
            } elseif (-not $PurgeYes) {
                Stop-Err 'non-interactive purge requires HINA_PURGE_YES=1 as an explicit second gate'
            }

            # Get-Date -UFormat %s is locale-dependent on PS 5.1 (decimal
            # separator can be ','). Use UnixTimeSeconds directly to avoid parsing.
            $ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            if (Test-Path -LiteralPath $RegistryDir) {
                # Move LOCALAPPDATA\Hina to a sibling .purged folder, but skip
                # the bin/ subdir we just locked. Easier: move only registry.json
                # and Apps/, descriptors/ subfolders if they exist.
                foreach ($leaf in 'registry.json','Apps','descriptors') {
                    $p = Join-Path $RegistryDir $leaf
                    if (Test-Path -LiteralPath $p) {
                        $purged = "$p.purged-$ts"
                        Move-Item -LiteralPath $p -Destination $purged -Force
                        Remove-Item -LiteralPath $purged -Recurse -Force -ErrorAction SilentlyContinue
                        Write-Info "removed $p"
                    }
                }
            }
            if (Test-Path -LiteralPath $existingExe) {
                $purgedExe = "$existingExe.purged-$ts"
                Move-Item -LiteralPath $existingExe -Destination $purgedExe -Force
                Remove-Item -LiteralPath $purgedExe -Force -ErrorAction SilentlyContinue
                Write-Info "removed $existingExe"
            }
            $action = 'install'
        }

        # --- download + verify ----------------------------------------------
        $archivePath = Join-Path $tmpRoot $archiveName
        Write-Info "downloading $url"
        Invoke-Download -Url $url -OutFile $archivePath

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        if (-not (Test-Zip -Path $archivePath)) {
            Stop-Err 'downloaded archive is not a valid zip (likely truncated or corrupt)'
        }
        Test-ArchiveChecksum -Archive $archivePath -ShaUrl $shaUrl

        # --- extract ---------------------------------------------------------
        $stagingDir = Join-Path $tmpRoot 'staging'
        New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
        Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingDir -Force
        $newExe = Get-ChildItem -Path $stagingDir -Filter 'hina.exe' -Recurse -File | Select-Object -First 1
        if (-not $newExe) { Stop-Err "hina.exe not found inside $archiveName" }

        # --- atomic install (backup -> stage -> rename -> smoke -> drop) ----
        $backup = $null
        if (Test-Path -LiteralPath $existingExe) {
            $backup = "$existingExe.bak.$PID"
            Copy-Item -LiteralPath $existingExe -Destination $backup -Force
        }
        $staged = "$existingExe.new.$PID"
        Copy-Item -LiteralPath $newExe.FullName -Destination $staged -Force

        try {
            Move-Item -LiteralPath $staged -Destination $existingExe -Force
        } catch {
            Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
            if ($backup -and (Test-Path -LiteralPath $backup)) {
                Move-Item -LiteralPath $backup -Destination $existingExe -Force
            }
            Stop-Err "atomic rename failed: $($_.Exception.Message). The running hina.exe may be locked - close all hina.exe processes and retry."
        }

        # Smoke-test the freshly placed binary. Capture stderr so we can give
        # an actionable hint when the failure is a missing system DLL or wrong
        # architecture rather than a generic "smoke failed".
        $smokeErr = ''
        try {
            $smokeErr = & $existingExe --help 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) { throw "smoke test failed (exit=$LASTEXITCODE)" }
            $smokeErr = ''
        } catch {
            $hint = ''
            if ($smokeErr -match 'is not a valid Win32 application|0xC000007B') {
                $hint = ' - architecture mismatch (downloaded x64 on arm64 or vice-versa).'
            } elseif ($smokeErr -match 'was not found|missing|DLL') {
                $hint = ' - missing system DLL. Make sure your Windows 10/11 is up to date (Universal C Runtime).'
            }
            if ($backup -and (Test-Path -LiteralPath $backup)) {
                Move-Item -LiteralPath $backup -Destination $existingExe -Force
                if ($smokeErr) { Write-Host $smokeErr -ForegroundColor DarkGray }
                Stop-Err "installed binary failed smoke test; rolled back to previous version$hint"
            } else {
                if ($smokeErr) { Write-Host $smokeErr -ForegroundColor DarkGray }
                Stop-Err "installed binary failed smoke test and no backup to restore$hint"
            }
        }
        if ($backup -and (Test-Path -LiteralPath $backup)) { Remove-Item -LiteralPath $backup -Force }
        Write-Info "installed $existingExe"

        # --- PATH setup -----------------------------------------------------
        $userPath  = [Environment]::GetEnvironmentVariable('Path', 'User')
        $userParts = if ($userPath) { $userPath -split ';' | Where-Object { $_ -ne '' } } else { @() }
        $onPath    = $userParts -contains $Dest
        if (-not $onPath) {
            if ($NoModifyPath) {
                Write-Info "$Dest is not on user PATH. Add it manually via: setx PATH `"$Dest;%PATH%`""
            } else {
                $newPath = @($Dest) + ($userParts | Where-Object { $_ -ne $Dest })
                [Environment]::SetEnvironmentVariable('Path', ($newPath -join ';'), 'User')
                $env:Path = "$Dest;$env:Path"
                Write-Info "prepended $Dest to user PATH"
            }
        }

        try {
            $ver = & $existingExe --version 2>$null | Select-Object -First 1
            if ($LASTEXITCODE -eq 0 -and $ver) { Write-Info "verified: $ver" }
        } catch { }

        Write-Host ""
        Write-Host "Hina installed. Try:"
        Write-Host "    hina --help"
        Write-Host "    hina install <url-to-hina.app.json>"
        Write-Host ""
        Write-Host "Docs: https://github.com/$Repo#readme"
    }
    finally {
        Remove-Item -LiteralPath $lockDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-VerifyAction {
    param([string]$Dest, [string]$Tag, [string]$ArchiveUrl, [string]$ShaUrl, [string]$ArchiveName)

    function Write-Info($msg) { Write-Host "hina-install: $msg" }
    function Stop-Err($msg)   { Write-Host "hina-install: $msg" -ForegroundColor Red; exit 1 }

    $installedExe = Join-Path $Dest 'hina.exe'
    if (-not (Test-Path -LiteralPath $installedExe)) { Stop-Err "no installed binary at $installedExe to verify" }

    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("hina-verify-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        $archive = Join-Path $tmp $ArchiveName
        $partial = "$archive.partial"
        Write-Info "downloading release archive for $Tag ..."
        # See Invoke-Download in the main script: -OutFile with a Range header
        # overwrites instead of appending, so we delete the partial each retry
        # and re-download from scratch.
        for ($i = 1; $i -le 5; $i++) {
            try {
                if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
                Invoke-WebRequest -UseBasicParsing -Uri $ArchiveUrl -OutFile $partial -TimeoutSec 600
                Move-Item -LiteralPath $partial -Destination $archive -Force
                break
            } catch {
                Write-Info "download attempt $i failed: $($_.Exception.Message)"
                Start-Sleep -Seconds $i
            }
        }
        if (-not (Test-Path -LiteralPath $archive)) { Stop-Err 'download failed' }

        $shaFile = Join-Path $tmp 'sha256'
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $ShaUrl -OutFile $shaFile -TimeoutSec 60
        } catch {
            Stop-Err "no SHA-256 published for $Tag - cannot perform cryptographic integrity check. (Re-)install with HINA_ACTION=reinstall to repair if you suspect corruption."
        }
        $expectedArchive = ((Get-Content -LiteralPath $shaFile -TotalCount 1) -split '\s+')[0].ToLowerInvariant()
        $actualArchive   = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
        if ($expectedArchive -ne $actualArchive) {
            Stop-Err 'release archive SHA-256 mismatch (the published asset is corrupt or has been tampered with)'
        }

        $stagingDir = Join-Path $tmp 'staging'
        New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
        Expand-Archive -LiteralPath $archive -DestinationPath $stagingDir -Force
        $releaseExe = Get-ChildItem -Path $stagingDir -Filter 'hina.exe' -Recurse -File | Select-Object -First 1
        if (-not $releaseExe) { Stop-Err 'release archive does not contain hina.exe' }

        $releaseHash   = (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseExe.FullName).Hash.ToLowerInvariant()
        $installedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $installedExe).Hash.ToLowerInvariant()

        if ($releaseHash -eq $installedHash) {
            Write-Info "integrity OK: $installedExe matches release $Tag"
        } else {
            Write-Host ""
            Write-Host "Integrity check FAILED" -ForegroundColor Red
            Write-Host "  Release binary SHA-256:   $releaseHash"
            Write-Host "  Installed binary SHA-256: $installedHash"
            Write-Host ""
            Write-Host "The installed binary does not match the released $Tag artifact."
            Write-Host "Run installer with HINA_ACTION=reinstall to repair."
            exit 1
        }
    }
    finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-UninstallAction {
    param(
        [string]$Mode,
        [string]$Dest,
        [string]$ExistingExe,
        [string]$RegistryDir,
        [bool]$UninstallYes,
        [bool]$NoModifyPath
    )

    function Write-Info($msg) { Write-Host "hina-install: $msg" }
    function Stop-Err($msg)   { Write-Host "hina-install: $msg" -ForegroundColor Red; exit 1 }
    function Test-Interactive {
        try { return -not [Console]::IsInputRedirected -and $Host.UI.RawUI -ne $null } catch { return $false }
    }
    function Read-Prompt($prompt) { Write-Host -NoNewline $prompt; return [Console]::ReadLine() }

    # Strip the user-PATH entry we added on install, then update the current
    # session $env:Path to match. Idempotent: skips if entry absent.
    function Remove-PathStanza {
        param([string]$Target)
        if ($NoModifyPath) { return }
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (-not $userPath) { return }
        $parts = $userPath -split ';' | Where-Object { $_ -ne '' }
        if (-not ($parts -contains $Target)) { return }
        $clean = ($parts | Where-Object { $_ -ne $Target }) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $clean, 'User')
        $env:Path = (($env:Path -split ';' | Where-Object { $_ -ne $Target -and $_ -ne '' }) -join ';')
        Write-Info "removed PATH entry for $Target from user environment"
    }

    function Remove-BinaryAtomic {
        param([string]$Exe)
        if (Test-Path -LiteralPath $Exe) {
            $stamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            $moved = "$Exe.uninst-$stamp"
            try {
                Move-Item -LiteralPath $Exe -Destination $moved -Force
            } catch {
                Stop-Err "could not remove ${Exe}: $($_.Exception.Message). It may be running - close all hina.exe processes and retry."
            }
            Remove-Item -LiteralPath $moved -Force -ErrorAction SilentlyContinue
            Write-Info "removed $Exe"
        } else {
            Write-Info "binary already absent at $Exe"
        }
    }

    switch ($Mode) {
        'uninstall-full' {
            Write-Host ""
            Write-Host "WARNING - full uninstall will permanently delete:"
            Write-Host "  - $ExistingExe"
            Write-Host "  - All hina-managed apps (via 'hina uninstall <name>' per app)"
            Write-Host "  - $RegistryDir"
            Write-Host "      (registry.json, Apps\, descriptors\, pinned publisher keys)"
            Write-Host "  - PATH entry for $Dest in your user environment"
            Write-Host ""
        }
        'uninstall-configs' {
            Write-Host ""
            Write-Host "WARNING - this will permanently delete:"
            Write-Host "  - $ExistingExe"
            Write-Host "  - Configs at $RegistryDir"
            Write-Host "      (registry.json, descriptors\, pinned publisher keys)"
            Write-Host "  - PATH entry for $Dest in your user environment"
            Write-Host ""
            Write-Host "KEPT (left as orphan files on disk; hina will no longer manage them):"
            Write-Host "  - $RegistryDir\Apps\"
            Write-Host ""
        }
        'uninstall-binary' {
            Write-Host ""
            Write-Host "This will remove:"
            Write-Host "  - $ExistingExe"
            Write-Host ""
            Write-Host "KEPT untouched:"
            Write-Host "  - $RegistryDir (full registry + apps + keys preserved)"
            Write-Host "  - PATH entry in your user environment"
            Write-Host ""
            Write-Host "Reinstall hina later to resume the previous state."
            Write-Host ""
        }
    }

    $interactive = Test-Interactive
    switch ($Mode) {
        { $_ -in 'uninstall-full','uninstall-configs' } {
            if ($interactive -and -not $UninstallYes) {
                $confirm = Read-Prompt 'Type "yes" to confirm: '
                if ($confirm -ne 'yes') { Stop-Err 'aborted by user' }
            } elseif (-not $UninstallYes) {
                Stop-Err "non-interactive '$Mode' requires HINA_UNINSTALL_YES=1 as a second gate"
            }
        }
        'uninstall-binary' {
            if ($interactive) {
                $confirm = Read-Prompt 'Proceed? [y/N]: '
                if ($confirm -notmatch '^(y|Y|yes|YES)$') { Stop-Err 'aborted by user' }
            }
        }
    }

    switch ($Mode) {
        'uninstall-full' {
            # Per-app teardown via `hina uninstall` so side-effects (Start Menu
            # shortcuts, .cmd shims, HKCU MIME entries, fonts) are cleaned.
            # `hina list` outputs `NAME VERSION SOURCE` table, header on first line.
            if (Test-Path -LiteralPath $ExistingExe) {
                try {
                    $apps = & $ExistingExe list 2>$null | Select-Object -Skip 1 |
                        ForEach-Object { ($_ -split '\s+')[0] } |
                        Where-Object { $_ -and $_ -ne 'NAME' }
                    foreach ($app in $apps) {
                        Write-Info "uninstalling app: $app"
                        try { & $ExistingExe uninstall $app 2>$null | Out-Null } catch { }
                        if ($LASTEXITCODE -ne 0) {
                            Write-Info "  warning: 'hina uninstall $app' returned $LASTEXITCODE (continuing)"
                        }
                    }
                } catch {
                    Write-Info "could not enumerate apps via 'hina list' (continuing): $($_.Exception.Message)"
                }
            } else {
                Write-Info 'binary missing or not executable; skipping per-app teardown'
            }
            Remove-BinaryAtomic -Exe $ExistingExe
            if (Test-Path -LiteralPath $RegistryDir) {
                $stamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                $moved = "$RegistryDir.uninst-$stamp"
                try {
                    Move-Item -LiteralPath $RegistryDir -Destination $moved -Force
                    Remove-Item -LiteralPath $moved -Recurse -Force -ErrorAction SilentlyContinue
                    Write-Info "removed $RegistryDir"
                } catch {
                    Write-Info "warning: could not move $RegistryDir aside ($($_.Exception.Message)); attempting direct delete"
                    Remove-Item -LiteralPath $RegistryDir -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
            Remove-PathStanza -Target $Dest
            Write-Info 'uninstall (full) complete'
        }
        'uninstall-configs' {
            $regJson = Join-Path $RegistryDir 'registry.json'
            $descDir = Join-Path $RegistryDir 'descriptors'
            if (Test-Path -LiteralPath $regJson) {
                Remove-Item -LiteralPath $regJson -Force
                Write-Info "removed $regJson"
            }
            if (Test-Path -LiteralPath $descDir) {
                $stamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                $moved = "$descDir.uninst-$stamp"
                Move-Item -LiteralPath $descDir -Destination $moved -Force
                Remove-Item -LiteralPath $moved -Recurse -Force -ErrorAction SilentlyContinue
                Write-Info "removed $descDir"
            }
            Remove-BinaryAtomic -Exe $ExistingExe
            Remove-PathStanza -Target $Dest
            $appsDir = Join-Path $RegistryDir 'Apps'
            if (Test-Path -LiteralPath $appsDir) {
                Write-Info "Apps\ at $appsDir preserved as orphans (hina no longer tracks them)"
            }
            Write-Info 'uninstall (configs) complete'
        }
        'uninstall-binary' {
            Remove-BinaryAtomic -Exe $ExistingExe
            Write-Info "uninstall (binary only) complete; $RegistryDir preserved"
        }
    }
}

Invoke-Main
