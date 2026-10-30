#Requires -Version 7
<#
.SYNOPSIS
    Publishes optimised, self-contained Commander4K binaries (ReadyToRun, single file) per architecture.
.PARAMETER Version
    Semantic version to stamp (e.g. 0.2.0 or 0.3.0-beta.1). Defaults to the VersionPrefix in Directory.Build.props + "-dev".
.PARAMETER Package
    Also writes the GitHub release assets the in-app updater expects into dist/release:
    Commander4K-<rid>.exe for each runtime and SHA256SUMS.txt.
.EXAMPLE
    ./build-release.ps1                                   # win-x64 and win-arm64 into dist/<rid>
    ./build-release.ps1 -Runtime win-arm64
    ./build-release.ps1 -Version 0.2.0 -Package           # what .github/workflows/release.yml runs
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64', 'win-arm64'),
    [string] $Configuration = 'Release',
    [string] $Version,
    [switch] $Package
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/Commander4K.App/Commander4K.App.csproj'
$releaseDir = Join-Path $PSScriptRoot 'dist/release'
$versionArgs = if ($Version) { @("-p:Version=$($Version.TrimStart('v'))") } else { @() }

if ($Package) {
    if (Test-Path $releaseDir) {
        Remove-Item $releaseDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $releaseDir | Out-Null
}

foreach ($rid in $Runtime) {
    $output = Join-Path $PSScriptRoot "dist/$rid"
    Write-Host "==> $rid -> $output" -ForegroundColor Cyan
    if (Test-Path $output) {
        Remove-Item $output -Recurse -Force
    }

    # IncludeNativeLibrariesForSelfExtract: SQLite and Skia ship native DLLs that must live inside the single file.
    dotnet publish $project `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained `
        --output $output `
        -p:PublishReadyToRun=true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        $versionArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid (exit code $LASTEXITCODE)."
    }

    $exe = Get-Item (Join-Path $output 'Commander4K.exe')
    Write-Host ("    {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green

    if ($Package) {
        Copy-Item $exe.FullName (Join-Path $releaseDir "Commander4K-$rid.exe")
    }
}

if ($Package) {
    # sha256sum format, verified by UpdateService before installing.
    $sums = Get-ChildItem $releaseDir -Filter 'Commander4K-*.exe' | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    Set-Content -Path (Join-Path $releaseDir 'SHA256SUMS.txt') -Value $sums -Encoding ascii
    Write-Host "==> Release assets in $releaseDir" -ForegroundColor Cyan
    Get-ChildItem $releaseDir | ForEach-Object { Write-Host "    $($_.Name)" }
}
