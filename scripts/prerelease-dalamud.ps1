# Tests the plugin against a pre-release Dalamud build while XIVLauncher still has Dalamud turned
# off after a game patch. Everything runs from a separate test profile, so your real XIVLauncher
# install, Dalamud config and installed plugins stay untouched.
#
# Only use a Dalamud branch that has already been updated for the new game version (check its
# recent commits for a ClientStructs update). An outdated Dalamud injected into a patched game
# crashes or misbehaves. That is exactly why XIVLauncher turns it off.
#
# Usage (run each step in order, from the repo root):
#   pwsh scripts/prerelease-dalamud.ps1 -Action Install                  # get Dalamud (CI build via gh)
#   pwsh scripts/prerelease-dalamud.ps1 -Action Install -ArtifactZip C:\path\dalamud-artifact.zip
#   pwsh scripts/prerelease-dalamud.ps1 -Action Install -BuildFromSource # needs VS 2026 C++ + CMake
#   pwsh scripts/prerelease-dalamud.ps1 -Action BuildPlugin              # build + test against it
#   pwsh scripts/prerelease-dalamud.ps1 -Action Inject                   # game must be running
#   pwsh scripts/prerelease-dalamud.ps1 -Action Status
#
# Layout (under %APPDATA%\XIVLauncher\prerelease-test):
#   dalamud\            the pre-release Dalamud build (also DALAMUD_HOME for plugin builds)
#   dalamudConfig.json  test copy of your Dalamud config (created from the real one on first inject)
#   installedPlugins\   empty test plugin folder, so API-incompatible installed plugins aren't touched
#   src\Dalamud\        source checkout (only with -BuildFromSource)

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'BuildPlugin', 'Inject', 'Status')]
    [string]$Action,

    # goatcorp/Dalamud branch. master = the current API level with the latest ClientStructs update,
    # which is what patch-day fixes land on. API-bump branches (e.g. api16-rollup) are for expansions.
    [string]$Branch = 'master',

    # A dalamud-artifact.zip downloaded by hand from the goatcorp/Dalamud Actions page.
    [string]$ArtifactZip,

    # Build Dalamud locally instead of downloading the CI artifact.
    [switch]$BuildFromSource,

    # Game client language passed to Dalamud on inject.
    [ValidateSet('english', 'japanese', 'german', 'french')]
    [string]$Language = 'english'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $root 'PenumbraOrganizer.Plugin\PenumbraOrganizer.Plugin.csproj'
$solution = Join-Path $root 'PenumbraOrganizer.Plugin.sln'
$pluginOutDir = Join-Path $root 'artifacts\prerelease\PenumbraOrganizer.Plugin'

$xlDir = Join-Path $env:APPDATA 'XIVLauncher'
$testRoot = Join-Path $xlDir 'prerelease-test'
$dalamudDir = Join-Path $testRoot 'dalamud'
$testConfig = Join-Path $testRoot 'dalamudConfig.json'
$testPlugins = Join-Path $testRoot 'installedPlugins'
$assetDir = Join-Path $xlDir 'dalamudAssets\dev'
$infoFile = Join-Path $dalamudDir 'prerelease-source.txt'

function Require-Command([string]$name, [string]$hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "'$name' was not found on PATH. $hint"
    }
}

function Get-DalamudVersion {
    $dll = Join-Path $dalamudDir 'Dalamud.dll'
    if (-not (Test-Path $dll)) {
        throw "No pre-release Dalamud at $dalamudDir. Run -Action Install first."
    }
    return [System.Reflection.AssemblyName]::GetAssemblyName($dll).Version
}

function Install-FromFolder([string]$source, [string]$description) {
    $dll = Get-ChildItem -Path $source -Filter 'Dalamud.dll' -Recurse | Select-Object -First 1
    if (-not $dll) {
        throw "No Dalamud.dll found under $source - is that the right artifact?"
    }
    $buildDir = $dll.DirectoryName
    foreach ($required in 'Dalamud.Injector.exe', 'Dalamud.Boot.dll') {
        if (-not (Test-Path (Join-Path $buildDir $required))) {
            throw "$required is missing from $buildDir - the build is incomplete."
        }
    }

    if (Test-Path $dalamudDir) {
        Remove-Item $dalamudDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $dalamudDir -Force | Out-Null
    Copy-Item -Path (Join-Path $buildDir '*') -Destination $dalamudDir -Recurse -Force
    Set-Content -Path $infoFile -Value $description

    Write-Host "Installed Dalamud $(Get-DalamudVersion) to $dalamudDir"
    Write-Host $description
}

function Invoke-Install {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $staging = Join-Path $testRoot 'staging'
    if (Test-Path $staging) {
        Remove-Item $staging -Recurse -Force
    }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    try {
        if ($ArtifactZip) {
            Expand-Archive -Path $ArtifactZip -DestinationPath $staging -Force
            Install-FromFolder $staging "Source: artifact zip $ArtifactZip"
            return
        }

        if ($BuildFromSource) {
            Require-Command git 'Install Git for Windows.'
            Require-Command dotnet 'Install the .NET 10 SDK.'
            Require-Command cmake 'Install CMake 4.2+ (it also ships with Visual Studio 2026).'

            $src = Join-Path $testRoot 'src\Dalamud'
            if (-not (Test-Path (Join-Path $src '.git'))) {
                git clone --recurse-submodules https://github.com/goatcorp/Dalamud.git $src
                if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }
            }
            Push-Location $src
            try {
                git fetch origin $Branch
                if ($LASTEXITCODE -ne 0) { throw "git fetch of '$Branch' failed." }
                git checkout -B $Branch "origin/$Branch"
                if ($LASTEXITCODE -ne 0) { throw "git checkout of '$Branch' failed." }
                git submodule update --init --recursive
                if ($LASTEXITCODE -ne 0) { throw 'git submodule update failed.' }

                # Same steps as goatcorp/Dalamud's own CI (.github/workflows/main.yml).
                cmake --preset vs2026
                if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed (needs Visual Studio 2026 with the C++ workload).' }
                cmake --build build --config Release
                if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
                dotnet build Dalamud/Dalamud.csproj -c Release
                if ($LASTEXITCODE -ne 0) { throw 'Dalamud build failed.' }
                dotnet build Dalamud.Injector/Dalamud.Injector.csproj -c Release
                if ($LASTEXITCODE -ne 0) { throw 'Dalamud.Injector build failed.' }

                $sha = (git rev-parse --short HEAD).Trim()
            }
            finally {
                Pop-Location
            }
            Install-FromFolder (Join-Path $src 'bin\Release') "Source: local build of $Branch @ $sha"
            return
        }

        Require-Command gh 'Install the GitHub CLI and run "gh auth login", or download the artifact by hand and pass -ArtifactZip.'
        $runJson = gh run list --repo goatcorp/Dalamud --branch $Branch --workflow 'Build Dalamud' `
            --status success --limit 1 --json 'databaseId,headSha,createdAt'
        if ($LASTEXITCODE -ne 0) { throw 'gh run list failed (are you logged in? run "gh auth login").' }
        $run = $runJson | ConvertFrom-Json | Select-Object -First 1
        if (-not $run) {
            throw "No successful 'Build Dalamud' run found for branch '$Branch'."
        }
        Write-Host "Downloading dalamud-artifact from run $($run.databaseId) ($($run.headSha.Substring(0, 7)), $($run.createdAt))..."
        gh run download $run.databaseId --repo goatcorp/Dalamud --name dalamud-artifact --dir $staging
        if ($LASTEXITCODE -ne 0) { throw 'gh run download failed (CI artifacts expire after a while; try -BuildFromSource).' }
        Install-FromFolder $staging "Source: CI run $($run.databaseId) of $Branch @ $($run.headSha.Substring(0, 7)) ($($run.createdAt))"
    }
    finally {
        if (Test-Path $staging) {
            Remove-Item $staging -Recurse -Force
        }
    }
}

function Invoke-BuildPlugin {
    Require-Command dotnet 'Install the .NET 10 SDK.'
    $version = Get-DalamudVersion
    $apiLevel = $version.Major
    Write-Host "Building against pre-release Dalamud $version (API $apiLevel) at $dalamudDir"

    # Dalamud.NET.Sdk and the test project both read DALAMUD_HOME instead of Hooks\dev when it's set.
    $previousHome = $env:DALAMUD_HOME
    $env:DALAMUD_HOME = $dalamudDir
    try {
        # Full rebuild so nothing compiled against the old Dalamud.dll is reused.
        Write-Host 'Building solution...'
        dotnet build $solution --no-incremental
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed against Dalamud $version - see the errors above (missing files, API breaks, etc.)."
        }

        Write-Host 'Running tests...'
        dotnet test $solution --no-build
        if ($LASTEXITCODE -ne 0) {
            throw "Tests failed against Dalamud $version - see the output above."
        }

        Write-Host 'Building plugin...'
        # DalamudApiLevel overrides the packager's default (15) so the manifest matches the build.
        dotnet build $pluginProject -c Debug --no-incremental -o $pluginOutDir "-p:DalamudApiLevel=$apiLevel"
        if ($LASTEXITCODE -ne 0) {
            throw "Plugin build failed against Dalamud $version."
        }
    }
    finally {
        $env:DALAMUD_HOME = $previousHome
    }

    Write-Host ''
    Write-Host "Built: $(Join-Path $pluginOutDir 'PenumbraOrganizer.Plugin.dll')"
    Write-Host 'After injecting, add that DLL as a dev plugin in /xlsettings > Experimental > Dev Plugin Locations.'
}

function Invoke-Inject {
    $version = Get-DalamudVersion
    $injector = Join-Path $dalamudDir 'Dalamud.Injector.exe'

    if (-not (Test-Path $assetDir)) {
        throw "Dalamud assets not found at $assetDir. Launch the game once with a working Dalamud so XIVLauncher downloads them."
    }

    $games = @(Get-Process -Name 'ffxiv_dx11' -ErrorAction SilentlyContinue)
    if ($games.Count -eq 0) {
        throw 'The game is not running. Start it through XIVLauncher (Dalamud will stay off), log in, then run this again.'
    }

    foreach ($game in $games) {
        $loaded = $false
        try {
            $loaded = [bool]($game.Modules | Where-Object { $_.ModuleName -eq 'Dalamud.Boot.dll' })
        }
        catch {
            # Module list can be unreadable without elevation; the injector will report a double-inject itself.
        }
        if ($loaded) {
            throw "Dalamud is already loaded in process $($game.Id). Restart the game first."
        }
    }

    New-Item -ItemType Directory -Path $testPlugins -Force | Out-Null
    $realConfig = Join-Path $xlDir 'dalamudConfig.json'
    if (-not (Test-Path $testConfig) -and (Test-Path $realConfig)) {
        # Copy once so settings and dev plugin locations carry over; Dalamud may migrate the copy.
        Copy-Item $realConfig $testConfig
        Write-Host "Copied your Dalamud config to $testConfig (the original is not modified)."
    }

    Write-Host "Injecting Dalamud $version into process(es): $($games.Id -join ', ')"
    Push-Location $dalamudDir
    try {
        $injectArgs = @('inject') + ($games | ForEach-Object { "$($_.Id)" }) + @(
            "--dalamud-working-directory=$dalamudDir",
            "--dalamud-configuration-path=$testConfig",
            "--dalamud-plugin-directory=$testPlugins",
            "--dalamud-asset-directory=$assetDir",
            "--dalamud-client-language=$Language"
        )
        & $injector @injectArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Dalamud.Injector exited with code $LASTEXITCODE. Logs are in $dalamudDir and $xlDir."
        }
    }
    finally {
        Pop-Location
    }

    Write-Host ''
    Write-Host 'Injected. In game:'
    Write-Host "  1. /xlsettings > Experimental > Dev Plugin Locations: add $(Join-Path $pluginOutDir 'PenumbraOrganizer.Plugin.dll')"
    Write-Host '  2. /xlplugins > Dev Tools: enable Penumbra Organizer, then /porganizer'
    Write-Host 'Penumbra must also have a build for this Dalamud, or the IPC calls will show a connection error.'
}

function Invoke-Status {
    Write-Host "Test profile: $testRoot"
    if (Test-Path (Join-Path $dalamudDir 'Dalamud.dll')) {
        Write-Host "Dalamud:      $(Get-DalamudVersion)"
        if (Test-Path $infoFile) { Write-Host "              $(Get-Content $infoFile)" }
    }
    else {
        Write-Host 'Dalamud:      not installed'
    }
    $pluginDll = Join-Path $pluginOutDir 'PenumbraOrganizer.Plugin.dll'
    Write-Host "Plugin build: $(if (Test-Path $pluginDll) { $pluginDll } else { 'not built' })"
    Write-Host "Assets:       $(if (Test-Path $assetDir) { $assetDir } else { 'missing' })"
    $games = @(Get-Process -Name 'ffxiv_dx11' -ErrorAction SilentlyContinue)
    Write-Host "Game:         $(if ($games.Count) { "running (PID $($games.Id -join ', '))" } else { 'not running' })"
}

switch ($Action) {
    'Install' { Invoke-Install }
    'BuildPlugin' { Invoke-BuildPlugin }
    'Inject' { Invoke-Inject }
    'Status' { Invoke-Status }
}
