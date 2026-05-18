param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts/publish"
)

$ErrorActionPreference = "Stop"

# Hina.CLI is NativeAOT — only RIDs the host can natively link are built here.
# Use scripts/publish-cli.sh on Linux/macOS hosts.
$rids = @("win-x64", "win-arm64")

foreach ($rid in $rids) {
    $out = Join-Path $OutputRoot $rid
    Write-Host "==> Publishing Hina.CLI for $rid -> $out"
    dotnet publish "Hina.CLI/Hina.CLI.csproj" -c $Configuration -r $rid --self-contained -o $out
}

Write-Host ""
Write-Host "Published:"
foreach ($rid in $rids) {
    Write-Host "  $(Join-Path $OutputRoot $rid)"
}
