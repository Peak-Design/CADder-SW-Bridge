<#
.SYNOPSIS
Builds, tests, packages and tags a release of the SolidWorks add-in.

.DESCRIPTION
One step from a clean tree to a release folder:

  1. refuses a dirty git tree and a running SolidWorks
  2. reads the version from the csproj (the one source of the number)
  3. builds Release and runs the tests
  4. zips the DLL, icons and licence texts into dist\
  5. compiles the installer when Inno Setup (ISCC.exe) is on this machine
  6. tags sw-v<version> (with -Tag)

Nothing is pushed. Push the tag yourself after you have installed from the
built installer and exported a corpus assembly, as the release checklist in
.claude\RELEASING.md asks.

.PARAMETER Tag
Create the git tag after a successful build.

.PARAMETER SkipTests
Do not run the test project. For a rebuild of an already tested commit.

.PARAMETER AllowDirty
Build from a tree with uncommitted changes. Never for a real release.
#>
[CmdletBinding()]
param(
    [switch]$Tag,
    [switch]$SkipTests,
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$repo = Resolve-Path (Join-Path $root "..")
$csproj = Join-Path $root "src\Peak.Cadder\Peak.Cadder.csproj"
$tests = Join-Path $root "tests\Peak.Cadder.Tests"
$dist = Join-Path $root "dist"

function Fail($message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

# ── Preconditions ──────────────────────────────────────────────────────
if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) {
    Fail "SolidWorks is running and holds the add-in DLL open. Close it first."
}

$dirty = git -C $repo status --porcelain
if ($dirty -and -not $AllowDirty) {
    Write-Host $dirty
    Fail "the tree has uncommitted changes. Commit them, or pass -AllowDirty for a test build."
}

[xml]$project = Get-Content $csproj
$version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { Fail "no <Version> in $csproj" }
Write-Host "CADder Bridge $version"

if ($Tag -and (git -C $repo tag --list "sw-v$version")) {
    Fail "tag sw-v$version exists. Bump <Version> in the csproj first."
}

# ── Copy check ─────────────────────────────────────────────────────────
# Release notes, the README and every string the add-in shows go out with
# this build, so the punctuation check runs before anything is packed.
$check = Join-Path $root "tools\Check-Copy.py"
if (Test-Path $check) {
    python $check $repo
    if ($LASTEXITCODE -ne 0) { Fail "the copy check found em dashes or client names" }
}

# ── Build and test ─────────────────────────────────────────────────────
dotnet build $csproj -c Release -v q
if ($LASTEXITCODE -ne 0) { Fail "build failed" }

if (-not $SkipTests) {
    dotnet test $tests -v q
    if ($LASTEXITCODE -ne 0) { Fail "tests failed" }
}

$bin = Join-Path $root "src\Peak.Cadder\bin\Release"
$dll = Join-Path $bin "Peak.Cadder.dll"
if (-not (Test-Path $dll)) { Fail "$dll was not built" }

$built = (Get-Item $dll).VersionInfo.ProductVersion -replace "\+.*$", ""
if ($built -ne $version) {
    Fail "the built DLL says $built, the csproj says $version"
}

# ── Package ────────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force $dist | Out-Null
$stage = Join-Path $dist "CADder-Bridge-$version"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item $dll $stage
$icons = Join-Path $bin "icons"
if (Test-Path $icons) { Copy-Item $icons (Join-Path $stage "icons") -Recurse }
Copy-Item (Join-Path $root "LICENSE") $stage
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") $stage
Copy-Item (Join-Path $root "README.md") $stage
Copy-Item (Join-Path $root "src\Peak.Cadder\Register-Addin.bat") $stage

$zip = "$stage.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Write-Host "zip: $zip"

# ── Installer ──────────────────────────────────────────────────────────
# Inno Setup installs for the machine or for one user, and winget picks the
# second, so look in both places and in the key the installer writes.
$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
foreach ($key in @(
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
    "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1")) {
    $where = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
    if ($where) { $isccCandidates += (Join-Path $where "ISCC.exe") }
}
$isccCandidates += (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($iscc) {
    & $iscc "/DVersion=$version" (Join-Path $root "installer\CADder-Bridge.iss")
    if ($LASTEXITCODE -ne 0) { Fail "the installer did not compile" }
    Write-Host "installer: $(Join-Path $dist "CADder-Bridge-$version-setup.exe")"
} else {
    Write-Host "Inno Setup is not installed. No installer built (the zip plus Register-Addin.bat still works)." -ForegroundColor Yellow
}

# ── Tag ────────────────────────────────────────────────────────────────
if ($Tag) {
    git -C $repo tag "sw-v$version"
    if ($LASTEXITCODE -ne 0) { Fail "git tag failed" }
    Write-Host "tagged sw-v$version. Push with: git push --tags"
}
