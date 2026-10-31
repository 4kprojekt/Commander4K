# Official plugin: the games of a Steam library. Put a c4k.dir in a steamapps folder (or in Steam's own folder):
#   { "enter": { "plugin": "official.steam-library" }, "preview": { "plugin": "official.steam-library" } }
# Games come from appmanifest_<appid>.acf files (and, by default, every library listed in libraryfolders.vdf);
# covers from Steam's appcache. Everything is read through the SDK (fs.read); a tile click asks the app to open
# steam://rungameid/<appid>, which the user confirms once.
#
# options (in c4k.dir): steamPath — Steam's folder for covers when the library is elsewhere (default: next to
# steamapps, then C:\Program Files (x86)\Steam); allLibraries — false shows only this folder's games; sort — name|size|updated.
. "$PSScriptRoot/c4k.ps1"

$DefaultSteam = 'C:\Program Files (x86)\Steam'

# "key"  "value" pairs of Valve's KeyValues text (enough for appmanifest and libraryfolders).
function Get-VdfPairs([string] $Text) {
    foreach ($match in [regex]::Matches($Text, '"(?<key>[^"]+)"\s+"(?<value>(?:\\.|[^"\\])*)"')) {
        [pscustomobject]@{ Key = $match.Groups['key'].Value; Value = ($match.Groups['value'].Value -replace '\\\\', '\') }
    }
}

function Join-C4KPath([string] $Folder, [string] $Name) { if ($Folder.EndsWith('\')) { "$Folder$Name" } else { "$Folder\$Name" } }

function Get-Parent([string] $Path) { $trimmed = $Path.TrimEnd('\'); $cut = $trimmed.LastIndexOf('\'); if ($cut -le 2) { $trimmed.Substring(0, $cut + 1) } else { $trimmed.Substring(0, $cut) } }

function Test-C4KPath([string] $Path) { try { $null -ne (Get-C4KItem $Path) } catch { $false } }

# The steamapps folder and Steam's root for the folder the c4k.dir is in.
function Resolve-Library([string] $Folder, $Options) {
    $steamapps = if ((Split-Path $Folder -Leaf) -ieq 'steamapps') { $Folder } else { Join-C4KPath $Folder 'steamapps' }
    $roots = @()
    if ($Options -and $Options.steamPath) { $roots += $Options.steamPath }
    $roots += (Get-Parent $steamapps), $DefaultSteam
    $root = $roots | Where-Object { Test-C4KPath (Join-C4KPath $_ 'appcache') } | Select-Object -First 1
    [pscustomobject]@{ SteamApps = $steamapps; Root = ($root ?? (Get-Parent $steamapps)) }
}

function Get-Games([string] $Folder, $Options) {
    $library = Resolve-Library $Folder $Options
    $folders = [System.Collections.Generic.List[string]]::new()
    $folders.Add($library.SteamApps)
    if (-not $Options -or $Options.allLibraries -ne $false) {
        $vdf = Join-C4KPath $library.SteamApps 'libraryfolders.vdf'
        if (Test-C4KPath $vdf) {
            foreach ($pair in Get-VdfPairs (Get-C4KText $vdf) | Where-Object Key -eq 'path') {
                $other = Join-C4KPath $pair.Value 'steamapps'
                if (-not ($folders -contains $other)) { $folders.Add($other) }
            }
        }
    }

    $games = foreach ($steamapps in $folders) {
        $entries = try { Get-C4KChildItem $steamapps } catch { @() }
        foreach ($entry in $entries | Where-Object { $_.kind -eq 'file' -and $_.name -like 'appmanifest_*.acf' }) {
            $values = @{}
            foreach ($pair in Get-VdfPairs (Get-C4KText $entry.path)) { if (-not $values.ContainsKey($pair.Key)) { $values[$pair.Key] = $pair.Value } }
            if (-not $values.appid -or -not $values.name) { continue }
            [pscustomobject]@{
                AppId = $values.appid
                Name = $values.name
                Size = [long]($values.SizeOnDisk ?? 0)
                Updated = if ($values.LastUpdated) { [DateTimeOffset]::FromUnixTimeSeconds([long]$values.LastUpdated).LocalDateTime } else { $null }
                InstallDir = Join-C4KPath (Join-C4KPath $steamapps 'common') ($values.installdir ?? '')
                Library = $steamapps
            }
        }
    }

    $sorted = switch ($Options.sort) {
        'size' { $games | Sort-Object Size -Descending }
        'updated' { $games | Sort-Object Updated -Descending }
        default { $games | Sort-Object Name }
    }
    [pscustomobject]@{ Library = $library; Games = @($sorted) }
}

# Steam's cover for a game: the per-game folder of newer clients, the flat file of older ones.
function Find-Cover([string] $Root, [string] $AppId) {
    $cache = Join-C4KPath (Join-C4KPath $Root 'appcache') 'librarycache'
    foreach ($candidate in @((Join-C4KPath (Join-C4KPath $cache $AppId) 'header.jpg'), (Join-C4KPath $cache "$($AppId)_header.jpg"))) {
        if (Test-C4KPath $candidate) { return $candidate }
    }
    return $null
}

function Format-Size([long] $Bytes) {
    if ($Bytes -ge 1GB) { '{0:N1} GB' -f ($Bytes / 1GB) } elseif ($Bytes -ge 1MB) { '{0:N0} MB' -f ($Bytes / 1MB) } else { '{0:N0} KB' -f ($Bytes / 1KB) }
}

Register-C4KHandler 'preview.render' {
    param($p)
    $result = Get-Games $p.entry.path $p.options
    $total = ($result.Games | Measure-Object Size -Sum).Sum
    $tiles = foreach ($game in $result.Games) {
        @{
            title = $game.Name
            subtitle = (Format-Size $game.Size)
            icon = '🎮'
            image = (Find-Cover $result.Library.Root $game.AppId)
            target = "steam://rungameid/$($game.AppId)"
        }
    }
    @{
        kind = 'tiles'
        title = "Steam · $($result.Games.Count) gier"
        description = if ($result.Games.Count) { "Razem $(Format-Size $total) · klik uruchamia grę w Steam" } else { "Brak plików appmanifest_*.acf w $($result.Library.SteamApps)." }
        tileWidth = 230
        tiles = @($tiles)
    }
}

Register-C4KHandler 'panel.load' {
    param($p)
    $options = if ($p.options) { $p.options } else { $null }
    $result = Get-Games $p.argument $options
    $filter = $p.filter
    $rows = foreach ($game in $result.Games | Where-Object { -not $filter -or $_.Name -like "*$filter*" }) {
        @{
            id = $game.AppId
            cells = @($game.Name, (Format-Size $game.Size), ($game.Updated ? $game.Updated.ToString('yyyy-MM-dd') : ''), $game.AppId, $game.InstallDir)
            sortKeys = @($null, $game.Size, ($game.Updated ? [DateTimeOffset]::new($game.Updated).ToUnixTimeSeconds() : 0), [long]$game.AppId, $null)
        }
    }
    @{
        columns = @(
            @{ id = 'name'; header = 'Gra'; width = 280 },
            @{ id = 'size'; header = 'Rozmiar'; align = 'right'; width = 100 },
            @{ id = 'updated'; header = 'Aktualizacja'; width = 110 },
            @{ id = 'appid'; header = 'AppID'; align = 'right'; width = 80 },
            @{ id = 'dir'; header = 'Folder'; width = 360 }
        )
        rows = @($rows)
        status = "$(@($rows).Count) gier · $($result.Library.SteamApps)"
    }
}

Register-C4KHandler 'panel.details' {
    param($p)
    @{ sections = @(@{ title = "AppID $($p.rowId)"; items = @(@('Uruchom', "steam://rungameid/$($p.rowId)"), @('Sklep', "https://store.steampowered.com/app/$($p.rowId)")) }) }
}

Start-C4KPlugin
