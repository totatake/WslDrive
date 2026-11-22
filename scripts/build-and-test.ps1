<#
.SYNOPSIS
    Builds and tests WslDrive.sln, with optional self-contained win-x64 single-file publishing.
.EXAMPLE
    .\scripts\build-and-test.ps1
    .\scripts\build-and-test.ps1 -Configuration Release -Publish
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot

try {
    Write-Host "==> Building WslDrive.sln ($Configuration)..." -ForegroundColor Cyan
    dotnet build WslDrive.sln -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "==> Running tests ($Configuration)..." -ForegroundColor Cyan
    dotnet test WslDrive.sln -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($Publish) {
        Write-Host "==> Publishing WslDrive.App (win-x64 framework-dependent)..." -ForegroundColor Cyan
        dotnet publish src/WslDrive.App/WslDrive.App.csproj -c $Configuration -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o publish/App
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        Write-Host "==> Publishing WslDrive.Cli (win-x64 framework-dependent)..." -ForegroundColor Cyan
        dotnet publish src/WslDrive.Cli/WslDrive.Cli.csproj -c $Configuration -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o publish/Cli
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    Write-Host "==> Done." -ForegroundColor Green
}
finally {
    Pop-Location
}
