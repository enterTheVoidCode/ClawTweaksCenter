<#
.SYNOPSIS
    Publishes Center and packs it into a Velopack release feed.

.DESCRIPTION
    The Velopack half of the build, kept in its OWN file on purpose: the updater is meant to be
    removable in three steps (Update\REMOVAL.md), and a packing step welded into Build-Setup.ps1
    would have to be unpicked by hand. Delete this file and nothing else changes.

    Output, all under -OutputDir (default: PortableExe\velopack):

        ClawTweaksCenter-<ver>-full.nupkg     the payload an existing install updates to
        ClawTweaksCenter-<ver>-delta.nupkg    only from the second version on
        ClawTweaksCenter-win-Setup.exe        what the Inno installer ships and runs
        releases.win.json / RELEASES          the feed index UpdateManager reads

    Point VelopackFeedOverride at -OutputDir to rehearse an update locally, with no GitHub release
    involved. See Update\REMOVAL.md.

.PARAMETER OutputDir
    Where the feed goes. Reused across versions ON PURPOSE - Velopack builds a delta against the
    packages already there, and a cleaned folder silently produces full-only releases.
#>
param(
    [string] $OutputDir = $null,
    [string] $Configuration = 'Release',
    # Repack a version that is already in the feed.
    #
    # vpk refuses it otherwise - "There is a release in channel win which is equal or greater to the
    # current version" - which is right for a release feed and in the way while developing, where the
    # same version gets packed again after changing a pack option. It removes only that version's own
    # packages, never the older ones the delta is built against.
    [switch] $Replace
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'ClawTweaksCenter.csproj'
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot 'PortableExe\velopack' }

# --- the version, read from the csproj so there is ONE source ---------------------------------------
# Velopack refuses anything but 3-part SemVer2 ("it must be a 3-part SemVer2 compliant version
# string", measured 2026-09-03). The csproj carries a 3-part <Version> for exactly this reason; if
# somebody widens it back to four parts, this stops the build instead of packing a version that
# disagrees with what Center shows the user.
[xml] $csproj = Get-Content $proj
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version in the csproj is '$version'. Velopack needs a 3-part SemVer2 version (x.y.z)."
}

# --- vpk has to be there ----------------------------------------------------------------------------
$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    $fallback = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
    if (Test-Path $fallback) { $vpk = $fallback } else {
        throw "vpk not found. Install it once:  dotnet tool install -g vpk"
    }
} else { $vpk = $vpk.Source }

Write-Host ""
Write-Host "  Velopack release  $version" -ForegroundColor Cyan
Write-Host ""

# --- publish ----------------------------------------------------------------------------------------
Write-Host ">> Publishing..." -ForegroundColor Gray
& dotnet publish $proj -c $Configuration | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$publishDir = Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows\win-x64\publish"
$publishedExe = Join-Path $publishDir "CTW_Center_${version}_Setup.exe"
if (-not (Test-Path $publishedExe)) { throw "Published exe not found: $publishedExe" }

# --- stage under the INSTALLED name -----------------------------------------------------------------
# Velopack's --mainExe is a FIXED name across every version; the distributed filename carries the
# version (CTW_Center_<ver>_Setup.exe), so packing that name would give version N a stub pointing at
# a file that version N+1 no longer has. CTW_Center.exe is what SelfInstaller installs as, so the
# installed name was already fixed - only the distributed one is not.
$stage = Join-Path $PSScriptRoot 'bin\velopack-stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item $publishedExe (Join-Path $stage 'CTW_Center.exe')

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if ($Replace) {
    $stale = Get-ChildItem $OutputDir -Filter "*-$version-*.nupkg" -File -ErrorAction SilentlyContinue
    foreach ($f in $stale) {
        Write-Host ">> Replacing existing release $($f.Name)" -ForegroundColor DarkYellow
        Remove-Item $f.FullName -Force
    }
}

# --- pack -------------------------------------------------------------------------------------------
# --packId ClawTweaksCenter is NOT cosmetic. It decides two things at once:
#   the install folder      %LOCALAPPDATA%\ClawTweaksCenter
#   the HKCU uninstall key  ...\Uninstall\ClawTweaksCenter
# and that second one is the key the HELPER reads to find Center (DisplayIcon / InstallLocation).
# Measured 2026-09-03: Velopack fills both, so the helper resolves a Velopack-installed Center with
# no change at all.
#
# WARNING, from the same measurement: it is ALSO the key SelfInstaller writes. The two share it.
# Uninstalling either one removes the whole key - so a migration has to remove the old entry BEFORE
# Velopack writes its own, never after.
#
# --shortcuts / --packTitle: Velopack has to create them, because SelfInstaller no longer runs on a
# Velopack install - IsRunningFromInstallDir accepts that layout, so Center never reaches its
# install-self path and never writes a shortcut. Packing with "None" (the first attempt here) left
# the user with no Start Menu and no Desktop entry at all after the migration.
#
# Desktop,StartMenuRoot matches what the classic install created, and --packTitle makes the file
# name match too ("ClawTweaks Center.lnk") - so Velopack's shortcut OVERWRITES the old one instead of
# sitting beside it under a second name.
Write-Host ">> Packing..." -ForegroundColor Gray
& $vpk pack `
    --packId ClawTweaksCenter `
    --packVersion $version `
    --packDir $stage `
    --mainExe CTW_Center.exe `
    --outputDir $OutputDir `
    --packTitle 'ClawTweaks Center' `
    --packAuthors 'ClawTweaks' `
    --shortcuts Desktop,StartMenuRoot
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

$setup = Join-Path $OutputDir 'ClawTweaksCenter-win-Setup.exe'
if (-not (Test-Path $setup)) { throw "vpk produced no Setup.exe in $OutputDir" }

Write-Host ""
Write-Host ("  Feed     : {0}" -f $OutputDir) -ForegroundColor Green
Write-Host ("  Setup    : {0} ({1:N1} MB)" -f (Split-Path -Leaf $setup), ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
Write-Host ("  Packages : {0}" -f (((Get-ChildItem $OutputDir -Filter '*.nupkg').Name) -join ', ')) -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Rehearse an update locally:" -ForegroundColor Gray
Write-Host "    reg add HKCU\Software\ClawTweaks\Center /v VelopackUpdates /t REG_DWORD /d 1 /f" -ForegroundColor DarkGray
Write-Host "    reg add HKCU\Software\ClawTweaks\Center /v VelopackFeedOverride /d `"$OutputDir`" /f" -ForegroundColor DarkGray
Write-Host ""
