#requires -Version 7.2
# Refreshes "files" (path + SHA-256) and "version" of every plugin in catalog.json from its folder, so the catalog
# always matches what raw.githubusercontent.com serves. Run after changing a catalog plugin, then commit both:
#   pwsh ./plugins-catalog/Update-Catalog.ps1
# The hash is over the UTF-8 text with CR removed (PluginCatalogService.ContentHash), so a CRLF checkout on Windows
# and the LF file on GitHub give the same value. A test (PluginCatalogTests) fails when the catalog is out of date.
param([string] $Catalog = (Join-Path $PSScriptRoot 'catalog.json'))

$root = Split-Path $PSScriptRoot -Parent
$document = Get-Content $Catalog -Raw | ConvertFrom-Json -AsHashtable
$sha = [System.Security.Cryptography.SHA256]::Create()

foreach ($plugin in $document.plugins) {
    $folder = Join-Path $root $plugin.path
    $files = Get-ChildItem $folder -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](__pycache__|node_modules)[\\/]' -and $_.Extension -notin '.pyc' } |
        Sort-Object FullName
    $plugin.files = @(foreach ($file in $files) {
        $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.UTF8Encoding]::new($false)) -replace "`r", ''
        @{
            path = [System.IO.Path]::GetRelativePath($folder, $file.FullName).Replace('\', '/')
            sha256 = [Convert]::ToHexStringLower($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text)))
        }
    })
    $plugin.version = (Get-Content (Join-Path $folder 'plugin.json') -Raw | ConvertFrom-Json).version
}

$document.updated = (Get-Date).ToString('yyyy-MM-dd')
($document | ConvertTo-Json -Depth 10) + "`n" | Set-Content $Catalog -NoNewline -Encoding utf8NoBOM
Write-Host "Updated $($document.plugins.Count) plugins in $Catalog"
