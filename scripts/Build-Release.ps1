[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repositoryRoot 'RebornLauncher.sln'
$launcherProject = Join-Path $repositoryRoot 'src\RebornLauncher\RebornLauncher.csproj'
$publishRoot = Join-Path $repositoryRoot 'artifacts\publish\win-x64'
$installerRoot = Join-Path $repositoryRoot 'artifacts\installer'
$innoScript = Join-Path $repositoryRoot 'packaging\RebornLauncher.iss'

if (-not $SkipTests) {
    & dotnet test $solution --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed with exit code $LASTEXITCODE"
    }
}

& dotnet publish $launcherProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishRoot `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Launcher publish failed with exit code $LASTEXITCODE"
}

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
Write-Host "  Installer: $($installer.FullName)"
Write-Host "  SHA-256:  $installerHash"
