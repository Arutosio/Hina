#!/usr/bin/env bash
# Publish the Hina CLI as a NativeAOT single-file binary for the current host's
# native runtime identifier(s). NativeAOT cannot cross-compile across OS families,
# so this script only emits artifacts the host can actually link.
#
# Linux host  -> linux-x64, linux-arm64 (if cross-toolchain installed)
# macOS host  -> osx-x64, osx-arm64
# Windows host -> use scripts/publish-cli.ps1 instead.
#
# Usage:
#   ./scripts/publish-cli.sh
#   CONFIGURATION=Debug ./scripts/publish-cli.sh
#   OUTPUT_ROOT=./out ./scripts/publish-cli.sh

set -euo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
OUTPUT_ROOT="${OUTPUT_ROOT:-artifacts/publish}"
PROJECT="Hina.CLI/Hina.CLI.csproj"

unameOut="$(uname -s)"
case "${unameOut}" in
    Linux*)   rids=("linux-x64") ;;
    Darwin*)  rids=("osx-x64" "osx-arm64") ;;
    *)        echo "Unsupported host OS: ${unameOut}. Use publish-cli.ps1 on Windows." >&2; exit 1 ;;
esac

# macOS NativeAOT needs Homebrew openssl@3 and brotli on the linker path.
# The csproj already wires both prefixes via conditional LinkerArg, but warn early
# if they're missing so the build doesn't fail deep in clang.
if [[ "$unameOut" == Darwin* ]]; then
    for lib in openssl@3 brotli; do
        if ! brew --prefix "$lib" > /dev/null 2>&1; then
            echo "warning: '$lib' not installed via brew. Run: brew install openssl@3 brotli" >&2
        fi
    done
fi

for rid in "${rids[@]}"; do
    out="${OUTPUT_ROOT}/${rid}"
    echo "==> Publishing ${PROJECT} for ${rid} -> ${out}"
    dotnet publish "${PROJECT}" -c "${CONFIGURATION}" -r "${rid}" --self-contained -o "${out}"
done

echo
echo "Published:"
for rid in "${rids[@]}"; do
    echo "  ${OUTPUT_ROOT}/${rid}"
done
