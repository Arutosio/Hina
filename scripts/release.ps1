param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "dist",
    [switch]$SkipTests,
    [string[]]$Rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
)

# release.ps1 — Windows mirror of scripts/release.sh. Produces the same
# six archives (Hina-windows-*.zip + Hina-linux-*.tar.gz + Hina-macos-*.tar.gz)
# from a Windows host. Uses self-contained non-AOT publish so cross-OS-family
# builds work from any host; per-OS AOT happens in the CI matrix.

$ErrorActionPreference = "Stop"
$project = "Hina.CLI/Hina.CLI.csproj"

Set-Location (Join-Path $PSScriptRoot "..")

if (-not $SkipTests) {
    Write-Host "==> dotnet test (use -SkipTests to skip)"
    dotnet test Hina.sln -c $Configuration --nologo
}

if (Test-Path $OutputRoot) { Remove-Item -Recurse -Force $OutputRoot }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$installSh = @'
#!/bin/sh
set -e
DEST="${HOME}/.local/bin"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$DEST"
cp "$SCRIPT_DIR/hina" "$DEST/hina"
chmod +x "$DEST/hina"
echo "Hina installed to $DEST/hina"
case ":$PATH:" in
    *":$DEST:"*) ;;
    *) echo "Add $DEST to your PATH to invoke 'hina' from anywhere." ;;
esac
'@

$installBat = @'
@echo off
setlocal
set "DEST=%LOCALAPPDATA%\Hina\bin"
if not exist "%DEST%" mkdir "%DEST%"
copy /Y "%~dp0hina.exe" "%DEST%\hina.exe" >NUL
echo Hina installed to %DEST%\hina.exe
echo.
echo If %DEST% is not on your user PATH, add it via:
echo   setx PATH "%%PATH%%;%DEST%"
echo (open a new terminal afterwards for the change to take effect)
endlocal
'@

$readme = @'
Hina — cross-platform package manager

This archive contains a single self-contained binary. Run the included
installer or copy the binary somewhere on your PATH.

  Linux / macOS:  ./install.sh
  Windows:        install.bat

After install:
  hina --help
  hina install <url-to-hina.app.json>

See https://github.com/Arutosio/Hina for documentation.
'@

function Get-Friendly($rid) {
    switch ($rid) {
        "win-x64"     { "windows-x64" }
        "win-arm64"   { "windows-arm64" }
        "linux-x64"   { "linux-x64" }
        "linux-arm64" { "linux-arm64" }
        "osx-x64"     { "macos-x64" }
        "osx-arm64"   { "macos-arm64" }
        default       { $rid }
    }
}

foreach ($rid in $Rids) {
    $stage = Join-Path $OutputRoot "stage/Hina-$rid"
    Write-Host ""
    Write-Host "==> Publishing Hina.CLI for $rid"

    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    dotnet publish $project -c $Configuration -r $rid `
        --self-contained `
        -p:PublishAot=false `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $stage `
        --nologo | Out-Null

    Get-ChildItem -Path $stage -Include *.pdb,*.xml,*.json -File -Recurse | Remove-Item -Force

    # Rename the published binary so users see `hina` / `hina.exe`.
    if ($rid.StartsWith("win-")) {
        $src = Join-Path $stage "Hina.CLI.exe"
        if (Test-Path $src) { Move-Item $src (Join-Path $stage "hina.exe") -Force }
    } else {
        $src = Join-Path $stage "Hina.CLI"
        if (Test-Path $src) { Move-Item $src (Join-Path $stage "hina") -Force }
    }

    Set-Content -Path (Join-Path $stage "README.txt") -Value $readme -NoNewline
    if ($rid.StartsWith("win-")) {
        Set-Content -Path (Join-Path $stage "install.bat") -Value $installBat -NoNewline
    } else {
        Set-Content -Path (Join-Path $stage "install.sh") -Value $installSh -NoNewline -Encoding UTF8
    }

    $friendly = Get-Friendly $rid
    if ($rid.StartsWith("win-")) {
        $archive = Join-Path $OutputRoot "Hina-$friendly.zip"
        Compress-Archive -Path "$stage" -DestinationPath $archive -Force
        Write-Host "    wrote $archive"
    } else {
        $archive = Join-Path $OutputRoot "Hina-$friendly.tar.gz"
        tar -C (Join-Path $OutputRoot "stage") -czf $archive "Hina-$rid"
        Write-Host "    wrote $archive"
    }
}

Remove-Item -Recurse -Force (Join-Path $OutputRoot "stage")

# SHA-256 companion files. install.sh / install.ps1 verify these on download
# and refuse to extract on mismatch (covers truncation + tampering).
Write-Host ""
Write-Host "==> Computing SHA-256 sums"
Get-ChildItem $OutputRoot -Include Hina-*.tar.gz, Hina-*.zip -File | ForEach-Object {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
    $line = "$hash  $($_.Name)"
    Set-Content -LiteralPath "$($_.FullName).sha256" -Value $line -Encoding ascii -NoNewline
    Write-Host "    $line"
}

Write-Host ""
Write-Host "==> Done. Artifacts in $OutputRoot/:"
Get-ChildItem $OutputRoot | Format-Table Name, Length
