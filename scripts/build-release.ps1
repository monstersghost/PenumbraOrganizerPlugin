# Builds the plugin's release zip, matching the exact structure GitHub releases have always
# shipped (the SDK's own auto-generated zip omits images/icon.png, which past releases included
# manually - this script closes that gap so it's no longer a manual step).
#
# Usage: pwsh scripts/build-release.ps1
# Output: artifacts/release/PenumbraOrganizer.Plugin.zip, ready for `gh release create <version> <zip>`.
# Version is read from the csproj, not passed in - bump <Version> there (and repo.json) first.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'PenumbraOrganizer.Plugin.sln'
$pluginProject = Join-Path $root 'PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.csproj'
$releaseBinDir = Join-Path $root 'PenumbraOrganizer.Plugin\bin\Release'
$sdkZipPath = Join-Path $releaseBinDir 'PenumbraOrganizer.Plugin\latest.zip'
$iconPath = Join-Path $releaseBinDir 'images\icon.png'
$artifactsDir = Join-Path $root 'artifacts\release'
$finalZipPath = Join-Path $artifactsDir 'PenumbraOrganizer.Plugin.zip'

[xml]$csprojXml = Get-Content $pluginProject
$version = $csprojXml.Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) {
    throw "Could not read <Version> from $pluginProject."
}
Write-Host "Building release for version $version..."

Write-Host "Running tests..."
dotnet test $solution
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed - fix them before building a release."
}

Write-Host "Building Release configuration..."
dotnet build $pluginProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed."
}

if (-not (Test-Path $sdkZipPath)) {
    throw "Expected the SDK-generated zip at $sdkZipPath but it wasn't there. Did the Dalamud.NET.Sdk packaging step change?"
}
if (-not (Test-Path $iconPath)) {
    throw "Expected the plugin icon at $iconPath but it wasn't there."
}

if (Test-Path $artifactsDir) {
    Remove-Item -Recurse -Force $artifactsDir
}
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
Copy-Item $sdkZipPath $finalZipPath

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($finalZipPath, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $iconPath, 'images/icon.png') | Out-Null
}
finally {
    $zip.Dispose()
}

# Confirm the icon actually landed inside the zip rather than trusting the write silently worked.
$verifyZip = [System.IO.Compression.ZipFile]::OpenRead($finalZipPath)
$hasIcon = $null -ne ($verifyZip.Entries | Where-Object { $_.FullName -eq 'images/icon.png' })
$verifyZip.Dispose()
if (-not $hasIcon) {
    throw "images/icon.png did not end up in the final zip - packaging step failed silently."
}

Write-Host "Release package created:"
Write-Host "  $finalZipPath"
Write-Host ""
Write-Host "Next steps (not run by this script):"
Write-Host "  1. Confirm <Version> in PenumbraOrganizer.Plugin.csproj and AssemblyVersion/DownloadLink* in repo.json all match: $version"
Write-Host "  2. git push origin main"
Write-Host "  3. gh release create $version `"$finalZipPath`" --title `"v$version`" --notes-file <path-to-notes>"
