# Background IMAP -> .eml synchronisation of official.mail. Dot-sourced by main.ps1; every file and mail operation goes
# through the SDK (c4k.ps1): the app owns the password, the connections and the certificates.
#
# Account folder layout:
#   <account>/c4k.dir                  options: imap, smtp, user, name, refresh ("5m"; missing/false = only on open and
#                                      "Sprawdź teraz"), since ("<n>d|m|y" or "YYYY-MM-DD"; empty = 3 months)
#   <account>/.mail/state.json         sync state (see New-MailState)
#   <account>/.mail/lock               { machine, lastTick }: one computer per account folder
#   <account>/<local folder>/<"● " if unread><yyyy-MM-dd HHmm sender - subject [uid N]>.eml
#
# One tick does at most 50 fetches or 15 s of work and then answers { more = $true }; the next tick continues from
# state.json, which is written after every folder. Sync never changes the server except for drafts (APPEND).

$script:MailMaxFetches = 50
$script:MailMaxSeconds = 15
$script:MailLockMinutes = 10
$script:MailMaxStateBytes = 16777216
$script:MailUnread = [string][char]0x25CF + ' '
$script:MailCheckRequested = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$script:MailUidPattern = '\[uid (\d+)\]\.eml$'

# ---- small helpers ----

function Join-MailPath([string] $Base, [string] $Child) { $Base.TrimEnd('\', '/') + '\' + $Child }

function Get-MailLeaf([string] $Path) { ($Path.TrimEnd('\', '/') -split '[\\/]')[-1] }

function Get-MailNow { [DateTimeOffset]::UtcNow }

# ConvertFrom-Json turns ISO strings into DateTime; state and lock values may be either.
function Get-MailInstant($Value) {
    if ($null -eq $Value -or ($Value -is [string] -and $Value.Trim() -eq '')) { return $null }
    if ($Value -is [DateTimeOffset]) { return $Value }
    if ($Value -is [DateTime]) { return [DateTimeOffset]::new($Value.ToUniversalTime(), [TimeSpan]::Zero) }
    try {
        return [DateTimeOffset]::Parse([string] $Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
    }
    catch { return $null }
}

function Format-MailInstant($Value) {
    $instant = Get-MailInstant $Value
    if ($null -eq $instant) { $null } else { $instant.ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture) }
}

# "5m", "30s", "1h" -> TimeSpan (at least one minute); missing, false or unparsable -> $null (no automatic refresh).
function ConvertTo-MailInterval($Value) {
    if ($null -eq $Value -or $Value -is [bool]) { return $null }
    if ([string] $Value -notmatch '^\s*(\d+)\s*([smh])\s*$') { return $null }
    $n = [int] $Matches[1]
    $span = switch ($Matches[2]) { 's' { [TimeSpan]::FromSeconds($n) } 'm' { [TimeSpan]::FromMinutes($n) } default { [TimeSpan]::FromHours($n) } }
    if ($span -lt [TimeSpan]::FromMinutes(1)) { [TimeSpan]::FromMinutes(1) } else { $span }
}

# "<n>d" | "<n>m" | "<n>y" | "YYYY-MM-DD" -> ISO date for mail.sync; empty or unknown = 3 months back.
function ConvertTo-MailSince($Value) {
    $now = Get-MailNow
    $since = $now.AddMonths(-3)
    if ($Value -is [DateTime]) {
        $since = [DateTimeOffset]::new([DateTime]::SpecifyKind($Value.Date, [DateTimeKind]::Unspecified), [TimeSpan]::Zero)
    }
    elseif ([string] $Value -match '^\s*(\d+)\s*([dmy])\s*$') {
        $n = [int] $Matches[1]
        $since = switch ($Matches[2]) { 'd' { $now.AddDays(-$n) } 'm' { $now.AddMonths(-$n) } default { $now.AddYears(-$n) } }
    }
    elseif ([string] $Value -match '^\s*(\d{4}-\d{2}-\d{2})\s*$') {
        $since = [DateTimeOffset]::ParseExact($Matches[1], 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
    }
    $since.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
}

function Format-MailNewCount([int] $Count) {
    $tens = $Count % 100
    $ones = $Count % 10
    if ($Count -eq 1) { '1 nowa wiadomość' }
    elseif ($ones -ge 2 -and $ones -le 4 -and ($tens -lt 12 -or $tens -gt 14)) { "$Count nowe wiadomości" }
    else { "$Count nowych wiadomości" }
}

# JSON file through the SDK, or $null when it is missing or not JSON.
function Read-MailJson([string] $Path) {
    try {
        if ($null -eq (Get-C4KItem $Path)) { return $null }
        return (Get-C4KText $Path) | ConvertFrom-Json -AsHashtable
    }
    catch { return $null }
}

# ---- accounts ----

function Get-MailAccounts { @(Get-C4KStorage 'accounts' @() | Where-Object { $_ }) }

# Adds an account folder to the plugin's storage; $true when it was not there yet.
function Register-MailAccount([string] $Path) {
    $accounts = [System.Collections.Generic.List[string]]::new()
    foreach ($account in Get-MailAccounts) { $accounts.Add([string] $account) }
    if ($accounts -contains $Path) { return $false }
    $accounts.Add($Path)
    Set-C4KStorage 'accounts' $accounts.ToArray()
    $true
}

# The account options from the folder's c4k.dir (preview or enter action naming this plugin), else $Options.
function Get-MailAccountConfig([string] $Folder, $Options) {
    $config = Read-MailJson (Join-MailPath $Folder 'c4k.dir')
    $found = $null
    if ($config -is [hashtable]) {
        foreach ($key in 'preview', 'enter') {
            $action = $config[$key]
            if ($action -is [hashtable] -and $action['plugin'] -eq 'official.mail' -and $action['options'] -is [hashtable]) { $found = $action['options']; break }
        }
    }
    if ($null -eq $found -and $Options -is [hashtable]) { $found = $Options }
    if ($null -eq $found -or -not $found['imap'] -or -not $found['user']) { return $null }
    $found
}

function New-MailAccountParam([hashtable] $Options, [bool] $Interactive) {
    @{
        imap = [string] $Options['imap']
        smtp = $(if ($Options['smtp']) { [string] $Options['smtp'] } else { $null })
        user = [string] $Options['user']
        interactive = $Interactive
    }
}

function Request-MailCheck([string] $Path) {
    [void] $script:MailCheckRequested.Add($Path)
    try { Request-C4KBackgroundTick } catch { Write-C4KLog "background.wake failed: $_" }
}

# ---- state.json and lock ----


function New-MailState {
    @{
        folders = @{}
        lastCheck = $null
        lastSuccess = $null
        error = $null
        authRequired = $false
        progress = $null
        flagged = [System.Collections.Generic.List[string]]::new()
        drafts = @{}
        retired = [System.Collections.Generic.List[string]]::new()
    }
}

function Get-MailStatePath([string] $Folder) { Join-MailPath (Join-MailPath $Folder '.mail') 'state.json' }

# The account's state. A missing state.json is a new account; one that exists but cannot be read or parsed gives
# $null: the caller skips the account and never writes over the file (it holds known UIDs, drafts and authRequired).
function Read-MailState([string] $Folder) {
    $path = Get-MailStatePath $Folder
    try {
        $item = Get-C4KItem $path
        if ($null -eq $item) { return New-MailState }
        if ([long] $item.size -gt $script:MailMaxStateBytes) { throw "larger than $($script:MailMaxStateBytes) bytes" }
        $stored = (Get-C4KText $path $script:MailMaxStateBytes) | ConvertFrom-Json -AsHashtable
        if ($stored -isnot [hashtable]) { throw 'not a JSON object' }
    }
    catch {
        Write-C4KLog "mail state $path cannot be read, account skipped: $_"
        return $null
    }

    $state = New-MailState
    $state.lastCheck = Format-MailInstant $stored['lastCheck']
    $state.lastSuccess = Format-MailInstant $stored['lastSuccess']
    $state.error = $stored['error']
    $state.authRequired = [bool] $stored['authRequired']
    $state.progress = $stored['progress']
    foreach ($item in @($stored['flagged'])) { if ($item) { $state.flagged.Add([string] $item) } }
    foreach ($item in @($stored['retired'])) { if ($item) { $state.retired.Add([string] $item) } }
    if ($stored['drafts'] -is [hashtable]) {
        foreach ($name in $stored['drafts'].Keys) {
            $draft = $stored['drafts'][$name]
            # Older states stored the server uid only.
            $state.drafts[$name] = if ($draft -is [hashtable]) { @{ uid = [long] $draft['uid']; modified = Format-MailInstant $draft['modified'] } } else { @{ uid = [long] $draft; modified = $null } }
        }
    }
    if ($stored['folders'] -is [hashtable]) {
        foreach ($server in $stored['folders'].Keys) {
            $entry = $stored['folders'][$server]
            if ($entry -isnot [hashtable] -or -not $entry['local']) { continue }
            $known = [System.Collections.Generic.List[long]]::new()
            foreach ($uid in @($entry['known'])) { if ($null -ne $uid) { $known.Add([long] $uid) } }
            $state.folders[$server] = @{
                local = [string] $entry['local']
                special = [string] $entry['special']
                uidValidity = [long] $entry['uidValidity']
                lastUid = [long] $entry['lastUid']
                modSeq = [long] $entry['modSeq']
                known = $known
                backfill = [bool] $entry['backfill']
            }
        }
    }
    $state
}

function Save-MailState([string] $Folder, [hashtable] $State) {
    Set-C4KText (Get-MailStatePath $Folder) ($State | ConvertTo-Json -Depth 10 -Compress)
}

function Get-MailLock([string] $Folder) {
    $lock = Read-MailJson (Join-MailPath (Join-MailPath $Folder '.mail') 'lock')
    if ($lock -is [hashtable] -and $lock['machine']) { $lock } else { $null }
}

function Test-MailLockedElsewhere([string] $Folder) {
    $lock = Get-MailLock $Folder
    if ($null -eq $lock -or [string] $lock['machine'] -ieq $env:COMPUTERNAME) { return $false }
    $last = Get-MailInstant $lock['lastTick']
    $null -ne $last -and ((Get-MailNow) - $last).TotalMinutes -lt $script:MailLockMinutes
}

function Test-MailLockOwned([string] $Folder) {
    $lock = Get-MailLock $Folder
    $null -ne $lock -and [string] $lock['machine'] -ieq $env:COMPUTERNAME
}

function Write-MailLock([string] $Folder) {
    $lock = @{ machine = $env:COMPUTERNAME; lastTick = (Get-MailNow).ToString('o', [Globalization.CultureInfo]::InvariantCulture) }
    Set-C4KText (Join-MailPath (Join-MailPath $Folder '.mail') 'lock') ($lock | ConvertTo-Json -Compress)
}

function Set-MailFlagged([hashtable] $State, [string] $Server, [long] $Uid, [bool] $Flagged) {
    $key = "$Server/$Uid"
    if ($Flagged) { if (-not $State.flagged.Contains($key)) { $State.flagged.Add($key) } }
    else { [void] $State.flagged.Remove($key) }
}

# ---- budget ----

function New-MailBudget { @{ fetches = 0; deadline = (Get-MailNow).AddSeconds($script:MailMaxSeconds) } }

function Test-MailBudgetSpent([hashtable] $Budget) { $Budget.fetches -ge $script:MailMaxFetches -or (Get-MailNow) -ge $Budget.deadline }

function Test-MailDue([string] $Folder, [hashtable] $Options, [hashtable] $State) {
    if ($script:MailCheckRequested.Contains($Folder)) { return $true }
    if ($null -ne $State.progress) { return $true }
    $last = Get-MailInstant $State.lastCheck
    if ($null -eq $last) { return $true }
    $refresh = ConvertTo-MailInterval $Options['refresh']
    $null -ne $refresh -and ((Get-MailNow) - $last) -ge $refresh
}

# ---- folders and files ----

function Get-MailUniqueLocal([string] $Name, [System.Collections.Generic.HashSet[string]] $Used) {
    $candidate = $Name
    for ($n = 2; $Used.Contains($candidate); $n++) { $candidate = "$Name ($n)" }
    [void] $Used.Add($candidate)
    $candidate
}

# Server folders -> local folders in state.folders. Known folders keep their local name; new ones get a free name
# ("Wysłane", then "Wysłane (2)"); a folder renamed on the server (one vanished folder with the same UIDVALIDITY)
# moves its local folder. A folder that is gone from the server is forgotten, but its local name stays reserved in
# state.retired: its files are never adopted by a later folder (whose "removed" would delete them).
function Update-MailFolderMap([string] $Folder, [hashtable] $State, [object[]] $Folders) {
    $map = $State.folders
    $present = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($info in $Folders) { [void] $present.Add([string] $info.serverName) }
    $vanished = [System.Collections.Generic.List[string]]::new()
    foreach ($server in @($map.Keys)) { if (-not $present.Contains($server)) { $vanished.Add($server) } }
    $used = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void] $used.Add('.mail')
    foreach ($name in $State.retired) { [void] $used.Add($name) }
    foreach ($server in @($map.Keys)) { [void] $used.Add($map[$server].local) }

    foreach ($info in $Folders) {
        $server = [string] $info.serverName
        if ($map.ContainsKey($server)) { $map[$server].special = [string] $info.special; continue }
        $validity = [long] $info.uidValidity
        $renamed = @($vanished | Where-Object { $validity -ne 0 -and $map[$_].uidValidity -eq $validity })
        $sameValidity = @($Folders | Where-Object { [long] $_.uidValidity -eq $validity -and -not $map.ContainsKey([string] $_.serverName) })
        if ($renamed.Count -eq 1 -and $sameValidity.Count -eq 1) {
            $entry = $map[$renamed[0]]
            [void] $used.Remove($entry.local)
            $local = Get-MailUniqueLocal ([string] $info.localName) $used
            if ($entry.local -ne $local) {
                $from = Join-MailPath $Folder $entry.local
                if ($null -ne (Get-C4KItem $from) -and $null -eq (Get-C4KItem (Join-MailPath $Folder $local))) { Move-C4KItem $from (Join-MailPath $Folder $local) }
                else { [void] $used.Remove($local); $local = $entry.local; [void] $used.Add($local) }
            }
            $entry.local = $local
            $map.Remove($renamed[0])
            [void] $vanished.Remove($renamed[0])
            # Flags stored under the old server name follow the folder.
            for ($i = 0; $i -lt $State.flagged.Count; $i++) {
                if ($State.flagged[$i].StartsWith($renamed[0] + '/')) { $State.flagged[$i] = $server + $State.flagged[$i].Substring($renamed[0].Length) }
            }
        }
        else {
            $local = Get-MailUniqueLocal ([string] $info.localName) $used
            $entry = @{ local = $local; uidValidity = 0; lastUid = 0; modSeq = 0; known = [System.Collections.Generic.List[long]]::new(); backfill = $true }
        }
        $entry.special = [string] $info.special
        $map[$server] = $entry
    }

    foreach ($server in $vanished) {
        if (-not $State.retired.Contains($map[$server].local)) { $State.retired.Add($map[$server].local) }
        $map.Remove($server)
    }
    foreach ($server in $map.Keys) { New-C4KDirectory (Join-MailPath $Folder $map[$server].local) }
}

# The synced messages of a local folder ("[uid N].eml" at the end; "[stary]" files are not ours). Listing errors
# propagate, so a folder that cannot be listed is skipped for this tick instead of looking empty.
# Returns @{ files = uid -> name; dupes = uid -> all names when one uid has several files }; names in ordinal order.
function Get-MailLocalFiles([string] $Dir) {
    $files = @{}
    $dupes = @{}
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in @(Get-C4KChildItem $Dir)) {
        if ($entry.kind -eq 'file' -and $entry.name -match $script:MailUidPattern) { $names.Add([string] $entry.name) }
    }
    $names.Sort([StringComparer]::Ordinal)
    foreach ($name in $names) {
        [void] ($name -match $script:MailUidPattern)
        $uid = [long] $Matches[1]
        if ($files.ContainsKey($uid)) {
            if (-not $dupes.ContainsKey($uid)) { $dupes[$uid] = [System.Collections.Generic.List[string]]::new(); $dupes[$uid].Add($files[$uid]) }
            $dupes[$uid].Add($name)
            continue
        }
        $files[$uid] = $name
    }
    @{ files = $files; dupes = $dupes }
}

# A free file name in $Dir: $Name, or " (2)", " (3)"… inserted before " [uid N]" (or before ".eml"). Never overwrites.
function Get-MailFreeName([string] $Dir, [string] $Name) {
    if ($null -eq (Get-C4KItem (Join-MailPath $Dir $Name))) { return $Name }
    $cut = $Name.IndexOf(' [uid ')
    if ($cut -lt 0) { $cut = $Name.Length - 4 }
    for ($n = 2; ; $n++) {
        $candidate = $Name.Substring(0, $cut) + " ($n)" + $Name.Substring($cut)
        if ($null -eq (Get-C4KItem (Join-MailPath $Dir $candidate))) { return $candidate }
    }
}

function Rename-MailFile([string] $Dir, [string] $Name, [string] $Target) {
    $free = Get-MailFreeName $Dir $Target
    Move-C4KItem (Join-MailPath $Dir $Name) (Join-MailPath $Dir $free)
    $free
}

function Save-MailMessage([hashtable] $Account, [string] $Server, [string] $Dir, $Header, [hashtable] $Files, [hashtable] $State) {
    $uid = [long] $Header.uid
    $flags = @($Header.flags)
    $seen = $flags -contains 'seen'
    $name = Get-MailFreeName $Dir ($(if ($seen) { '' } else { $script:MailUnread }) + [string] $Header.fileName)
    [void] (Save-C4KMail $Account $Server $uid (Join-MailPath $Dir $name))
    $Files[$uid] = $name
    Set-MailFlagged $State $Server $uid ($flags -contains 'flagged')
    -not $seen
}

# One uid with several files: when they are only the read and the unread variant of one name, keep the one that
# matches the server's seen flag and delete the other; anything else stays (the first name in ordinal order is used).
function Resolve-MailDuplicate([string] $Dir, [long] $Uid, [bool] $Seen, [hashtable] $Local) {
    if (-not $Local.dupes.ContainsKey($Uid)) { return }
    $names = @($Local.dupes[$Uid])
    $bases = @($names | ForEach-Object { if ($_.StartsWith($script:MailUnread)) { $_.Substring($script:MailUnread.Length) } else { $_ } } | Select-Object -Unique)
    if ($names.Count -ne 2 -or $bases.Count -ne 1) { return }
    $keep = if ($Seen) { $bases[0] } else { $script:MailUnread + $bases[0] }
    if ($names -cnotcontains $keep) { return }
    foreach ($name in $names) { if ($name -cne $keep) { Remove-C4KItem (Join-MailPath $Dir $name) } }
    $Local.files[$Uid] = $keep
    $Local.dupes.Remove($Uid)
}

# Local drafts (files without "[uid N]") that are new or whose modification time differs from the one recorded at
# their last upload go to the server's Drafts folder (APPEND); the previous server copy is deleted. Their uids join
# "known", so sync does not download them back.
function Push-MailDrafts([string] $Dir, [hashtable] $Account, [string] $Server, [hashtable] $Entry, [hashtable] $State) {
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @(Get-C4KChildItem $Dir)) {
        if ($file.kind -ne 'file' -or $file.name -notmatch '\.eml$' -or $file.name -match $script:MailUidPattern) { continue }
        [void] $names.Add($file.name)
        $old = $State.drafts[$file.name]
        $modified = Format-MailInstant $file.modified
        if ($null -ne $old -and $old.modified -eq $modified) { continue }
        $uid = Add-C4KMail $Account $Server (Join-MailPath $Dir $file.name) @('draft', 'seen')
        if ($null -ne $old -and $old.uid -gt 0) { Remove-C4KMail $Account $Server @([long] $old.uid); [void] $Entry.known.Remove([long] $old.uid) }
        $State.drafts[$file.name] = @{ uid = $(if ($null -ne $uid) { [long] $uid } else { 0 }); modified = $modified }
        if ($null -ne $uid -and -not $Entry.known.Contains([long] $uid)) { $Entry.known.Add([long] $uid) }
    }
    foreach ($name in @($State.drafts.Keys)) { if (-not $names.Contains($name)) { $State.drafts.Remove($name) } }
}

function Sync-MailFolder([string] $Folder, [hashtable] $Account, [hashtable] $State, $Info, [hashtable] $Budget, [string] $Since) {
    $out = @{ more = $false; newUnread = 0; remaining = 0 }
    $server = [string] $Info.serverName
    $entry = $State.folders[$server]
    $dir = Join-MailPath $Folder $entry.local
    $known = $entry.known
    if ($entry.lastUid -eq 0 -and $known.Count -eq 0) { $entry.backfill = $true }

    if ($entry.special -eq 'drafts') { Push-MailDrafts $dir $Account $server $entry $State }

    $local = Get-MailLocalFiles $dir
    $files = $local.files
    $sync = Sync-C4KMail -Account $Account -Folder $server -UidValidity $entry.uidValidity -LastUid $entry.lastUid -ModSeq $entry.modSeq -Since $Since -KnownUids $known.ToArray()

    if ($sync.uidValidityChanged) {
        # The server renumbered the folder: keep the old files under "[stary]" and start again.
        foreach ($uid in @($files.Keys)) {
            $names = if ($local.dupes.ContainsKey($uid)) { @($local.dupes[$uid]) } else { @($files[$uid]) }
            foreach ($name in $names) { [void] (Rename-MailFile $dir $name ($name.Substring(0, $name.Length - 4) + ' [stary].eml')) }
        }
        $local = @{ files = @{}; dupes = @{} }
        $files = $local.files
        $known.Clear()
        $entry.lastUid = 0
        $entry.modSeq = 0
        $entry.backfill = $true
        foreach ($key in @($State.flagged | Where-Object { $_.StartsWith($server + '/') })) { [void] $State.flagged.Remove($key) }
    }
    $entry.uidValidity = [long] $sync.uidValidity
    $entry.modSeq = [long] $sync.modSeq

    foreach ($uid in @($sync.removed)) {
        $uid = [long] $uid
        $names = if ($local.dupes.ContainsKey($uid)) { @($local.dupes[$uid]) } elseif ($files.ContainsKey($uid)) { @($files[$uid]) } else { @() }
        foreach ($name in $names) { Remove-C4KItem (Join-MailPath $dir $name) }
        $files.Remove($uid)
        $local.dupes.Remove($uid)
        [void] $known.Remove($uid)
        Set-MailFlagged $State $server $uid $false
    }

    # Without CONDSTORE these are the current flags of every known message: rename only on a real change.
    foreach ($change in @($sync.flagChanges)) {
        $uid = [long] $change.uid
        $flags = @($change.flags)
        $seen = $flags -contains 'seen'
        Set-MailFlagged $State $server $uid ($flags -contains 'flagged')
        Resolve-MailDuplicate $dir $uid $seen $local
        if (-not $files.ContainsKey($uid)) { continue }
        $name = $files[$uid]
        $unread = $name.StartsWith($script:MailUnread)
        $target = $null
        if ($seen -and $unread) { $target = $name.Substring($script:MailUnread.Length) }
        elseif (-not $seen -and -not $unread) { $target = $script:MailUnread + $name }
        if ($target) { $files[$uid] = Rename-MailFile $dir $name $target }
    }

    $draftUids = @($State.drafts.Values | ForEach-Object { [long] $_.uid })
    $missing = @($known | Where-Object { -not $files.ContainsKey([long] $_) -and $draftUids -notcontains [long] $_ })

    $fresh = @($sync['new'] | Sort-Object { [long] $_.uid })
    for ($i = 0; $i -lt $fresh.Count; $i++) {
        $header = $fresh[$i]
        $uid = [long] $header.uid
        if ($files.ContainsKey($uid)) {
            # Saved by a tick that stopped before state.json was written: adopt the file, its flags follow next time.
            if (-not $known.Contains($uid)) { $known.Add($uid) }
            if ($uid -gt $entry.lastUid) { $entry.lastUid = $uid }
            continue
        }
        if (Test-MailBudgetSpent $Budget) { $out.more = $true; $out.remaining = $fresh.Count - $i; break }
        $isUnread = Save-MailMessage $Account $server $dir $header $files $State
        $Budget.fetches++
        if (-not $known.Contains($uid)) { $known.Add($uid) }
        if ($uid -gt $entry.lastUid) { $entry.lastUid = $uid }
        if ($isUnread -and -not $entry.backfill) { $out.newUnread++ }
    }

    # A known message whose file is gone locally is downloaded again (sync never deletes on the server for it).
    if ($missing.Count -gt 0 -and -not $out.more) {
        $lowest = ($missing | Measure-Object -Minimum).Minimum
        $again = Sync-C4KMail -Account $Account -Folder $server -UidValidity $entry.uidValidity -LastUid ([long] $lowest - 1) -Since $Since
        $headers = @{}
        foreach ($header in @($again['new'])) { $headers[[long] $header.uid] = $header }
        foreach ($uid in $missing) {
            $uid = [long] $uid
            if (-not $headers.ContainsKey($uid)) { [void] $known.Remove($uid); continue }  # outside "since": forget it
            if (Test-MailBudgetSpent $Budget) { $out.more = $true; $out.remaining++; continue }
            [void] (Save-MailMessage $Account $server $dir $headers[$uid] $files $State)
            $Budget.fetches++
        }
    }

    # The first download of a folder ends when nothing is left: from then on new unread mail notifies.
    if (-not $out.more) { $entry.backfill = $false }
    $out
}

# One bounded pass over an account. Errors land in state.json (-32010 = login needed: background ticks skip the
# account until an interactive check succeeds). A folder that fails for another reason is skipped for this pass;
# the others still sync. Returns more (work left), newUnread and the Inbox folder.
function Sync-MailAccount([string] $Folder, [hashtable] $Options, [hashtable] $State, [hashtable] $Budget, [bool] $Interactive) {
    $result = @{ more = $false; newUnread = 0; inbox = $null }
    $account = New-MailAccountParam $Options $Interactive
    New-C4KDirectory (Join-MailPath $Folder '.mail') -Hidden
    Write-MailLock $Folder
    $State.lastCheck = (Get-MailNow).ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    $since = ConvertTo-MailSince $Options['since']
    $remaining = 0
    $failures = [System.Collections.Generic.List[string]]::new()
    try {
        $folders = @(Get-C4KMailFolders $account)
        Update-MailFolderMap $Folder $State $folders
        $ordered = @($folders | Sort-Object { if ($_.special -eq 'inbox') { 0 } else { 1 } })
        foreach ($info in $ordered) {
            if (Test-MailBudgetSpent $Budget) { $result.more = $true; break }
            $r = $null
            try {
                $r = Sync-MailFolder $Folder $account $State $info $Budget $since
            }
            catch {
                $code = if ($_.Exception.Data.Contains('Code')) { [int] $_.Exception.Data['Code'] } else { 0 }
                if ($code -in -32010, -32011) { throw }
                $failures.Add("$($info.serverName): $($_.Exception.Message)")
                Write-C4KLog "mail folder $($info.serverName) of $Folder skipped: $($_.Exception.Message)"
            }
            finally { Save-MailState $Folder $State }
            if ($null -eq $r) { continue }
            if ($r.more) { $result.more = $true }
            $remaining += $r.remaining
            if ([string] $info.special -notin 'trash', 'junk', 'sent', 'drafts') { $result.newUnread += $r.newUnread }
        }
        $State.authRequired = $false
        if ($failures.Count -gt 0) { $State.error = $failures -join '; ' }
        else { $State.error = $null; $State.lastSuccess = $State.lastCheck }
    }
    catch {
        $code = if ($_.Exception.Data.Contains('Code')) { [int] $_.Exception.Data['Code'] } else { 0 }
        if ($code -eq -32010) { $State.authRequired = $true; $State.error = 'wymaga logowania' }
        elseif ($code -eq -32011) { $State.error = 'certyfikat serwera nie jest zaufany' }
        else { $State.error = [string] $_.Exception.Message }
        $result.more = $false
        Write-C4KLog "mail sync of $Folder failed: $($State.error)"
    }

    if ($result.more) {
        $done = 0
        foreach ($server in $State.folders.Keys) { $done += $State.folders[$server].known.Count }
        $State.progress = @{ done = $done; total = $done + [Math]::Max($remaining, 1) }
    }
    else { $State.progress = $null }
    Save-MailState $Folder $State
    foreach ($server in $State.folders.Keys) {
        if ($State.folders[$server].special -eq 'inbox') { $result.inbox = Join-MailPath $Folder $State.folders[$server].local }
    }
    $result
}

# Sync-MailAccount guarded per account: whatever fails is logged and, when possible, recorded in that account's own
# state; the caller goes on with the next account.
function Invoke-MailAccountSafely([string] $Path, [hashtable] $Options, [hashtable] $State, [hashtable] $Budget, [bool] $Interactive) {
    try {
        return Sync-MailAccount $Path $Options $State $Budget $Interactive
    }
    catch {
        $message = [string] $_.Exception.Message
        Write-C4KLog "mail account $Path failed: $message"
        try { $State.error = $message; $State.progress = $null; Save-MailState $Path $State } catch { Write-C4KLog "mail state of $Path not saved: $_" }
        return @{ more = $false; newUnread = 0; inbox = $null }
    }
}

# ---- entry points ----

# background.tick: every due account that is not waiting for a login and not synced by another computer. The lock of
# every account this computer owns is refreshed on each tick, so another computer keeps seeing it as taken even when
# "refresh" is longer than the lock's 10 minutes.
function Invoke-MailTick {
    $budget = New-MailBudget
    $more = $false
    $notes = [System.Collections.Generic.List[string]]::new()
    $target = $null
    $accounts = Get-MailAccounts
    $keep = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $accounts) {
        $path = [string] $path
        $options = Get-MailAccountConfig $path $null
        if ($null -eq $options) {
            $exists = try { $null -ne (Get-C4KItem $path) } catch { $true }
            if ($exists) { $keep.Add($path) }
            continue
        }
        $keep.Add($path)
        $state = $null
        $due = $false
        try {
            if (Test-MailLockedElsewhere $path) { continue }
            $state = Read-MailState $path
            if ($null -eq $state) { continue }
            $spent = Test-MailBudgetSpent $budget
            $due = -not $spent -and -not $state.authRequired -and (Test-MailDue $path $options $state)
            if (-not $due) {
                if ($spent) { $more = $true }
                if (Test-MailLockOwned $path) { Write-MailLock $path }
            }
        }
        catch {
            Write-C4KLog "mail account $path skipped: $($_.Exception.Message)"
            continue
        }
        if (-not $due) { continue }
        [void] $script:MailCheckRequested.Remove($path)
        $r = Invoke-MailAccountSafely $path $options $state $budget $false
        if ($r.more) { $more = $true }
        if ($r.newUnread -gt 0) {
            $notes.Add((Get-MailLeaf $path) + ': ' + (Format-MailNewCount $r.newUnread))
            if (-not $target) { $target = $r.inbox }
        }
    }
    if ($keep.Count -ne $accounts.Count) { Set-C4KStorage 'accounts' $keep.ToArray() }
    if ($notes.Count -gt 0) { Show-C4KNotification ($notes -join '; ') -Target $target }
    @{ more = $more }
}

# "Sprawdź teraz": interactive (the app may ask for the password), clears "wymaga logowania" first.
function Invoke-MailCheck([string] $Path, $Options) {
    [void] (Register-MailAccount $Path)
    $config = Get-MailAccountConfig $Path $Options
    if ($null -eq $config -or (Test-MailLockedElsewhere $Path)) { return }
    $state = Read-MailState $Path
    if ($null -eq $state) { return }
    $state.authRequired = $false
    [void] $script:MailCheckRequested.Remove($Path)
    $r = Invoke-MailAccountSafely $Path $config $state (New-MailBudget) $true
    if ($r.more) { Request-MailCheck $Path }
}
