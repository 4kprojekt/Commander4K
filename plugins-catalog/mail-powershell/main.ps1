# Commander4K official plugin: mail (IMAP/SMTP through the app, messages as .eml files).
# The app does IMAP, SMTP, MIME and keeps the passwords; this plugin only decides what to sync where (sync.ps1).
# Account folder: a c4k.dir with { "preview": { "plugin": "official.mail", "options": { imap, smtp, user, refresh, since } } }.

. "$PSScriptRoot/c4k.ps1"
. "$PSScriptRoot/sync.ps1"
. "$PSScriptRoot/compose.ps1"

# Plain text about the account, read from state.json and the files only (no server connection).
function Get-MailAccountStatus([string] $Path, $Options) {
    $config = Get-MailAccountConfig $Path $Options
    $state = Read-MailState $Path
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('Konto: ' + (Get-MailLeaf $Path))
    if ($null -eq $state) {
        $lines.Add('Nie można odczytać .mail\state.json: synchronizacja tego konta jest wstrzymana.')
        return $lines -join "`n"
    }
    if ($null -eq $config) {
        $lines.Add('Brak konfiguracji: dodaj imap i user w opcjach c4k.dir.')
        return $lines -join "`n"
    }
    $lines.Add('Użytkownik: ' + $config['user'] + ' (' + $config['imap'] + ')')
    $last = Get-MailInstant $state.lastCheck
    $lines.Add('Ostatnie sprawdzenie: ' + $(if ($last) { $last.ToLocalTime().ToString('yyyy-MM-dd HH:mm') } else { 'jeszcze nie' }))
    if ($state.authRequired) { $lines.Add('Stan: wymaga logowania') }
    elseif ($state.error) { $lines.Add('Błąd: ' + $state.error) }
    if ($state.progress) { $lines.Add('Pobieram: ' + $state.progress.done + ' / ' + $state.progress.total) }
    foreach ($server in @($state.folders.Keys | Sort-Object)) {
        $local = $state.folders[$server].local
        try {
            $files = (Get-MailLocalFiles (Join-MailPath $Path $local)).files
            $unread = @($files.Values | Where-Object { $_.StartsWith($script:MailUnread) }).Count
            $lines.Add("  $local`: $($files.Count) (nieprzeczytane: $unread)")
        }
        catch { $lines.Add("  $local`: nie można odczytać folderu") }
    }
    $lines -join "`n"
}

function Get-MailAccountCard([string] $Path, $Options) {
    $config = Get-MailAccountConfig $Path $Options
    $state = Read-MailState $Path
    $items = [System.Collections.Generic.List[hashtable]]::new()
    $actions = [System.Collections.Generic.List[hashtable]]::new()
    $status = 'ok'
    if ($null -eq $state) {
        return @{ title = (Get-MailLeaf $Path); status = 'error'; items = @(@{ kind = 'text'; text = 'nie można odczytać .mail\state.json' }); actions = @() }
    }
    $inbox = $null
    foreach ($server in $state.folders.Keys) {
        if ($state.folders[$server].special -eq 'inbox') { $inbox = Join-MailPath $Path $state.folders[$server].local }
    }
    if ($inbox) {
        try {
            $files = (Get-MailLocalFiles $inbox).files
            $unread = @($files.Values | Where-Object { $_.StartsWith($script:MailUnread) }).Count
            $items.Add(@{ kind = 'value'; label = 'Nieprzeczytane'; value = [string] $unread })
        }
        catch { $status = 'warning' }
        $actions.Add(@{ label = 'Otwórz'; target = $inbox })
    }
    $last = Get-MailInstant $state.lastCheck
    if ($null -eq $config) { $status = 'warning'; $items.Add(@{ kind = 'text'; text = 'brak konfiguracji konta' }) }
    elseif ($state.authRequired) { $status = 'error'; $items.Add(@{ kind = 'text'; text = 'wymaga logowania' }) }
    elseif ($state.error) { $status = 'error'; $items.Add(@{ kind = 'text'; text = 'błąd: ' + $state.error }) }
    if ($last) { $items.Add(@{ kind = 'value'; label = 'Sprawdzono'; value = $last.ToLocalTime().ToString('HH:mm') }) }
    if ($state.progress -and $state.progress.total -gt 0) {
        $items.Add(@{ kind = 'progress'; label = 'Pobieranie'; fraction = [Math]::Min(1.0, [double] $state.progress.done / [double] $state.progress.total); text = "$($state.progress.done) / $($state.progress.total)" })
    }
    $actions.Add(@{ label = 'Sprawdź teraz'; target = (Get-C4KActionTarget 'check' @{ account = $Path }) })
    @{ title = (Get-MailLeaf $Path); status = $status; items = $items.ToArray(); actions = $actions.ToArray() }
}

# ---- reading a message ----

function ConvertTo-MailMarkdownLine([string] $Text) { [regex]::Replace($Text, '([\\`*_\[\]<>|~#!])', '\$1') }

function ConvertTo-MailMarkdownText([string] $Text) {
    $Text = $Text.Replace([string][char]0xA0, ' ').Replace("`r`n", "`n").Replace("`r", "`n")
    $lines = $Text -split "`n"
    $out = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = ConvertTo-MailMarkdownLine $lines[$i]
        $line = [regex]::Replace($line, '^(\s*)([-+])(\s)', '$1\$2$3')
        $line = [regex]::Replace($line, '^(\s*)(\d+)([.)])(\s)', '$1$2\$3$4')
        $line = [regex]::Replace($line, '^( +)', { param($m) [string]::new([char]0xA0, $m.Length) })
        $continues = $line.Trim() -ne '' -and $i + 1 -lt $lines.Count -and $lines[$i + 1].Trim() -ne ''
        $out.Add($(if ($continues) { $line + '  ' } else { $line }))
    }
    $out -join "`n"
}

function Format-MailDate($Value) {
    $instant = Get-MailInstant $Value
    if ($instant) { $instant.ToLocalTime().ToString('yyyy-MM-dd HH:mm') } else { '' }
}

# A file name for an attachment from a stranger: no folders, no reserved characters or names, no "..".
function Get-MailSafeFileName([string] $Name, [int] $Index) {
    $leaf = ($Name -split '[\\/]')[-1]
    $clean = (($leaf -replace '[:*?"<>|\x00-\x1f]', '_') -replace '\.{2,}', '.').Trim().TrimStart('.').TrimEnd('. ')
    if ($clean.Length -gt 100) { $clean = $clean.Substring(0, 100) }
    if (-not $clean -or $clean -match '^(con|prn|aux|nul|com\d|lpt\d)(\..*)?$') { $clean = "zalacznik-$Index" + $clean }
    $clean
}

function Get-MailFreeFileName([string] $Dir, [string] $Name) {
    if ($null -eq (Get-C4KItem (Join-MailPath $Dir $Name))) { return $Name }
    $dot = $Name.LastIndexOf('.')
    $stem = if ($dot -gt 0) { $Name.Substring(0, $dot) } else { $Name }
    $ext = if ($dot -gt 0) { $Name.Substring($dot) } else { '' }
    for ($n = 2; ; $n++) {
        $candidate = "$stem ($n)$ext"
        if ($null -eq (Get-C4KItem (Join-MailPath $Dir $candidate))) { return $candidate }
    }
}

function Get-MailSpecialFolder($Ctx, [string] $Special) {
    if (-not $Ctx -or -not $Ctx.state) { return $null }
    foreach ($server in $Ctx.state.folders.Keys) { if ($Ctx.state.folders[$server].special -eq $Special) { return $server } }
    $null
}

function Get-MailDraftsLocal($Ctx) {
    $server = Get-MailSpecialFolder $Ctx 'drafts'
    if ($server) { $Ctx.state.folders[$server].local } else { $script:MailDraftsFolder }
}

function Test-MailIsDraftFile($Ctx) { $null -ne $Ctx -and -not $Ctx.uid -and $Ctx.local -ieq (Get-MailDraftsLocal $Ctx) }

function New-MailMessageActions($Ctx, $Mail, [bool] $Unread) {
    $actions = [System.Collections.Generic.List[hashtable]]::new()
    if ($Ctx) {
        $actions.Add(@{ label = 'Odpowiedz'; target = (Get-C4KActionTarget 'reply'); shortcut = 'R' })
        $actions.Add(@{ label = 'Odpowiedz wszystkim'; target = (Get-C4KActionTarget 'replyAll'); shortcut = 'Shift+R' })
        $actions.Add(@{ label = 'Przekaż dalej'; target = (Get-C4KActionTarget 'forward'); shortcut = 'F' })
    }
    if ($Ctx -and $Ctx.uid -and $Ctx.server) {
        if ($Ctx.special -ne 'archive' -and (Get-MailSpecialFolder $Ctx 'archive')) { $actions.Add(@{ label = 'Archiwizuj'; target = (Get-C4KActionTarget 'archive'); shortcut = 'A' }) }
        $actions.Add(@{ label = 'Usuń'; target = (Get-C4KActionTarget 'delete'); shortcut = 'Delete' })
        if (-not $Unread) { $actions.Add(@{ label = 'Oznacz jako nieprzeczytaną'; target = (Get-C4KActionTarget 'unread'); shortcut = 'U' }) }
    }
    $shown = 0
    foreach ($attachment in @($Mail.attachments)) {
        if ($shown -ge 8) { break }
        $actions.Add(@{ label = 'Zapisz: ' + $attachment.fileName; target = (Get-C4KActionTarget 'saveAttachment' @{ i = [string] $attachment.index }) })
        $shown++
    }
    $actions.ToArray()
}

# The reading view of a message file. $MarkRead (Enter: a pinned view) marks an unread message read on the server and
# renames the file; the result then carries the new path. $EntryPath is the path the host shows now (or $null).
function Get-MailMessageResult([string] $Path, [bool] $MarkRead, [string] $Note, $EntryPath) {
    $ctx = Get-MailContext $Path
    $name = Get-MailLeaf $Path
    if (Test-MailIsDraftFile $ctx) {
        $form = Get-MailDraftForm $Path $null $Note $false
        if ($EntryPath -and $Path -eq $EntryPath) { $form.Remove('path') }
        return $form
    }

    $unread = $name.StartsWith($script:MailUnread)
    if ($MarkRead -and $unread -and $ctx -and $ctx.uid -and $ctx.server) {
        try {
            Set-C4KMailFlags $ctx.account $ctx.server @($ctx.uid) @('seen')
            $Path = Join-MailPath $ctx.dir (Rename-MailFile $ctx.dir $name $name.Substring($script:MailUnread.Length))
            $ctx = Get-MailContext $Path
            $unread = $false
        }
        catch { $Note = 'Nie oznaczono jako przeczytanej: ' + (Get-MailErrorText $_) }
    }

    try { $mail = Read-C4KMail $Path }
    catch { return @{ kind = 'text'; content = 'Nie można odczytać wiadomości: ' + (Get-MailErrorText $_) } }

    $markdown = [System.Collections.Generic.List[string]]::new()
    if ($Note) { $markdown.Add('*' + (ConvertTo-MailMarkdownLine $Note) + '*'); $markdown.Add('') }
    $header = [System.Collections.Generic.List[string]]::new()
    $header.Add('**Od:** ' + (ConvertTo-MailMarkdownLine (Format-MailAddress $mail.from)))
    $header.Add('**Do:** ' + (ConvertTo-MailMarkdownLine (Format-MailAddressList $mail.to)))
    if (@($mail.cc).Count -gt 0) { $header.Add('**DW:** ' + (ConvertTo-MailMarkdownLine (Format-MailAddressList $mail.cc))) }
    $header.Add('**Temat:** ' + (ConvertTo-MailMarkdownLine ([string] $mail.subject)))
    $header.Add('**Data:** ' + (Format-MailDate $mail.date))
    $markdown.Add(($header -join "  `n"))
    $attachments = @($mail.attachments)
    if ($attachments.Count -gt 0) {
        $markdown.Add('')
        $markdown.Add('**Załączniki:**')
        $markdown.Add('')
        foreach ($attachment in $attachments) {
            $kb = [Math]::Max(1, [Math]::Ceiling([double] $attachment.size / 1024))
            $markdown.Add('- ' + (ConvertTo-MailMarkdownLine ([string] $attachment.fileName)) + " ($kb KB)")
        }
    }
    $markdown.Add('')
    $markdown.Add('---')
    $markdown.Add('')
    $body = [string] $mail.bodyText
    if ($body.Length -gt 200000) { $body = $body.Substring(0, 200000) + "`n[…]" }
    $markdown.Add((ConvertTo-MailMarkdownText $body))

    $result = @{ kind = 'markdown'; content = ($markdown -join "`n"); actions = (New-MailMessageActions $ctx $mail $unread) }
    if ($null -eq $EntryPath -or $Path -ne $EntryPath) { $result.path = $Path }
    $result
}

# ---- actions on a message ----

# After a server-side move or delete the message has a new uid in the target folder. Find it among the target's new
# messages by name; sync adopts the file afterwards because the uid is in the name. $null when it cannot be told.
function Find-MailMovedUid($Ctx, [string] $TargetServer, [string] $FileName) {
    $entry = $Ctx.state.folders[$TargetServer]
    $base = { param($n) ((($n -replace '^● ', '') -replace ' \[uid \d+\]\.eml$', '') -replace ' \(\d+\)$', '') }
    $wanted = & $base $FileName
    $sync = Sync-C4KMail -Account $Ctx.account -Folder $TargetServer -UidValidity $entry.uidValidity -LastUid $entry.lastUid -KnownUids $entry.known.ToArray()
    $best = $null
    foreach ($header in @($sync['new'])) {
        if ((& $base ([string] $header.fileName)) -ceq $wanted -and ($null -eq $best -or [long] $header.uid -gt [long] $best.uid)) { $best = $header }
    }
    $best
}

# Moves the file into the target folder under its new uid and fixes the bookkeeping of both folders.
function Move-MailLocalFile($Ctx, [string] $Path, [string] $TargetServer, $Header) {
    $state = $Ctx.state
    $target = $state.folders[$TargetServer]
    $name = Get-MailLeaf $Path
    $targetDir = Join-MailPath $Ctx.root $target.local
    New-C4KDirectory $targetDir
    $prefix = if ($name.StartsWith($script:MailUnread)) { $script:MailUnread } else { '' }
    $free = Get-MailFreeName $targetDir ($prefix + [string] $Header.fileName)
    Move-C4KItem $Path (Join-MailPath $targetDir $free)
    $uid = [long] $Header.uid
    if (-not $target.known.Contains($uid)) { $target.known.Add($uid) }
    [void] $state.folders[$Ctx.server].known.Remove([long] $Ctx.uid)
    $flagged = $state.flagged.Contains("$($Ctx.server)/$($Ctx.uid)")
    Set-MailFlagged $state $Ctx.server $Ctx.uid $false
    if ($flagged) { Set-MailFlagged $state $TargetServer $uid $true }
    Save-MailState $Ctx.root $state
    Join-MailPath $targetDir $free
}

# delete (to Kosz, or for good inside Kosz) and archive.
function Invoke-MailMove($Ctx, [string] $Path, [string] $Kind) {
    if (-not $Ctx -or -not $Ctx.uid -or -not $Ctx.server) { throw 'Ta wiadomość nie pochodzi z synchronizacji konta.' }
    if ($null -eq $Ctx.state) { throw 'Nie można odczytać stanu konta (.mail\state.json).' }
    $name = Get-MailLeaf $Path
    if ($Kind -eq 'delete' -and $Ctx.special -eq 'trash') {
        Remove-C4KMail $Ctx.account $Ctx.server @($Ctx.uid)
        Remove-C4KItem $Path
        [void] $Ctx.state.folders[$Ctx.server].known.Remove([long] $Ctx.uid)
        Set-MailFlagged $Ctx.state $Ctx.server $Ctx.uid $false
        Save-MailState $Ctx.root $Ctx.state
        return @{ kind = 'text'; content = 'Wiadomość usunięta na stałe.' }
    }
    $target = if ($Kind -eq 'archive') { Get-MailSpecialFolder $Ctx 'archive' } else { Get-MailSpecialFolder $Ctx 'trash' }
    if ($Kind -eq 'archive') {
        if (-not $target) { throw 'To konto nie ma folderu archiwum.' }
        Move-C4KMail $Ctx.account $Ctx.server @($Ctx.uid) $target
    }
    else { Remove-C4KMail $Ctx.account $Ctx.server @($Ctx.uid) }

    $header = $null
    if ($target) {
        try { $header = Find-MailMovedUid $Ctx $target $name }
        catch { Write-C4KLog "moved message not found in ${target}: $(Get-MailErrorText $_)" }
    }
    if ($header) {
        $newPath = Move-MailLocalFile $Ctx $Path $target $header
        return Get-MailMessageResult $newPath $false $null $null
    }
    # Not found: drop the local copy; the next sync downloads it where it now is.
    Remove-C4KItem $Path
    [void] $Ctx.state.folders[$Ctx.server].known.Remove([long] $Ctx.uid)
    Save-MailState $Ctx.root $Ctx.state
    @{ kind = 'text'; content = 'Wiadomość przeniesiona; pojawi się w nowym folderze po następnej synchronizacji.' }
}

function Invoke-MailUnread($Ctx, [string] $Path) {
    if (-not $Ctx -or -not $Ctx.uid -or -not $Ctx.server) { throw 'Ta wiadomość nie pochodzi z synchronizacji konta.' }
    $name = Get-MailLeaf $Path
    if (-not $name.StartsWith($script:MailUnread)) {
        Set-C4KMailFlags $Ctx.account $Ctx.server @($Ctx.uid) @() @('seen')
        $Path = Join-MailPath $Ctx.dir (Rename-MailFile $Ctx.dir $name ($script:MailUnread + $name))
    }
    Get-MailMessageResult $Path $false $null $null
}

function Invoke-MailSaveAttachment([string] $Path, $ActionArgs) {
    $index = -1
    if (-not $ActionArgs -or -not [int]::TryParse([string] $ActionArgs['i'], [ref] $index) -or $index -lt 0) { throw 'Nie ma takiego załącznika.' }
    $mail = Read-C4KMail $Path
    $attachment = @($mail.attachments) | Where-Object { [int] $_.index -eq $index } | Select-Object -First 1
    if (-not $attachment) { throw 'Nie ma takiego załącznika.' }
    $other = [string] (Get-C4KContext).otherPath
    if (-not $other) { throw 'Otwórz w drugim panelu folder, do którego zapisać załącznik.' }
    $name = Get-MailFreeFileName $other (Get-MailSafeFileName ([string] $attachment.fileName) $index)
    $target = Join-MailPath $other $name
    Save-C4KMailAttachment $Path $index $target
    Show-C4KNotification "Zapisano: $name" -Target $target
}

function Invoke-MailMessageAction([string] $Path, [string] $Action, $ActionArgs, $Values) {
    $ctx = Get-MailContext $Path
    # Buttons of a draft's form.
    if (Test-MailIsDraftFile $ctx) { return Invoke-MailDraftAction $Path $Action $ActionArgs $Values }
    switch ($Action) {
        { $_ -in 'reply', 'replyAll', 'forward' } { return New-MailReplyDraft $Path $Action $ctx }
        'archive' { return Invoke-MailMove $ctx $Path 'archive' }
        'delete' { return Invoke-MailMove $ctx $Path 'delete' }
        'unread' { return Invoke-MailUnread $ctx $Path }
        'saveAttachment' { Invoke-MailSaveAttachment $Path $ActionArgs; return $null }
    }
    throw ('Nieznana akcja: ' + $Action)
}

Register-C4KHandler 'background.tick' { param($p) Invoke-MailTick }

Register-C4KHandler 'preview.render' {
    param($p)
    if ($p.preview -eq 'account') {
        $path = [string] $p.entry.path
        # Opening the account (Enter) or seeing it for the first time asks for a check on the next tick.
        if ((Register-MailAccount $path) -or $p.pinned) { Request-MailCheck $path }
        return @{ kind = 'text'; content = (Get-MailAccountStatus $path $p.options) }
    }
    $path = [string] $p.entry.path
    try { Get-MailMessageResult $path ([bool] $p.pinned) $null $path }
    catch { @{ kind = 'text'; content = 'Nie można pokazać wiadomości: ' + (Get-MailErrorText $_) } }
}

Register-C4KHandler 'preview.card' {
    param($p)
    if ($null -eq $p.entry) { return @{ title = 'Poczta'; items = @(); actions = @() } }
    $path = [string] $p.entry.path
    if (Register-MailAccount $path) { Request-MailCheck $path }
    Get-MailAccountCard $path $p.options
}

Register-C4KHandler 'preview.action' {
    param($p)
    if ($p.preview -eq 'message') {
        try { return Invoke-MailMessageAction ([string] $p.entry.path) ([string] $p.action) $p.args $p.values }
        catch { throw (Get-MailErrorText $_) }
    }
    if ($p.action -eq 'check') {
        $path = if ($p.args -and $p.args['account']) { [string] $p.args['account'] } else { [string] $p.entry.path }
        Invoke-MailCheck $path $p.options
    }
    $null
}

Register-C4KHandler 'preview.formChanged' {
    param($p)
    if ($p.preview -eq 'message') { Save-MailFormEdit ([string] $p.entry.path) $p.values }
    $null
}

Start-C4KPlugin
