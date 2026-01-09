param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts/publish"
)

$ErrorActionPreference = "Stop"

$winOut = Join-Path $OutputRoot "win-x64"
$linuxOut = Join-Path $OutputRoot "linux-x64"

dotnet publish "Hina.CLI/Hina.CLI.csproj" -c $Configuration -r win-x64 -o $winOut
dotnet publish "Hina.CLI/Hina.CLI.csproj" -c $Configuration -r linux-x64 -o $linuxOut

Write-Host "Published:"
Write-Host "  $winOut"
Write-Host "  $linuxOut"
