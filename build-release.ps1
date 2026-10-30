#Requires -Version 7
<#
.SYNOPSIS
    Publishes optimised, self-contained Commander4K binaries (ReadyToRun, single file) per architecture.
.PARAMETER Version
    Semantic version to stamp (e.g. 0.2.0 or 0.3.0-beta.1). Defaults to the VersionPrefix in Directory.Build.props + "-dev".
.PARAMETER Package
    Also writes the GitHub release assets the in-app updater expects into dist/release:
    Commander4K-<rid>.zip (portable: exe + LICENSE + README), Commander4K-<rid>.msi (installer, WiX 5)
    for each runtime, and SHA256SUMS.txt.
.EXAMPLE
    ./build-release.ps1                                   # win-x64 and win-arm64 into dist/publish/<rid>
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
    # Separate from any dist/<rid> folder someone may be running the app from (it holds config/ and data/).
    $output = Join-Path $PSScriptRoot "dist/publish/$rid"
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
        # Portable ZIP.
        $zipStage = Join-Path $PSScriptRoot "dist/stage-$rid"
        if (Test-Path $zipStage) { Remove-Item $zipStage -Recurse -Force }
        New-Item -ItemType Directory -Path $zipStage | Out-Null
        Copy-Item $exe.FullName, (Join-Path $PSScriptRoot 'LICENSE'), (Join-Path $PSScriptRoot 'README.md') $zipStage
        Compress-Archive -Path (Join-Path $zipStage '*') -DestinationPath (Join-Path $releaseDir "Commander4K-$rid.zip") -CompressionLevel Optimal

        # MSI. Windows Installer versions are numeric only, so a pre-release tag maps to its x.y.z.
        $marker = Join-Path $zipStage 'Commander4K.installed'
        Set-Content -Path $marker -Value 'Installed by the Commander4K MSI; the updater downloads a new MSI instead of replacing the exe.'
        $productVersion = if ($Version) { ($Version.TrimStart('v') -split '[-+]')[0] } else { '0.0.1' }
        $msiOut = Join-Path $PSScriptRoot "dist/msi-$rid"
        dotnet build (Join-Path $PSScriptRoot 'installer/Commander4K.Installer/Commander4K.Installer.wixproj') `
            --configuration Release `
            --no-incremental `
            --output $msiOut `
            "-p:IntermediateOutputPath=$(Join-Path $PSScriptRoot "dist/msi-obj-$rid")\" `
            "-p:InstallerPlatform=$($rid.Substring(4))" `
            "-p:ProductVersion=$productVersion" `
            "-p:PublishDir=$output\" `
            "-p:MarkerFile=$marker"
        if ($LASTEXITCODE -ne 0) {
            throw "MSI build failed for $rid (exit code $LASTEXITCODE)."
        }

        Copy-Item (Join-Path $msiOut 'Commander4K.msi') (Join-Path $releaseDir "Commander4K-$rid.msi")
        Remove-Item $zipStage, $msiOut, (Join-Path $PSScriptRoot "dist/msi-obj-$rid") -Recurse -Force
    }
}

if ($Package) {
    # sha256sum format, verified by UpdateService before installing.
    $sums = Get-ChildItem $releaseDir -Include 'Commander4K-*.zip', 'Commander4K-*.msi' -Recurse | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    Set-Content -Path (Join-Path $releaseDir 'SHA256SUMS.txt') -Value $sums -Encoding ascii
    Write-Host "==> Release assets in $releaseDir" -ForegroundColor Cyan
    Get-ChildItem $releaseDir | ForEach-Object { Write-Host "    $($_.Name)" }
}
