# Commander4K official plugin: mail (IMAP/SMTP through the app, messages as .eml files).
# The app does IMAP, SMTP, MIME and keeps the passwords; this plugin only decides what to sync where (sync.ps1).
# Account folder: a c4k.dir with { "preview": { "plugin": "official.mail", "options": { imap, smtp, user, refresh, since } } }.

. "$PSScriptRoot/c4k.ps1"
. "$PSScriptRoot/sync.ps1"

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

Register-C4KHandler 'background.tick' { param($p) Invoke-MailTick }

Register-C4KHandler 'preview.render' {
    param($p)
    if ($p.preview -eq 'account') {
        $path = [string] $p.entry.path
        # Opening the account (Enter) or seeing it for the first time asks for a check on the next tick.
        if ((Register-MailAccount $path) -or $p.pinned) { Request-MailCheck $path }
        return @{ kind = 'text'; content = (Get-MailAccountStatus $path $p.options) }
    }
    # Reading view comes with the message actions (next version); for now the file name.
    @{ kind = 'text'; content = 'Wiadomość: ' + [string] $p.entry.name }
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
    if ($p.action -eq 'check') {
        $path = if ($p.args -and $p.args['account']) { [string] $p.args['account'] } else { [string] $p.entry.path }
        Invoke-MailCheck $path $p.options
    }
    $null
}

Start-C4KPlugin
