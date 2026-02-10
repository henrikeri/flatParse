# FlatMaster Build Script
# Builds and optionally runs the FlatMaster application

param(
    [switch]$Clean,
    [switch]$Run,
    [switch]$Test,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "   FlatMaster Build Script" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# Check .NET SDK
Write-Host "Checking .NET SDK..." -ForegroundColor Yellow
try {
    $dotnetVersion = dotnet --version
    Write-Host "  ✓ .NET SDK $dotnetVersion found" -ForegroundColor Green
} catch {
    Write-Host "  ✗ .NET SDK not found!" -ForegroundColor Red
    Write-Host "  Please install .NET 8 SDK from https://dotnet.microsoft.com/download" -ForegroundColor Red
    exit 1
}

# Clean
if ($Clean) {
    Write-Host "`nCleaning solution..." -ForegroundColor Yellow
    dotnet clean --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ✗ Clean failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "  ✓ Clean complete" -ForegroundColor Green
}

# Restore
Write-Host "`nRestoring NuGet packages..." -ForegroundColor Yellow
dotnet restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ Restore failed" -ForegroundColor Red
    exit 1
}
Write-Host "  ✓ Restore complete" -ForegroundColor Green

# Build
Write-Host "`nBuilding solution ($Configuration)..." -ForegroundColor Yellow
dotnet build --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ Build failed" -ForegroundColor Red
    exit 1
}
Write-Host "  ✓ Build complete" -ForegroundColor Green

# Test
if ($Test) {
    Write-Host "`nRunning tests..." -ForegroundColor Yellow
    dotnet test --configuration $Configuration --no-build --verbosity normal
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ✗ Tests failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "  ✓ All tests passed" -ForegroundColor Green
}

# Run
if ($Run) {
    Write-Host "`nStarting FlatMaster..." -ForegroundColor Yellow
    dotnet run --project src/FlatMaster.WPF --configuration $Configuration --no-build
}

Write-Host "`n═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "   Build Complete!" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan

if (-not $Run) {
    Write-Host "`nTo run the application:" -ForegroundColor White
    Write-Host "  .\build.ps1 -Run" -ForegroundColor Cyan
    Write-Host "`nOr directly:" -ForegroundColor White
    Write-Host "  dotnet run --project src/FlatMaster.WPF" -ForegroundColor Cyan
}
