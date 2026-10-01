<#
.SYNOPSIS
    Dev shortcut: publish Center and swap it into the installed Velopack copy, no feed, no setup.

.DESCRIPTION
    For testing a Center change on this machine in seconds instead of a Velopack + Inno round:
      1. dotnet publish (same command Build-Velopack.ps1 runs),
      2. stop the running Center,
      3. copy the published exe over %LOCALAPPDATA%\ClawTweaksCenter\current\CTW_Center.exe,
      4. start Center again through the Velopack stub.

    Velopack's own bookkeeping (current\sq.version, packages\) is left alone, so the installed
    version number stays the one of the last real Velopack install. That is fine for a dev test and
    wrong for anything shipped - a release still goes through Build-Velopack.ps1.

.PARAMETER NoStart
    Swap the exe but do not start Center afterwards.

.PARAMETER Configuration
    Build configuration, default Release.
#>
param(
    [switch]$NoStart,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'ClawTweaksCenter.csproj'
[xml]$x = Get-Content $proj
$version = ($x.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)

$root = Join-Path $env:LOCALAPPDATA 'ClawTweaksCenter'
$target = Join-Path $root 'current\CTW_Center.exe'
if (-not (Test-Path $target)) { throw "No Velopack install of Center found at $target - install once with the setup first." }

Write-Host ">> Publishing Center $version ($Configuration)..." -ForegroundColor Gray
& dotnet publish $proj -c $Configuration | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$published = Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows\win-x64\publish\CTW_Center_${version}_Setup.exe"
if (-not (Test-Path $published)) { throw "Published exe not found: $published" }

Write-Host ">> Stopping Center..." -ForegroundColor Gray
Get-Process CTW_Center -ErrorAction SilentlyContinue | Stop-Process -Force
# The image stays mapped for a moment after the kill request returns.
for ($i = 0; $i -lt 20 -and (Get-Process CTW_Center -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }

Copy-Item $published $target -Force
$len = (Get-Item $target).Length
Write-Host ">> Installed $version into $target ($([math]::Round($len / 1MB, 1)) MB)" -ForegroundColor Green

if (-not $NoStart) {
    $stub = Join-Path $root 'CTW_Center.exe'
    if (-not (Test-Path $stub)) { $stub = $target }
    Start-Process $stub
    Write-Host ">> Center started." -ForegroundColor Green
}
