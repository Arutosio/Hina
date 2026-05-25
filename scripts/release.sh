#!/usr/bin/env bash
# release.sh — build self-contained Hina CLI for every supported RID,
# wrap each in an OS-native archive with an install helper, and dump the
# whole set under dist/.
#
# Runs from any Linux or macOS host. Uses self-contained (non-AOT)
# publishing so cross-OS-family builds work without per-host toolchains.
# For NativeAOT-optimised releases, use the per-OS GitHub Actions matrix
# in .github/workflows/release.yml instead.
#
# Usage:
#   ./scripts/release.sh                  # build, test, package every RID
#   SKIP_TESTS=1 ./scripts/release.sh     # skip test pass (faster iteration)
#   RIDS="linux-x64 osx-arm64" ./scripts/release.sh   # subset

set -euo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
OUT_ROOT="${OUT_ROOT:-dist}"
PROJECT="Hina.CLI/Hina.CLI.csproj"
SKIP_TESTS="${SKIP_TESTS:-0}"

DEFAULT_RIDS=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
read -r -a RIDS <<<"${RIDS:-${DEFAULT_RIDS[@]}}"

cd "$(dirname "$0")/.."

if [[ "$SKIP_TESTS" != "1" ]]; then
    echo "==> dotnet test (set SKIP_TESTS=1 to skip)"
    dotnet test Hina.sln -c "$CONFIGURATION" --nologo
fi

rm -rf "$OUT_ROOT"
mkdir -p "$OUT_ROOT"

# --- per-platform install helper templates -------------------------------

read -r -d '' INSTALL_SH <<'EOF' || true
#!/bin/sh
# Hina installer. Copies the bundled `hina` binary into $HOME/.local/bin so
# it's reachable from the shell PATH (most distros put ~/.local/bin on PATH
# by default; if yours doesn't, add it).
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
EOF

read -r -d '' INSTALL_BAT <<'EOF' || true
@echo off
REM Hina installer. Copies hina.exe into %LOCALAPPDATA%\Hina\bin and reminds
REM you to add that directory to your user PATH.
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
EOF

read -r -d '' README_TXT <<'EOF' || true
Hina — cross-platform package manager

This archive contains a single self-contained binary. Run the included
installer or copy the binary somewhere on your PATH.

  Linux / macOS:  ./install.sh
  Windows:        install.bat

After install, get started with:
  hina --help
  hina install <url-to-hina.app.json>

See https://github.com/Arutosio/Hina for documentation.
EOF

# --- per-RID publish + package ------------------------------------------

publish_one() {
    local rid="$1"
    local stage_dir="$OUT_ROOT/stage/Hina-$rid"

    echo
    echo "==> Publishing Hina.CLI for $rid"

    rm -rf "$stage_dir"
    mkdir -p "$stage_dir"

    # Cross-OS-family RIDs cannot AOT-compile — force PublishAot=false so the
    # script works from any host. NativeAOT-optimised binaries are built per
    # OS in the GitHub Actions release matrix instead.
    dotnet publish "$PROJECT" \
        -c "$CONFIGURATION" \
        -r "$rid" \
        --self-contained \
        -p:PublishAot=false \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=None \
        -p:DebugSymbols=false \
        -o "$stage_dir" \
        --nologo \
        >/dev/null

    # Trim publish output to just the binary + license + native libs the
    # self-extract host needs; drop debug symbols and other noise.
    find "$stage_dir" -type f \( -name '*.pdb' -o -name '*.xml' -o -name '*.json' \) -delete

    # Rename the published binary so users see `hina` / `hina.exe` rather
    # than `Hina.CLI`. (Project AssemblyName stays the C# convention.)
    if [[ "$rid" == win-* ]]; then
        [[ -f "$stage_dir/Hina.CLI.exe" ]] && mv "$stage_dir/Hina.CLI.exe" "$stage_dir/hina.exe"
    else
        [[ -f "$stage_dir/Hina.CLI" ]] && mv "$stage_dir/Hina.CLI" "$stage_dir/hina"
        chmod +x "$stage_dir/hina"
    fi

    # Drop platform-appropriate helpers + readme alongside the binary.
    echo "$README_TXT" > "$stage_dir/README.txt"
    if [[ "$rid" == win-* ]]; then
        printf '%s' "$INSTALL_BAT" > "$stage_dir/install.bat"
    else
        printf '%s\n' "$INSTALL_SH" > "$stage_dir/install.sh"
        chmod +x "$stage_dir/install.sh"
    fi

    # Final archive.
    local archive_base="Hina-$(rid_to_friendly "$rid")"
    if [[ "$rid" == win-* ]]; then
        (cd "$OUT_ROOT/stage" && zip -qr "../$archive_base.zip" "Hina-$rid")
        echo "    wrote $OUT_ROOT/$archive_base.zip"
    else
        tar -C "$OUT_ROOT/stage" -czf "$OUT_ROOT/$archive_base.tar.gz" "Hina-$rid"
        echo "    wrote $OUT_ROOT/$archive_base.tar.gz"
    fi
}

# Map technical RID -> human-friendly archive suffix (windows/linux/macos).
rid_to_friendly() {
    case "$1" in
        win-x64)     echo "windows-x64" ;;
        win-arm64)   echo "windows-arm64" ;;
        linux-x64)   echo "linux-x64" ;;
        linux-arm64) echo "linux-arm64" ;;
        osx-x64)     echo "macos-x64" ;;
        osx-arm64)   echo "macos-arm64" ;;
        *)           echo "$1" ;;
    esac
}

for rid in "${RIDS[@]}"; do
    publish_one "$rid"
done

# Tidy staging area; keep only the finished archives.
rm -rf "$OUT_ROOT/stage"

# Emit SHA-256 companion files. install.sh / install.ps1 download these
# alongside the archive and abort on mismatch (covers truncation + tampering).
echo
echo "==> Computing SHA-256 sums"
sha_cmd="sha256sum"
command -v sha256sum >/dev/null 2>&1 || sha_cmd="shasum -a 256"
for f in "$OUT_ROOT"/Hina-*.tar.gz "$OUT_ROOT"/Hina-*.zip; do
    [ -f "$f" ] || continue
    (cd "$OUT_ROOT" && $sha_cmd "$(basename "$f")" > "$(basename "$f").sha256")
    echo "    $(cat "$f.sha256")"
done

echo
echo "==> Done. Artifacts in $OUT_ROOT/:"
ls -lh "$OUT_ROOT"
