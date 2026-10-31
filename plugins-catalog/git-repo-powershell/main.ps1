# Official plugin: an overview of a Git repository as the folder's quick view. Put a c4k.dir in the repository:
#   { "preview": { "plugin": "official.git-repo" }, "enter": { "plugin": "official.git-repo" } }
# Shows the branch and how far it is from its upstream, uncommitted changes, recent commits, branches, tags and
# remotes. git runs through the app (shell.run), so every command line is audited; nothing is changed in the repository.
#
# options (in c4k.dir): commits — how many recent commits (default 15); files — how many changed files (default 30);
# branches — how many branches and tags (default 10).
. "$PSScriptRoot/c4k.ps1"

# exitCode, output, error of one git call; quotePath=false keeps non-ASCII file names readable.
function Invoke-Git([string] $Folder, [string[]] $Arguments) {
    Invoke-C4KProcess -File 'git' -Arguments (@('-c', 'core.quotePath=false', '-C', $Folder) + $Arguments) -WorkingDirectory $Folder
}

function Get-Lines($Result) { if ($Result.exitCode -eq 0) { @($Result.output -split "`r?`n" | Where-Object { $_ }) } else { @() } }

# Text for a Markdown table cell.
function Format-Cell([string] $Text) { ($Text ?? '') -replace '\|', '\|' -replace '[\r\n]+', ' ' }

# Remote URLs may carry a user and a token (https://user:token@host/...): never show them.
function Hide-Credentials([string] $Url) { $Url -replace '://[^/@\s]+@', '://' }

function Get-Option($Options, [string] $Name, [int] $Default) {
    $value = if ($Options) { $Options.$Name } else { $null }
    if ($value -is [ValueType] -and [int]$value -gt 0) { [int]$value } else { $Default }
}

$StatusNames = @{ 'M' = 'zmieniony'; 'A' = 'dodany'; 'D' = 'usunięty'; 'R' = 'przemianowany'; 'C' = 'skopiowany'; 'U' = 'konflikt'; 'T' = 'zmiana typu' }

# "XY path" of git status --porcelain=v1: X = staged, Y = working tree.
function Get-Change([string] $Line) {
    $x = $Line[0]; $y = $Line[1]; $path = $Line.Substring(3)
    $state = if ($x -eq '?') { 'nieśledzony' }
             elseif ($x -eq 'U' -or $y -eq 'U' -or "$x$y" -in 'AA', 'DD') { 'konflikt' }
             else {
                 $parts = @()
                 if ($x -ne ' ') { $parts += "$($StatusNames["$x"] ?? $x) (staged)" }
                 if ($y -ne ' ') { $parts += $StatusNames["$y"] ?? $y }
                 $parts -join ', '
             }
    [pscustomobject]@{ State = $state; Path = $path; Staged = $x -notin ' ', '?'; Untracked = $x -eq '?'; Conflict = $state -eq 'konflikt' }
}

function Get-Overview([string] $Folder, $Options) {
    $top = Invoke-Git $Folder @('rev-parse', '--show-toplevel')
    if ($top.exitCode -ne 0) {
        $reason = ($top.error ?? '').Trim()
        return "# Git`n`n**$(Format-Cell $Folder)** nie jest repozytorium Git.`n`n" + ($reason ? "``$(Format-Cell $reason)``" : '')
    }
    $root = $top.output.Trim().Replace('/', '\')
    $commits = Get-Option $Options 'commits' 15
    $files = Get-Option $Options 'files' 30
    $refs = Get-Option $Options 'branches' 10

    $status = Get-Lines (Invoke-Git $Folder @('status', '--porcelain=v1', '--branch', '--untracked-files=normal'))
    $head = ($status | Where-Object { $_.StartsWith('## ') } | Select-Object -First 1) ?? '## ?'
    $changes = @($status | Where-Object { -not $_.StartsWith('## ') } | ForEach-Object { Get-Change $_ })
    $log = Get-Lines (Invoke-Git $Folder @('log', "-$commits", '--date=short', '--pretty=format:%h%x09%ad%x09%an%x09%s%x09%D'))
    $branches = Get-Lines (Invoke-Git $Folder @('for-each-ref', '--sort=-committerdate', "--count=$refs", '--format=%(HEAD)%09%(refname:short)%09%(committerdate:short)%09%(upstream:short)%09%(upstream:track)', 'refs/heads'))
    $tags = Get-Lines (Invoke-Git $Folder @('for-each-ref', '--sort=-creatordate', "--count=$refs", '--format=%(refname:short)%09%(creatordate:short)%09%(subject)', 'refs/tags'))
    $remotes = Get-Lines (Invoke-Git $Folder @('remote', '-v')) | Where-Object { $_ -like '*(fetch)' }

    # "## main...origin/main [ahead 2, behind 1]", "## No commits yet on main", "## HEAD (no branch)"
    $branchText = $head.Substring(3)
    $track = if ($branchText -match '\[(?<track>[^\]]+)\]$') { $Matches.track; $branchText = $branchText.Substring(0, $branchText.LastIndexOf(' [')) } else { $null }
    $branchName, $upstream = ($branchText -replace '^No commits yet on ', '') -split '\.\.\.', 2
    $sync = if (-not $upstream) { 'bez gałęzi zdalnej' }
            elseif ($track -eq 'gone') { "$upstream (usunięta na serwerze)" }
            elseif ($track) { "$upstream · " + (($track -replace 'ahead (\d+)', '↑$1' -replace 'behind (\d+)', '↓$1') -replace ',', '') }
            else { "$upstream · aktualna" }

    $md = [System.Text.StringBuilder]::new()
    [void]$md.AppendLine("# $(Format-Cell (Split-Path $root -Leaf))").AppendLine()
    [void]$md.AppendLine("| | |").AppendLine("|---|---|")
    [void]$md.AppendLine("| Gałąź | **$(Format-Cell $branchName)** |")
    [void]$md.AppendLine("| Zdalna | $(Format-Cell $sync) |")
    if ($log.Count) {
        $hash, $date, $author, $subject = $log[0] -split "`t", 5
        [void]$md.AppendLine("| Ostatni commit | ``$hash`` · $date · $(Format-Cell $author) — $(Format-Cell $subject) |")
    }
    $summary = if ($changes.Count) {
        @(
            "$(@($changes | Where-Object Staged).Count) w indeksie"
            "$(@($changes | Where-Object { -not $_.Staged -and -not $_.Untracked -and -not $_.Conflict }).Count) niezatwierdzonych"
            "$(@($changes | Where-Object Untracked).Count) nieśledzonych"
            $(if (@($changes | Where-Object Conflict).Count) { "**$(@($changes | Where-Object Conflict).Count) w konflikcie**" })
        ) -join ' · '
    } else { 'brak zmian ✓' }
    [void]$md.AppendLine("| Zmiany | $summary |")
    if ($root -ine $Folder.TrimEnd('\')) { [void]$md.AppendLine("| Repozytorium | $(Format-Cell $root) |") }
    [void]$md.AppendLine()

    if ($changes.Count) {
        [void]$md.AppendLine("## Zmiany ($($changes.Count))").AppendLine().AppendLine('| Stan | Plik |').AppendLine('|---|---|')
        foreach ($change in $changes | Select-Object -First $files) { [void]$md.AppendLine("| $($change.State) | $(Format-Cell $change.Path) |") }
        if ($changes.Count -gt $files) { [void]$md.AppendLine().AppendLine("… i $($changes.Count - $files) więcej") }
        [void]$md.AppendLine()
    }

    if ($log.Count) {
        [void]$md.AppendLine('## Ostatnie commity').AppendLine().AppendLine('| Commit | Data | Autor | Opis |').AppendLine('|---|---|---|---|')
        foreach ($line in $log) {
            $hash, $date, $author, $subject, $decoration = $line -split "`t", 5
            $labels = if ($decoration) { ' ' + (($decoration -split ', ' | ForEach-Object { "``$((Format-Cell $_) -replace '^HEAD -> ', '')``" }) -join ' ') } else { '' }
            [void]$md.AppendLine("| ``$hash`` | $date | $(Format-Cell $author) | $(Format-Cell $subject)$labels |")
        }
        [void]$md.AppendLine()
    } else {
        [void]$md.AppendLine('Repozytorium nie ma jeszcze commitów.').AppendLine()
    }

    if ($branches.Count) {
        [void]$md.AppendLine('## Gałęzie').AppendLine().AppendLine('| Gałąź | Ostatnia zmiana | Zdalna |').AppendLine('|---|---|---|')
        foreach ($line in $branches) {
            $current, $name, $date, $remote, $remoteTrack = $line -split "`t", 5
            $shown = if ($current -eq '*') { "**$(Format-Cell $name)** ←" } else { Format-Cell $name }
            [void]$md.AppendLine("| $shown | $date | $(Format-Cell "$remote $remoteTrack".Trim()) |")
        }
        [void]$md.AppendLine()
    }

    if ($tags.Count) {
        [void]$md.AppendLine('## Tagi').AppendLine().AppendLine('| Tag | Data | Opis |').AppendLine('|---|---|---|')
        foreach ($line in $tags) {
            $name, $date, $subject = $line -split "`t", 3
            [void]$md.AppendLine("| ``$(Format-Cell $name)`` | $date | $(Format-Cell $subject) |")
        }
        [void]$md.AppendLine()
    }

    if ($remotes.Count) {
        [void]$md.AppendLine('## Serwery zdalne').AppendLine().AppendLine('| Nazwa | Adres |').AppendLine('|---|---|')
        foreach ($line in $remotes) {
            $name, $url = ($line -replace '\s+\(fetch\)$', '') -split '\s+', 2
            [void]$md.AppendLine("| $(Format-Cell $name) | $(Format-Cell (Hide-Credentials $url)) |")
        }
    }
    $md.ToString()
}

Register-C4KHandler 'preview.render' {
    param($p)
    @{ kind = 'markdown'; content = (Get-Overview $p.entry.path $p.options) }
}

Start-C4KPlugin
