[CmdletBinding()]
param(
    [string]$Version = '0.4.1',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

# Builds the Windows release: the desktop launcher, the headless CLI, and a bundled MinGit, wrapped
# in an Inno Setup installer. Linux and macOS builds come from the release workflow instead; they
# need no installer and use the system Git.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repositoryRoot 'RebornLauncher.sln'
$desktopProject = Join-Path $repositoryRoot 'src\RebornLauncher.Desktop\RebornLauncher.Desktop.csproj'
$cliProject = Join-Path $repositoryRoot 'src\RebornLauncher.Cli\RebornLauncher.Cli.csproj'
$publishRoot = Join-Path $repositoryRoot 'artifacts\publish\win-x64'
$installerRoot = Join-Path $repositoryRoot 'artifacts\installer'
$innoScript = Join-Path $repositoryRoot 'packaging\RebornLauncher.iss'
$minGitManifest = Join-Path $repositoryRoot 'packaging\mingit.json'
$downloadCache = Join-Path $repositoryRoot 'artifacts\cache'

function Publish-Project {
    param([string]$Project, [string]$Executable)

    # Each project publishes to its own folder: two single-file publishes sharing one would race
    # over their intermediate files.
    $staging = Join-Path $repositoryRoot "artifacts\publish\staging\$([System.IO.Path]::GetFileNameWithoutExtension($Project))"
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue

    & dotnet publish $Project `
        --configuration $Configuration `
        --runtime win-x64 `
        --output $staging `
        -p:Version=$Version `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "Publish of $Project failed with exit code $LASTEXITCODE"
    }

    $produced = Join-Path $staging $Executable
    if (-not (Test-Path -LiteralPath $produced -PathType Leaf)) {
        throw "Publish of $Project did not produce $Executable."
    }

    Copy-Item -LiteralPath $produced -Destination (Join-Path $publishRoot $Executable) -Force
}

function Add-MinGit {
    # Bundled so a fresh Windows machine needs no Git installed. GitBootstrapper looks for it at
    # tools\MinGit\cmd\git.exe beside the launcher, and falls back to Git on PATH if it is absent.
    $manifest = Get-Content -LiteralPath $minGitManifest -Raw | ConvertFrom-Json
    $archive = Join-Path $downloadCache "MinGit-$($manifest.version)-64-bit.zip"
    New-Item -ItemType Directory -Path $downloadCache -Force | Out-Null

    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        Write-Host "Downloading MinGit $($manifest.version)..."
        Invoke-WebRequest -Uri $manifest.url -OutFile $archive -UseBasicParsing
    }

    # Verified before it is unpacked: this ends up inside the installer users run.
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = $manifest.sha256.ToLowerInvariant()
    if ($actual -ne $expected) {
        Remove-Item -LiteralPath $archive -Force
        throw @"
MinGit failed verification and was discarded.
  expected $expected
  actual   $actual
Either the download was corrupted, or packaging\mingit.json is stale. Do not bypass this.
"@
    }

    $target = Join-Path $publishRoot 'tools\MinGit'
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $target -Force

    $git = Join-Path $target 'cmd\git.exe'
    if (-not (Test-Path -LiteralPath $git -PathType Leaf)) {
        throw "MinGit unpacked without cmd\git.exe, which is the path the launcher looks for."
    }

    $reported = & $git --version
    Write-Host "  bundled $reported"
}

if (-not $SkipTests) {
    & dotnet test $solution --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed with exit code $LASTEXITCODE"
    }
}

Remove-Item -LiteralPath $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

Publish-Project -Project $desktopProject -Executable 'RebornLauncher.exe'
Publish-Project -Project $cliProject -Executable 'reborn.exe'
Add-MinGit

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install it, then rerun scripts\Build-Release.ps1.'
}

New-Item -ItemType Directory -Path $installerRoot -Force | Out-Null
& $iscc "/DMyAppVersion=$Version" "/DPublishDir=$publishRoot" "/DOutputDir=$installerRoot" $innoScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

$installer = Get-ChildItem -LiteralPath $installerRoot -Filter '*.exe' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $installer) {
    throw 'Inno Setup completed without producing an installer.'
}

$installerHash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $installerRoot 'SHA256SUMS.txt'
[System.IO.File]::WriteAllText(
    $checksumPath,
    "$installerHash  $($installer.Name)`r`n",
    [System.Text.UTF8Encoding]::new($false))
Write-Host ''
Write-Host 'Launcher release created:' -ForegroundColor Green
Write-Host "  Portable:  $(Join-Path $publishRoot 'RebornLauncher.exe')"
Write-Host "  CLI:       $(Join-Path $publishRoot 'reborn.exe')"
Write-Host "  Installer: $($installer.FullName)"
Write-Host "  SHA-256:   $installerHash"
