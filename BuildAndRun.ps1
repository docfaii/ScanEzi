# ScanEzi - Build and Run Script
# Run this script after installing .NET 8 SDK and Windows App SDK

# Check for .NET SDK
$dotnetPath = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetPath) {
    Write-Host "ERROR: .NET SDK not found. Please install .NET 8 SDK from https://dotnet.microsoft.com/download" -ForegroundColor Red
    Write-Host "You need: .NET 8.0 SDK (LTS)" -ForegroundColor Yellow
    exit 1
}

Write-Host "Found .NET SDK: $($dotnetPath.Source)" -ForegroundColor Green

# Set working directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# Restore packages
Write-Host "Restoring NuGet packages..." -ForegroundColor Cyan
dotnet restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Package restore failed" -ForegroundColor Red
    exit 1
}

# Build
Write-Host "Building ScanEzi..." -ForegroundColor Cyan
dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed" -ForegroundColor Red
    exit 1
}

# Run
Write-Host "Running ScanEzi..." -ForegroundColor Cyan
dotnet run -c Release