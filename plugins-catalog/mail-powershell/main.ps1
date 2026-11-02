# Commander4K official plugin: mail (IMAP/SMTP through the app, messages as .eml files).
# The app does IMAP, SMTP, MIME and keeps the passwords; this plugin only decides what to sync where (sync.ps1).
# Account folder: a c4k.dir with { "preview": { "plugin": "official.mail", "options": { imap, smtp, user, refresh, since } } }.

. "$PSScriptRoot/c4k.ps1"
. "$PSScriptRoot/sync.ps1"
. "$PSScriptRoot/compose.ps1"

# ---- the account folder: view, card, new message ----

# Unread and total counts per local folder, counted from the files (a "● " prefix = unread); no server call. A folder
# that cannot be listed has $null counts.
function Get-MailFolderCounts([string] $Path, $State) {
    $rows = [System.Collections.Generic.List[hashtable]]::new()
    $order = @($State.folders.Keys | Sort-Object { if ($State.folders[$_].special -eq 'inbox') { 0 } else { 1 } }, { [string] $State.folders[$_].local })
    foreach ($server in $order) {
        $entry = $State.folders[$server]
        $row = @{ local = [string] $entry.local; special = [string] $entry.special; total = $null; unread = $null }
        try {
            $files = (Get-MailLocalFiles (Join-MailPath $Path $entry.local)).files
            $row.total = $files.Count
            $row.unread = @($files.Values | Where-Object { $_.StartsWith($script:MailUnread) }).Count
        }
        catch { Write-C4KLog "mail folder $($entry.local) of $Path not listed: $($_.Exception.Message)" }
        $rows.Add($row)
    }
    , $rows.ToArray()
}

# Why the account is not being synced right now ('synchronizuje <komputer>' / 'wymaga logowania' / error / $null).
function Get-MailAccountProblem([string] $Path, $Config, $State) {
    if (Test-MailLockedElsewhere $Path) { return @{ level = 'warning'; text = 'synchronizuje ' + [string] (Get-MailLock $Path)['machine'] } }
    if ($null -eq $Config) { return @{ level = 'warning'; text = 'brak konfiguracji konta' } }
    if ($State.authRequired) { return @{ level = 'warning'; text = 'wymaga logowania' } }
    if ($State.error) { return @{ level = 'error'; text = 'błąd: ' + $State.error } }
    $null
}

# The account folder opened with Enter: folders with counts, last check, problems, progress and two buttons.
function Get-MailAccountView([string] $Path, $Options) {
    $config = Get-MailAccountConfig $Path $Options
    $state = Read-MailState $Path
    $md = [System.Collections.Generic.List[string]]::new()
    $actions = @(
        @{ label = 'Sprawdź teraz'; target = (Get-C4KActionTarget 'check' @{ account = $Path }); shortcut = 'F5' }
        @{ label = 'Nowa wiadomość'; target = (Get-C4KActionTarget 'new' @{ account = $Path }); shortcut = 'N' }
    )
    $md.Add('# ' + (ConvertTo-MailMarkdownLine (Get-MailLeaf $Path)))
    $md.Add('')
    if ($null -eq $state) {
        $md.Add('Nie można odczytać `.mail\state.json`: synchronizacja tego konta jest wstrzymana.')
        $md.Add('')
        $md.Add('Pobrane wiadomości zostają na dysku. „Zacznij synchronizację od nowa” zmienia nazwę uszkodzonego pliku na `state.json.broken-<czas>` i zaczyna od nowego stanu; wiadomości już pobrane są rozpoznawane po numerze `[uid N]`, więc nie zostaną pobrane drugi raz.')
        $actions += @{ label = 'Zacznij synchronizację od nowa'; target = (Get-C4KActionTarget 'resetState' @{ account = $Path }) }
        return @{ kind = 'markdown'; content = ($md -join "`n"); actions = $actions }
    }
    if ($null -eq $config) {
        $md.Add('Brak konfiguracji: dodaj `imap` i `user` w opcjach `c4k.dir`.')
        return @{ kind = 'markdown'; content = ($md -join "`n"); actions = $actions }
    }
    $md.Add('**Użytkownik:** ' + (ConvertTo-MailMarkdownLine ([string] $config['user'])) + ' (' + (ConvertTo-MailMarkdownLine ([string] $config['imap'])) + ')  ')
    $last = Get-MailInstant $state.lastSuccess
    $md.Add('**Ostatnia udana synchronizacja:** ' + $(if ($last) { $last.ToLocalTime().ToString('yyyy-MM-dd HH:mm') } else { 'jeszcze nie' }))
    $problem = Get-MailAccountProblem $Path $config $state
    if ($problem) { $md.Add(''); $md.Add('**Stan:** ' + (ConvertTo-MailMarkdownLine $problem.text)) }
    if ($state.progress -and $state.progress.total -gt 0) { $md.Add(''); $md.Add("**Pobieranie:** $($state.progress.done) / $($state.progress.total)") }
    $md.Add('')
    $md.Add('| Folder | Wiadomości | Nieprzeczytane |')
    $md.Add('|---|---:|---:|')
    foreach ($row in (Get-MailFolderCounts $Path $state)) {
        $total = if ($null -eq $row.total) { '?' } else { [string] $row.total }
        $unread = if ($null -eq $row.unread) { '?' } else { [string] $row.unread }
        $md.Add('| ' + (ConvertTo-MailMarkdownLine $row.local) + " | $total | $unread |")
    }
    @{ kind = 'markdown'; content = ($md -join "`n"); actions = $actions }
}

# The dashboard card: files and state.json only, never a mail.* call.
function Get-MailAccountCard([string] $Path, $Options) {
    $config = Get-MailAccountConfig $Path $Options
    $state = Read-MailState $Path
    $items = [System.Collections.Generic.List[hashtable]]::new()
    $actions = [System.Collections.Generic.List[hashtable]]::new()
    $status = 'ok'
    if ($null -eq $state) {
        return @{ title = (Get-MailLeaf $Path); status = 'error'; items = @(@{ kind = 'text'; text = 'nie można odczytać .mail\state.json' }); actions = @(@{ label = 'Zacznij synchronizację od nowa'; target = (Get-C4KActionTarget 'resetState' @{ account = $Path }) }) }
    }
    $inbox = $null
    $others = 0
    foreach ($row in (Get-MailFolderCounts $Path $state)) {
        if ($row.special -eq 'inbox') {
            $inbox = Join-MailPath $Path $row.local
            if ($null -eq $row.unread) { $status = 'warning' }
            else { $items.Add(@{ kind = 'value'; label = 'Nieprzeczytane'; value = [string] $row.unread }) }
        }
    }
    foreach ($row in (Get-MailFolderCounts $Path $state)) {
        if ($row.special -in 'inbox', 'trash', 'junk' -or -not $row.unread -or $others -ge 6) { continue }
        $items.Add(@{ kind = 'value'; label = $row.local; value = [string] $row.unread })
        $others++
    }
    $problem = Get-MailAccountProblem $Path $config $state
    $last = Get-MailInstant $state.lastCheck
    if ($problem) { $status = $problem.level; $items.Add(@{ kind = 'text'; text = $problem.text }) }
    elseif ($last) { $items.Add(@{ kind = 'text'; text = 'sprawdzono ' + $last.ToLocalTime().ToString('HH:mm') }) }
    else { $items.Add(@{ kind = 'text'; text = 'jeszcze nie sprawdzono' }) }
    if ($state.progress -and $state.progress.total -gt 0) {
        $items.Add(@{ kind = 'progress'; label = 'Pobieranie'; fraction = [Math]::Min(1.0, [double] $state.progress.done / [double] $state.progress.total); text = "$($state.progress.done) / $($state.progress.total)" })
    }
    if ($inbox) { $actions.Add(@{ label = 'Otwórz'; target = $inbox }) }
    $actions.Add(@{ label = 'Sprawdź teraz'; target = (Get-C4KActionTarget 'check' @{ account = $Path }) })
    @{ title = (Get-MailLeaf $Path); status = $status; items = $items.ToArray(); actions = $actions.ToArray() }
}

# "Nowa wiadomość" on an account: an empty draft in Szkice and its form.
function New-MailBlankDraft([string] $Path, $Options) {
    $config = Get-MailAccountConfig $Path $Options
    if ($null -eq $config) { throw 'Brak konfiguracji konta (imap i user w c4k.dir).' }
    $ctx = @{ root = $Path; options = $config; state = (Read-MailState $Path) }
    $dir = Get-MailDraftsDir $ctx
    $draftPath = Join-MailPath $dir (Get-MailFreeName $dir (Get-MailDraftName ''))
    $draft = @{
        to = @(); cc = @(); bcc = @(); subject = ''; body = ''; inReplyTo = $null; references = @(); attachments = @()
        from = @{ name = $null; address = (Get-MailFromAddress $config) }
    }
    Write-C4KMailDraft $draftPath $draft
    Get-MailDraftForm $draftPath $null $null $false
}

# ---- "Dodaj konto pocztowe" ----

$script:MailHostPattern = '^[^\s:/\\@]+(:\d{1,5})?$'
$script:MailSchemaUrl = 'https://raw.githubusercontent.com/4kprojekt/Commander4K/main/docs/c4k.dir.schema.json'

function Test-MailLocalDiskPath([string] $Path) { $Path -match '^([A-Za-z]:[\\/]|\\\\[^\\])' }

# JSON text -> ordered dictionaries, lists, strings, numbers, booleans. Unlike ConvertFrom-Json it never turns a string
# that looks like a date ("2026-01-01") into a DateTime, and it keeps the key order. Comments and trailing commas are
# accepted (the host reads c4k.dir that way too).
function ConvertFrom-MailJsonValue([System.Text.Json.JsonElement] $Element) {
    switch ($Element.ValueKind) {
        'Object' {
            $map = [ordered]@{}
            foreach ($property in $Element.EnumerateObject()) { $map[$property.Name] = ConvertFrom-MailJsonValue $property.Value }
            return $map
        }
        'Array' {
            $list = [System.Collections.Generic.List[object]]::new()
            foreach ($item in $Element.EnumerateArray()) { $list.Add((ConvertFrom-MailJsonValue $item)) }
            return , $list.ToArray()
        }
        'String' { return $Element.GetString() }
        'Number' {
            $long = [long] 0
            if ($Element.TryGetInt64([ref] $long)) { return $long }
            $decimal = [decimal] 0
            if ([decimal]::TryParse($Element.GetRawText(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $decimal)) { return $decimal }
            return $Element.GetDouble()
        }
        'True' { return $true }
        'False' { return $false }
        default { return $null }
    }
}

function ConvertFrom-MailJsonText([string] $Text) {
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Skip
    $options.AllowTrailingCommas = $true
    $document = [System.Text.Json.JsonDocument]::Parse($Text, $options)
    try { ConvertFrom-MailJsonValue $document.RootElement }
    finally { $document.Dispose() }
}

# The text of the dashboard c4k.dir with the account's tile added, like FolderConfigWriter.AppendTile: the other keys
# and tiles stay as they were, in the same order, and string values are not reinterpreted. $Existing is $null for a new file.
function Add-MailDashboardTile($Existing, $Tile) {
    if ($null -eq $Existing) { $root = [ordered]@{ '$schema' = $script:MailSchemaUrl } }
    else {
        $root = try { ConvertFrom-MailJsonText $Existing } catch { $null }
        if ($root -isnot [System.Collections.IDictionary]) { throw 'Nie można odczytać c4k.dir w tym folderze (to nie jest poprawny JSON), więc go nie zmieniam.' }
    }
    $isDashboardObject = { param($a) $a -is [System.Collections.IDictionary] -and $a['view'] -eq 'dashboard' }
    $target = $null
    if (& $isDashboardObject $root['enter']) { $target = $root['enter'] }
    elseif (& $isDashboardObject $root['preview']) { $target = $root['preview'] }
    if ($target) {
        $target['tiles'] = @(@($target['tiles']) | Where-Object { $null -ne $_ }) + @($Tile)
    }
    elseif ($null -eq $root['enter'] -and ($null -eq $root['preview'] -or $root['preview'] -eq 'dashboard')) {
        $root['enter'] = [ordered]@{ view = 'dashboard'; tiles = @($Tile) }
        $root['preview'] = 'dashboard'
    }
    else {
        if (-not (Confirm-C4K 'Dodaj konto pocztowe' 'W tym folderze c4k.dir pokazuje coś innego niż pulpit. Zamienić podgląd na pulpit z kartą konta (dotychczasowy podgląd przejdzie pod Enter, jeśli jest wolny)?')) {
            throw 'Anulowano: c4k.dir w tym folderze nie został zmieniony.'
        }
        $old = $root['preview']
        $root.Remove('preview')
        $root['preview'] = [ordered]@{ view = 'dashboard'; tiles = @($Tile) }
        if ($null -eq $root['enter'] -and $null -ne $old) { $root['enter'] = $old }
    }
    ($root | ConvertTo-Json -Depth 20) + "`n"
}

# The fields of the account window. $Values are the answers of the previous round (shown again after a check failed).
function Get-MailAccountFields($Values, [bool] $WithFolder) {
    $value = { param($Id, $Default) if ($Values -and $null -ne $Values[$Id]) { [string] $Values[$Id] } else { $Default } }
    $manual = @{ servers = 'manual' }
    $fields = [System.Collections.Generic.List[hashtable]]::new()
    $fields.Add(@{ id = 'user'; label = 'Adres e-mail'; value = (& $value 'user' ''); placeholder = 'ja@example.com'; help = 'To także nazwa użytkownika do logowania. Hasło zapisze aplikacja, nie wtyczka.'; required = $true })
    $fields.Add(@{ id = 'name'; label = 'Imię i nazwisko nadawcy'; value = (& $value 'name' ''); help = 'Opcjonalne; widać je w polu „Od” wysłanych wiadomości.' })
    if ($WithFolder) {
        $fields.Add(@{ id = 'folder'; label = 'Nazwa folderu konta'; value = (& $value 'folder' ''); placeholder = 'domena adresu, np. example.com' })
    }
    $fields.Add(@{ id = 'serversHeading'; type = 'heading'; label = 'Serwery' })
    $fields.Add(@{
            id = 'servers'; type = 'segmented'; label = 'Ustawienia serwerów'; value = (& $value 'servers' 'auto')
            options = @(@{ value = 'auto'; label = 'Z adresu' }, @{ value = 'manual'; label = 'Ręcznie' })
            help = 'Z adresu: imap.<domena>:993 i smtp.<domena>:465. Port 993/465 to TLS, każdy inny wymaga STARTTLS.'
        })
    $fields.Add(@{ id = 'imap'; label = 'Serwer IMAP'; value = (& $value 'imap' ''); placeholder = 'imap.example.com:993'; required = $true; visibleWhen = $manual })
    $fields.Add(@{ id = 'smtp'; label = 'Serwer SMTP'; value = (& $value 'smtp' ''); placeholder = 'smtp.example.com:465'; help = 'Puste = konto tylko do odbioru.'; visibleWhen = $manual })
    $fields.Add(@{ id = 'syncHeading'; type = 'heading'; label = 'Synchronizacja' })
    $fields.Add(@{
            id = 'refresh'; type = 'select'; label = 'Sprawdzaj pocztę'; value = (& $value 'refresh' '5m')
            options = @(@{ value = '1m'; label = 'co minutę' }, @{ value = '5m'; label = 'co 5 minut' }, @{ value = '15m'; label = 'co 15 minut' }, @{ value = '1h'; label = 'co godzinę' }, @{ value = 'off'; label = 'tylko po otwarciu i „Sprawdź teraz”' })
        })
    $fields.Add(@{
            id = 'since'; type = 'select'; label = 'Pobieraj wiadomości z'; value = (& $value 'since' '3m')
            options = @(@{ value = '1m'; label = 'ostatniego miesiąca' }, @{ value = '3m'; label = 'ostatnich 3 miesięcy' }, @{ value = '1y'; label = 'ostatniego roku' }, @{ value = '5y'; label = 'ostatnich 5 lat' })
        })
    , $fields.ToArray()
}

# Checks the answers of the account window: errors per field, else the c4k.dir options (and the folder name).
function Test-MailAccountValues($Values, [string] $Parent) {
    $errors = @{}
    $address = ([string] $Values['user']).Trim()
    if ($address -notmatch $script:MailAddressPattern) { $errors['user'] = 'To nie wygląda na adres e-mail.' }
    $domain = if ($address.Contains('@')) { $address.Substring($address.IndexOf('@') + 1) } else { '' }
    if ($Values['servers'] -eq 'manual') {
        $imap = ([string] $Values['imap']).Trim()
        $smtp = ([string] $Values['smtp']).Trim()
        if ($imap -notmatch $script:MailHostPattern) { $errors['imap'] = 'Podaj host albo host:port, np. imap.example.com:993.' }
        if ($smtp -and $smtp -notmatch $script:MailHostPattern) { $errors['smtp'] = 'Podaj host albo host:port albo zostaw puste.' }
    }
    else {
        $imap = "imap.${domain}:993"
        $smtp = "smtp.${domain}:465"
    }
    $folder = $null
    if ($Parent) {
        $folder = ([string] $Values['folder']).Trim()
        if (-not $folder) { $folder = $domain }
        $folder = (($folder -replace '[\\/:*?"<>|\x00-\x1f]', '_').Trim()).TrimEnd('.', ' ')
        if (-not $folder -or $folder -match '^(con|prn|aux|nul|com\d|lpt\d)(\..*)?$') { $errors['folder'] = 'Podaj poprawną nazwę folderu.' }
        elseif ($null -ne (Get-C4KItem (Join-MailPath $Parent $folder))) { $errors['folder'] = "Folder $folder już istnieje." }
    }
    $options = [ordered]@{ imap = $imap }
    if ($smtp) { $options['smtp'] = $smtp }
    $options['user'] = $address
    $name = ([string] $Values['name']).Trim()
    if ($name) { $options['name'] = $name }
    $options['refresh'] = if ($Values['refresh'] -eq 'off') { $false } elseif ($Values['refresh']) { [string] $Values['refresh'] } else { '5m' }
    $options['since'] = if ($Values['since']) { [string] $Values['since'] } else { '3m' }
    @{ errors = $errors; options = $options; folder = $folder }
}

# One window with the address, the sender, the servers and the sync settings (and the folder name when $Parent is
# given). After the answers pass their checks it logs in once (the app asks for the password and keeps it); a refused
# login shows the window again with the reason, so nothing is written until it works. Returns @{ options; folder },
# or $null when the user cancelled.
function Read-MailAccountOptions([string] $Title, [string] $Parent) {
    $values = $null
    $errors = $null
    $message = $null
    $description = 'Ustawienia trafią do c4k.dir folderu konta. Przed zapisem aplikacja zaloguje się raz, żeby sprawdzić konto.'
    while ($true) {
        $values = Show-C4KDialog $Title (Get-MailAccountFields $values ([bool] $Parent)) -Description $description -Submit 'Sprawdź i zapisz' -Message $message -Errors $errors
        if ($null -eq $values) { return $null }
        $result = Test-MailAccountValues $values $Parent
        $errors = $result.errors
        $message = $null
        if ($errors.Count -gt 0) { continue }
        $account = @{ imap = $result.options['imap']; smtp = $result.options['smtp']; user = $result.options['user']; interactive = $true }
        try {
            [void] (Get-C4KMailFolders $account)
            return @{ options = $result.options; folder = $result.folder }
        }
        catch { $message = 'Nie można się zalogować: ' + (Get-MailErrorText $_) }
    }
}

function Add-MailAccount([string] $Active) {
    if (-not $Active -or -not (Test-MailLocalDiskPath $Active)) {
        throw 'Konto pocztowe można dodać tylko w folderze na dysku lub udziale sieciowym (nie w archiwum, na SFTP ani w widoku Ten komputer).'
    }
    $title = 'Dodaj konto pocztowe'
    $answer = Read-MailAccountOptions $title $Active
    if ($null -eq $answer) { return @{ message = 'Anulowano.' } }
    $options = $answer.options
    $folderName = $answer.folder

    $accountPath = Join-MailPath $Active $folderName
    if ($null -ne (Get-C4KItem $accountPath)) { throw "Folder $folderName już istnieje." }
    $dashboardPath = Join-MailPath $Active 'c4k.dir'

    $action = { [ordered]@{ plugin = 'official.mail'; options = $options } }
    $accountConfig = [ordered]@{ '$schema' = $script:MailSchemaUrl; preview = (& $action); enter = (& $action) }

    $tile = [ordered]@{ title = $folderName; open = $folderName; card = 'official.mail/account'; refresh = '30s' }
    $existing = $null
    if ($null -ne (Get-C4KItem $dashboardPath)) {
        $existing = Get-C4KText $dashboardPath
        if ($existing -match '(?m)^\s*//|/\*') {
            if (-not (Confirm-C4K $title 'c4k.dir w tym folderze ma komentarze; po dodaniu karty konta zostaną usunięte. Kontynuować?')) { throw 'Anulowano: c4k.dir w tym folderze nie został zmieniony.' }
        }
    }
    $dashboard = Add-MailDashboardTile $existing $tile

    New-C4KDirectory $accountPath
    Set-C4KText (Join-MailPath $accountPath 'c4k.dir') (($accountConfig | ConvertTo-Json -Depth 20) + "`n")
    Set-C4KText $dashboardPath $dashboard
    [void] (Register-MailAccount $accountPath)
    Request-MailCheck $accountPath
    @{ message = "Dodano konto $folderName. Pierwsza synchronizacja ruszy w tle." }
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
        # Without a Trash folder the server would expunge: permanent removal is offered only inside Kosz.
        if ($Ctx.special -eq 'trash' -or (Get-MailSpecialFolder $Ctx 'trash')) { $actions.Add(@{ label = 'Usuń'; target = (Get-C4KActionTarget 'delete'); shortcut = 'Delete' }) }
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
    if ($Kind -eq 'delete' -and $Ctx.special -ne 'trash' -and -not (Get-MailSpecialFolder $Ctx 'trash')) { throw 'To konto nie ma folderu Kosz: usunięcie byłoby trwałe, więc nic nie zrobiono.' }
    if ($Kind -eq 'delete' -and $Ctx.special -eq 'trash') {
        if (-not (Confirm-C4K 'Usuń na stałe' 'Wiadomość zostanie usunięta z Kosza na serwerze i z dysku na stałe. Tej operacji nie można cofnąć. Kontynuować?')) { return $null }
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

Register-C4KHandler 'command.execute' {
    param($p)
    if ($p.command -ne 'add-account') { throw "Nieznane polecenie: $($p.command)" }
    try { Add-MailAccount ([string] $p.context.activePath) }
    catch { throw (Get-MailErrorText $_) }
}

Register-C4KHandler 'background.tick' { param($p) Invoke-MailTick }

# A folder that is being made a mail account (the app's "folder view" choice): ask for the account, check the login
# and hand the options back; the app writes them into the folder's c4k.dir.
Register-C4KHandler 'preview.setup' {
    param($p)
    if ($p.preview -ne 'account') { return @{ options = @{} } }
    if (-not (Test-MailLocalDiskPath ([string] $p.folder))) {
        throw 'Konto pocztowe może mieć tylko folder na dysku lub w udziale sieciowym.'
    }
    $answer = Read-MailAccountOptions 'Konto pocztowe w tym folderze'
    if ($null -eq $answer) { return $null }
    @{ options = $answer.options }
}

Register-C4KHandler 'preview.render' {
    param($p)
    if ($p.preview -eq 'account') {
        $path = [string] $p.entry.path
        # Opening the account (Enter) or seeing it for the first time asks for a check on the next tick.
        if ((Register-MailAccount $path) -or $p.pinned) { Request-MailCheck $path }
        return Get-MailAccountView $path $p.options
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
    $path = if ($p.args -and $p.args['account']) { [string] $p.args['account'] } else { [string] $p.entry.path }
    if ($p.action -eq 'check') { Invoke-MailCheck $path $p.options }
    elseif ($p.action -eq 'resetState') {
        try { Reset-MailState $path; Request-MailCheck $path }
        catch { throw (Get-MailErrorText $_) }
    }
    elseif ($p.action -eq 'new') {
        try { return New-MailBlankDraft $path $p.options }
        catch { throw (Get-MailErrorText $_) }
    }
    $null
}

Register-C4KHandler 'preview.formChanged' {
    param($p)
    if ($p.preview -eq 'message') { Save-MailFormEdit ([string] $p.entry.path) $p.values }
    $null
}

Start-C4KPlugin
