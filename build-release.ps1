#Requires -Version 7
<#
.SYNOPSIS
    Publishes optimised, self-contained Commander4K binaries (ReadyToRun, single file) per architecture.
.EXAMPLE
    ./build-release.ps1                      # win-x64 and win-arm64 into dist/<rid>
    ./build-release.ps1 -Runtime win-arm64
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64', 'win-arm64'),
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/Commander4K.App/Commander4K.App.csproj'

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
        -p:DebugType=none
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid (exit code $LASTEXITCODE)."
    }

    $exe = Get-Item (Join-Path $output 'Commander4K.exe')
    Write-Host ("    {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
}
