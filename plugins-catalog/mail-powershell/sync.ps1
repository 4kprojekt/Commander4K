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
    }
}

function Get-MailStatePath([string] $Folder) { Join-MailPath (Join-MailPath $Folder '.mail') 'state.json' }

function Read-MailState([string] $Folder) {
    $state = New-MailState
    $stored = Read-MailJson (Get-MailStatePath $Folder)
    if ($stored -isnot [hashtable]) { return $state }
    $state.lastCheck = Format-MailInstant $stored['lastCheck']
    $state.lastSuccess = Format-MailInstant $stored['lastSuccess']
    $state.error = $stored['error']
    $state.authRequired = [bool] $stored['authRequired']
    $state.progress = $stored['progress']
    foreach ($item in @($stored['flagged'])) { if ($item) { $state.flagged.Add([string] $item) } }
    if ($stored['drafts'] -is [hashtable]) { $state.drafts = $stored['drafts'] }
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
            }
        }
    }
    $state
}

function Save-MailState([string] $Folder, [hashtable] $State) {
    Set-C4KText (Get-MailStatePath $Folder) ($State | ConvertTo-Json -Depth 10)
}

function Test-MailLockedElsewhere([string] $Folder) {
    $lock = Read-MailJson (Join-MailPath (Join-MailPath $Folder '.mail') 'lock')
    if ($lock -isnot [hashtable] -or -not $lock['machine']) { return $false }
    if ([string] $lock['machine'] -ieq $env:COMPUTERNAME) { return $false }
    $last = Get-MailInstant $lock['lastTick']
    $null -ne $last -and ((Get-MailNow) - $last).TotalMinutes -lt $script:MailLockMinutes
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

# ---- folders ----

function Get-MailUniqueLocal([string] $Name, [System.Collections.Generic.HashSet[string]] $Used) {
    $candidate = $Name
    for ($n = 2; $Used.Contains($candidate); $n++) { $candidate = "$Name ($n)" }
    [void] $Used.Add($candidate)
    $candidate
}

# Server folders -> local folders in state.folders. Known folders keep their local name; new ones get a free name
# ("Wysłane", then "Wysłane (2)"); a folder renamed on the server (same UIDVALIDITY) moves its local folder.
function Update-MailFolderMap([string] $Folder, [hashtable] $State, [object[]] $Folders) {
    $map = $State.folders
    $present = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($info in $Folders) { [void] $present.Add([string] $info.serverName) }
    $vanished = [System.Collections.Generic.List[string]]::new()
    foreach ($server in @($map.Keys)) { if (-not $present.Contains($server)) { $vanished.Add($server) } }
    $used = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void] $used.Add('.mail')
    foreach ($server in @($map.Keys)) { if ($present.Contains($server)) { [void] $used.Add($map[$server].local) } }

    foreach ($info in $Folders) {
        $server = [string] $info.serverName
        if ($map.ContainsKey($server)) { $map[$server].special = [string] $info.special; continue }
        $validity = [long] $info.uidValidity
        $renamed = @($vanished | Where-Object { $validity -ne 0 -and $map[$_].uidValidity -eq $validity })
        $sameValidity = @($Folders | Where-Object { [long] $_.uidValidity -eq $validity -and -not $map.ContainsKey([string] $_.serverName) })
        $local = Get-MailUniqueLocal ([string] $info.localName) $used
        if ($renamed.Count -eq 1 -and $sameValidity.Count -eq 1) {
            $entry = $map[$renamed[0]]
            $from = Join-MailPath $Folder $entry.local
            if ($entry.local -ne $local -and $null -ne (Get-C4KItem $from) -and $null -eq (Get-C4KItem (Join-MailPath $Folder $local))) {
                Move-C4KItem $from (Join-MailPath $Folder $local)
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
            $entry = @{ local = $local; uidValidity = 0; lastUid = 0; modSeq = 0; known = [System.Collections.Generic.List[long]]::new() }
        }
        $entry.special = [string] $info.special
        $map[$server] = $entry
    }

    # Gone from the server: forget the mapping, keep the files.
    foreach ($server in $vanished) { $map.Remove($server) }
    foreach ($server in $map.Keys) { New-C4KDirectory (Join-MailPath $Folder $map[$server].local) }
}

# uid -> file name of the synced messages in a local folder ("[uid N].eml" at the end; "[stary]" files are not ours).
function Get-MailLocalFiles([string] $Dir) {
    $files = @{}
    $entries = try { @(Get-C4KChildItem $Dir) } catch { @() }
    foreach ($entry in $entries) {
        if ($entry.kind -eq 'file' -and $entry.name -match $script:MailUidPattern) { $files[[long] $Matches[1]] = [string] $entry.name }
    }
    $files
}

function Save-MailMessage([hashtable] $Account, [string] $Server, [string] $Dir, $Header, [hashtable] $Files, [hashtable] $State) {
    $uid = [long] $Header.uid
    $flags = @($Header.flags)
    $seen = $flags -contains 'seen'
    $name = $(if ($seen) { '' } else { $script:MailUnread }) + [string] $Header.fileName
    [void] (Save-C4KMail $Account $Server $uid (Join-MailPath $Dir $name))
    $Files[$uid] = $name
    Set-MailFlagged $State $Server $uid ($flags -contains 'flagged')
    -not $seen
}

# Local drafts (files without "[uid N]") that are new or changed since the last check go to the server's Drafts
# folder (APPEND); the previous server copy is deleted. Their uids join "known", so sync does not download them back.
function Push-MailDrafts([string] $Dir, [hashtable] $Account, [string] $Server, [hashtable] $Entry, [hashtable] $State, $PreviousCheck) {
    $entries = try { @(Get-C4KChildItem $Dir) } catch { @() }
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $entries) {
        if ($file.kind -ne 'file' -or $file.name -notmatch '\.eml$' -or $file.name -match $script:MailUidPattern) { continue }
        [void] $names.Add($file.name)
        $old = $State.drafts[$file.name]
        $modified = Get-MailInstant $file.modified
        $changed = $null -eq $old -or $null -eq $PreviousCheck -or ($null -ne $modified -and $modified -gt $PreviousCheck)
        if (-not $changed) { continue }
        $uid = Add-C4KMail $Account $Server (Join-MailPath $Dir $file.name) @('draft', 'seen')
        if ($null -ne $old -and [long] $old -gt 0) { Remove-C4KMail $Account $Server @([long] $old) }
        $State.drafts[$file.name] = $(if ($null -ne $uid) { [long] $uid } else { 0 })
        if ($null -ne $uid -and -not $Entry.known.Contains([long] $uid)) { $Entry.known.Add([long] $uid) }
    }
    foreach ($name in @($State.drafts.Keys)) { if (-not $names.Contains($name)) { $State.drafts.Remove($name) } }
}

function Sync-MailFolder([string] $Folder, [hashtable] $Account, [hashtable] $State, $Info, [hashtable] $Budget, [string] $Since, $PreviousCheck) {
    $out = @{ more = $false; newUnread = 0; remaining = 0 }
    $server = [string] $Info.serverName
    $entry = $State.folders[$server]
    $dir = Join-MailPath $Folder $entry.local
    $known = $entry.known
    $initial = $entry.lastUid -eq 0 -and $known.Count -eq 0

    if ($entry.special -eq 'drafts') { Push-MailDrafts $dir $Account $server $entry $State $PreviousCheck }

    $files = Get-MailLocalFiles $dir
    $sync = Sync-C4KMail -Account $Account -Folder $server -UidValidity $entry.uidValidity -LastUid $entry.lastUid -ModSeq $entry.modSeq -Since $Since -KnownUids $known.ToArray()

    if ($sync.uidValidityChanged) {
        # The server renumbered the folder: keep the old files under "[stary]" and start again.
        foreach ($uid in @($files.Keys)) {
            $name = $files[$uid]
            Move-C4KItem (Join-MailPath $dir $name) (Join-MailPath $dir ($name.Substring(0, $name.Length - 4) + ' [stary].eml'))
        }
        $files = @{}
        $known.Clear()
        $entry.lastUid = 0
        $entry.modSeq = 0
        $initial = $true
        foreach ($key in @($State.flagged | Where-Object { $_.StartsWith($server + '/') })) { [void] $State.flagged.Remove($key) }
    }
    $entry.uidValidity = [long] $sync.uidValidity
    $entry.modSeq = [long] $sync.modSeq

    foreach ($uid in @($sync.removed)) {
        $uid = [long] $uid
        if ($files.ContainsKey($uid)) { Remove-C4KItem (Join-MailPath $dir $files[$uid]); $files.Remove($uid) }
        [void] $known.Remove($uid)
        Set-MailFlagged $State $server $uid $false
    }

    # Without CONDSTORE these are the current flags of every known message: rename only on a real change.
    foreach ($change in @($sync.flagChanges)) {
        $uid = [long] $change.uid
        $flags = @($change.flags)
        Set-MailFlagged $State $server $uid ($flags -contains 'flagged')
        if (-not $files.ContainsKey($uid)) { continue }
        $name = $files[$uid]
        $unread = $name.StartsWith($script:MailUnread)
        $target = $null
        if (($flags -contains 'seen') -and $unread) { $target = $name.Substring($script:MailUnread.Length) }
        elseif (-not ($flags -contains 'seen') -and -not $unread) { $target = $script:MailUnread + $name }
        if ($target) { Move-C4KItem (Join-MailPath $dir $name) (Join-MailPath $dir $target); $files[$uid] = $target }
    }

    $draftUids = @($State.drafts.Values | ForEach-Object { [long] $_ })
    $missing = @($known | Where-Object { -not $files.ContainsKey([long] $_) -and $draftUids -notcontains [long] $_ })

    $fresh = @($sync['new'] | Sort-Object { [long] $_.uid })
    for ($i = 0; $i -lt $fresh.Count; $i++) {
        if (Test-MailBudgetSpent $Budget) { $out.more = $true; $out.remaining = $fresh.Count - $i; break }
        $header = $fresh[$i]
        $uid = [long] $header.uid
        $isUnread = Save-MailMessage $Account $server $dir $header $files $State
        $Budget.fetches++
        if (-not $known.Contains($uid)) { $known.Add($uid) }
        if ($uid -gt $entry.lastUid) { $entry.lastUid = $uid }
        if ($isUnread -and -not $initial) { $out.newUnread++ }
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
    $out
}

# One bounded pass over an account. Errors land in state.json (-32010 = login needed: background ticks skip the
# account until an interactive check succeeds). Returns more (work left), newUnread and the Inbox folder.
function Sync-MailAccount([string] $Folder, [hashtable] $Options, [hashtable] $State, [hashtable] $Budget, [bool] $Interactive) {
    $result = @{ more = $false; newUnread = 0; inbox = $null }
    $account = New-MailAccountParam $Options $Interactive
    $previousCheck = Get-MailInstant $State.lastCheck
    New-C4KDirectory (Join-MailPath $Folder '.mail') -Hidden
    Write-MailLock $Folder
    $State.lastCheck = (Get-MailNow).ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    $since = ConvertTo-MailSince $Options['since']
    $remaining = 0
    try {
        $folders = @(Get-C4KMailFolders $account)
        Update-MailFolderMap $Folder $State $folders
        $ordered = @($folders | Sort-Object { if ($_.special -eq 'inbox') { 0 } else { 1 } })
        foreach ($info in $ordered) {
            if (Test-MailBudgetSpent $Budget) { $result.more = $true; break }
            $r = Sync-MailFolder $Folder $account $State $info $Budget $since $previousCheck
            if ($r.more) { $result.more = $true }
            $remaining += $r.remaining
            if ([string] $info.special -notin 'trash', 'junk', 'sent', 'drafts') { $result.newUnread += $r.newUnread }
            Save-MailState $Folder $State
        }
        $State.error = $null
        $State.authRequired = $false
        $State.lastSuccess = $State.lastCheck
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

# ---- entry points ----

# background.tick: every due account that is not waiting for a login and not synced by another computer.
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
        if (Test-MailBudgetSpent $budget) { $more = $true; continue }
        $state = Read-MailState $path
        if ($state.authRequired) { continue }
        if (-not (Test-MailDue $path $options $state)) { continue }
        if (Test-MailLockedElsewhere $path) { continue }
        [void] $script:MailCheckRequested.Remove($path)
        $r = Sync-MailAccount $path $options $state $budget $false
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
    $state.authRequired = $false
    [void] $script:MailCheckRequested.Remove($Path)
    $r = Sync-MailAccount $Path $config $state (New-MailBudget) $true
    if ($r.more) { Request-MailCheck $Path }
}
