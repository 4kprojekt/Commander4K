# Composing for official.mail: a draft is an .eml file in the account's drafts folder ("Szkice"); the preview form edits
# it (every pause in typing rewrites the file) and "Wyślij" sends it. Dot-sourced by main.ps1 after sync.ps1; every file
# and mail operation goes through the SDK. Sync uploads drafts (Push-MailDrafts), so a sent or discarded draft is also
# removed from the server and from state.json here.

$script:MailDraftsFolder = 'Szkice'
$script:MailAddressPattern = '^[^@\s,;<>"()\[\]\\]+@[^@\s,;<>"()\[\]\\]+$'
$script:MailMaxRepliesKept = 200

# ---- paths and account context ----

function Get-MailParent([string] $Path) {
    $trimmed = $Path.TrimEnd('\', '/')
    $cut = [Math]::Max($trimmed.LastIndexOf('\'), $trimmed.LastIndexOf('/'))
    if ($cut -le 0) { $null } else { $trimmed.Substring(0, $cut) }
}

# The account a message file belongs to: the nearest ancestor (up to three levels) whose c4k.dir names this plugin.
# $null when there is none. state is $null when state.json cannot be read.
function Get-MailContext([string] $FilePath) {
    $dir = Get-MailParent $FilePath
    if (-not $dir) { return $null }
    $root = Get-MailParent $dir
    $options = $null
    for ($i = 0; $i -lt 3 -and $root; $i++) {
        $options = Get-MailAccountConfig $root $null
        if ($options) { break }
        $root = Get-MailParent $root
    }
    if (-not $options) { return $null }
    $state = Read-MailState $root
    $ctx = @{ root = $root; options = $options; dir = $dir; local = (Get-MailLeaf $dir); state = $state; server = $null; special = $null; uid = $null; account = (New-MailAccountParam $options $true) }
    if ($state) {
        foreach ($server in $state.folders.Keys) {
            if ($state.folders[$server].local -ieq $ctx.local) { $ctx.server = $server; $ctx.special = $state.folders[$server].special; break }
        }
    }
    if ((Get-MailLeaf $FilePath) -match $script:MailUidPattern) { $ctx.uid = [long] $Matches[1] }
    $ctx
}

function Get-MailErrorText($ErrorRecord) {
    $text = [string] $ErrorRecord.Exception.Message
    $text = $text -replace '^C4K -?\d+:\s*', ''
    if ($text) { $text } else { 'nieznany błąd' }
}

function Get-MailFromAddress([hashtable] $Options) {
    if ($Options['from']) { return [string] $Options['from'] }
    $user = [string] $Options['user']
    if ($user.Contains('@')) { return $user }
    $imapHost = ([string] $Options['imap']) -replace ':\d+$', ''
    "$user@$imapHost"
}

# ---- addresses ----

function Split-MailAddressList([string] $Text) {
    $parts = [System.Collections.Generic.List[string]]::new()
    $current = [System.Text.StringBuilder]::new()
    $quoted = $false
    $angle = $false
    foreach ($c in $Text.ToCharArray()) {
        if ($c -eq '"') { $quoted = -not $quoted }
        elseif (-not $quoted -and $c -eq '<') { $angle = $true }
        elseif (-not $quoted -and $c -eq '>') { $angle = $false }
        if (-not $quoted -and -not $angle -and ($c -eq ',' -or $c -eq ';')) { $parts.Add($current.ToString()); [void] $current.Clear() }
        else { [void] $current.Append($c) }
    }
    $parts.Add($current.ToString())
    @($parts | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# "Jan Kowalski <jan@example.org>, ewa@example.org" -> @{ valid = @(@{ name; address }); invalid = @('text') }
function ConvertFrom-MailAddressField([string] $Text) {
    $valid = [System.Collections.Generic.List[hashtable]]::new()
    $invalid = [System.Collections.Generic.List[string]]::new()
    foreach ($part in Split-MailAddressList $Text) {
        $name = $null
        $address = $part
        if ($part -match '^(.*?)<\s*([^<>]+?)\s*>\s*$') {
            $name = $Matches[1].Trim().Trim('"').Trim()
            $address = $Matches[2]
            if (-not $name) { $name = $null }
        }
        if ($address -match $script:MailAddressPattern) { $valid.Add(@{ name = $name; address = $address }) } else { $invalid.Add($part) }
    }
    @{ valid = $valid.ToArray(); invalid = $invalid.ToArray() }
}

function Format-MailAddress($Address) {
    if ($null -eq $Address) { return '' }
    $address = [string] $Address['address']
    $name = [string] $Address['name']
    if (-not $name) { return $address }
    if ($name -match '[,;<>"]') { $name = '"' + $name.Replace('\', '\\').Replace('"', '\"') + '"' }
    "$name <$address>"
}

function Format-MailAddressList($Addresses) { (@($Addresses | Where-Object { $_ } | ForEach-Object { Format-MailAddress $_ })) -join ', ' }

# ---- the draft file ----

# Header lines of an .eml (unfolded, names lower-case). Headers sit at the start, so a bounded read is enough.
function Get-MailRawHeaders([string] $Path) {
    $text = Get-C4KText $Path 65536
    $end = $text.IndexOf("`r`n`r`n")
    if ($end -lt 0) { $end = $text.IndexOf("`n`n") }
    $head = if ($end -ge 0) { $text.Substring(0, $end) } else { $text }
    $head = $head -replace '\r?\n[ \t]+', ' '
    $headers = @{}
    foreach ($line in ($head -split '\r?\n')) {
        if ($line -match '^([!-9;-~]+):\s*(.*)$') { $headers[$Matches[1].ToLowerInvariant()] = $Matches[2] }
    }
    $headers
}

# The draft as the form shows it. Bcc and In-Reply-To are not in mail.parse, so they come from the header text.
function Read-MailDraft([string] $Path) {
    $mail = Read-C4KMail $Path
    $headers = Get-MailRawHeaders $Path
    $bcc = @()
    if ($headers['bcc']) {
        $plain = $headers['bcc'] -replace '=\?[^?]+\?[bBqQ]\?[^?]*\?=', ''
        $bcc = @([regex]::Matches($plain, '[^\s<>,;"()]+@[^\s<>,;"()]+') | ForEach-Object { @{ name = $null; address = $_.Value } })
    }
    $inReplyTo = $null
    if ($headers['in-reply-to'] -match '<([^<>]+)>') { $inReplyTo = $Matches[1] }
    @{
        to = @($mail.to); cc = @($mail.cc); bcc = $bcc
        subject = [string] $mail.subject; body = ([string] $mail.bodyText -replace '\r\n?', "`n") -replace '\n$', ''
        inReplyTo = $inReplyTo; references = @($mail.references)
        attachments = @($mail.attachments)
    }
}

# Form field values (strings) of a draft; $Values (what the user typed) wins over the file.
function Get-MailDraftFields($Draft, $Values) {
    $fields = [ordered]@{
        to = Format-MailAddressList $Draft.to
        cc = Format-MailAddressList $Draft.cc
        bcc = Format-MailAddressList $Draft.bcc
        subject = [string] $Draft.subject
        body = [string] $Draft.body
    }
    if ($Values -is [System.Collections.IDictionary]) {
        foreach ($key in @($fields.Keys)) { if ($Values.Contains($key) -and $null -ne $Values[$key]) { $fields[$key] = [string] $Values[$key] } }
    }
    $fields
}

# Writes the draft file from form fields. Addresses that are not valid yet are left out (the user is still typing);
# $Attachments are paths or "<this file>.eml#<n>" references, which mail.compose reads before it overwrites the file.
function Write-MailDraftFile([string] $Path, $Ctx, $Fields, $Draft, [string[]] $Attachments) {
    $body = @{
        to = @((ConvertFrom-MailAddressField $Fields.to).valid)
        cc = @((ConvertFrom-MailAddressField $Fields.cc).valid)
        bcc = @((ConvertFrom-MailAddressField $Fields.bcc).valid)
        subject = [string] $Fields.subject
        body = [string] $Fields.body
        inReplyTo = $Draft.inReplyTo
        references = @($Draft.references)
        attachments = @($Attachments)
        from = @{ name = $null; address = (Get-MailFromAddress $Ctx.options) }
    }
    Write-C4KMailDraft $Path $body
}

function Get-MailAttachmentRefs([string] $Path, $Draft) {
    $refs = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt @($Draft.attachments).Count; $i++) { $refs.Add("$Path#$i") }
    , $refs.ToArray()
}

# ---- the form ----

function New-MailFormResult([string] $Path, $Fields, $Draft, [string] $Message, [bool] $IsError) {
    $files = @()
    $buttons = [System.Collections.Generic.List[hashtable]]::new()
    $buttons.Add(@{ label = 'Wyślij'; target = (Get-C4KActionTarget 'send'); shortcut = 'Ctrl+Enter' })
    $buttons.Add(@{ label = 'Dodaj zaznaczone pliki z drugiego panelu'; target = (Get-C4KActionTarget 'attachSelected') })
    foreach ($attachment in @($Draft.attachments)) {
        $files += @{ name = [string] $attachment.fileName; path = "$Path#$($attachment.index)"; size = [long] $attachment.size }
        $buttons.Add(@{ label = 'Usuń załącznik: ' + $attachment.fileName; target = (Get-C4KActionTarget 'removeAttachment' @{ i = [string] $attachment.index }) })
    }
    $buttons.Add(@{ label = 'Odrzuć szkic'; target = (Get-C4KActionTarget 'discard') })
    $result = @{
        kind = 'form'
        title = 'Nowa wiadomość'
        path = $Path
        fields = @(
            @{ id = 'to'; label = 'Do'; value = $Fields.to }
            @{ id = 'cc'; label = 'DW'; value = $Fields.cc }
            @{ id = 'bcc'; label = 'UDW'; value = $Fields.bcc }
            @{ id = 'subject'; label = 'Temat'; value = $Fields.subject }
            @{ id = 'body'; label = 'Treść'; value = $Fields.body; multiline = $true }
        )
        files = $files
        buttons = $buttons.ToArray()
    }
    if ($Message) { $result.message = $Message; $result.messageError = $IsError }
    $result
}

function Get-MailDraftForm([string] $Path, $Values, [string] $Message, [bool] $IsError) {
    $draft = Read-MailDraft $Path
    New-MailFormResult $Path (Get-MailDraftFields $draft $Values) $draft $Message $IsError
}

# ---- creating drafts ----

function Get-MailDraftsDir($Ctx) {
    $local = $script:MailDraftsFolder
    if ($Ctx.state) {
        foreach ($server in $Ctx.state.folders.Keys) { if ($Ctx.state.folders[$server].special -eq 'drafts') { $local = $Ctx.state.folders[$server].local } }
    }
    $dir = Join-MailPath $Ctx.root $local
    New-C4KDirectory $dir
    $dir
}

function Get-MailDraftName([string] $Subject) {
    $clean = (($Subject -replace '[\\/:*?"<>|\x00-\x1f\[\]]', ' ') -replace '\s+', ' ').Trim()
    if ($clean.Length -gt 60) {
        $clean = $clean.Substring(0, 60)
        if ([char]::IsHighSurrogate($clean[$clean.Length - 1])) { $clean = $clean.Substring(0, $clean.Length - 1) }
    }
    $clean = $clean.Trim().TrimEnd('.')
    if (-not $clean) { $clean = '(bez tematu)' }
    (Get-Date).ToString('yyyy-MM-dd HHmm') + ' ' + $clean + '.eml'
}

function Set-MailReplyLink([string] $DraftPath, $Link) {
    $replies = @{}
    $stored = Get-C4KStorage 'replies' @{}
    if ($stored -is [hashtable]) { foreach ($key in $stored.Keys) { $replies[$key] = $stored[$key] } }
    $key = $DraftPath.ToLowerInvariant()
    if ($null -eq $Link) { $replies.Remove($key) } else { $replies[$key] = $Link }
    # Entries of drafts that are long gone must not pile up.
    while ($replies.Count -gt $script:MailMaxRepliesKept) { $replies.Remove(@($replies.Keys)[0]) }
    Set-C4KStorage 'replies' $replies
}

function Get-MailReplyLink([string] $DraftPath) {
    $stored = Get-C4KStorage 'replies' @{}
    if ($stored -is [hashtable]) { $stored[$DraftPath.ToLowerInvariant()] } else { $null }
}

# reply | replyAll | forward of the message file: a new draft in Szkice and its form.
function New-MailReplyDraft([string] $Path, [string] $Mode, $Ctx) {
    if ($null -eq $Ctx) { throw 'Ta wiadomość nie leży w folderze konta poczty (brak c4k.dir z wtyczką Poczta).' }
    $self = Get-MailFromAddress $Ctx.options
    $reply = New-C4KMailReply $Path $Mode $self
    $dir = Get-MailDraftsDir $Ctx
    $draftPath = Join-MailPath $dir (Get-MailFreeName $dir (Get-MailDraftName ([string] $reply.subject)))
    $draft = @{
        to = @($reply.to); cc = @($reply.cc); bcc = @($reply.bcc)
        subject = [string] $reply.subject; body = [string] $reply.body
        inReplyTo = $reply.inReplyTo; references = @($reply.references)
        attachments = @($reply.attachments)
        from = @{ name = $null; address = (Get-MailFromAddress $Ctx.options) }
    }
    Write-C4KMailDraft $draftPath $draft
    $original = @{ original = $Path; mode = $Mode }
    if ($Ctx.uid) { $original.uid = $Ctx.uid; $original.dir = $Ctx.dir }
    Set-MailReplyLink $draftPath $original
    Get-MailDraftForm $draftPath $null $null $false
}

# ---- removing drafts ----

# Local file, the server copy sync uploaded (by its drafts uid) and the entry in state.json.
function Remove-MailDraft([string] $DraftPath, $Ctx) {
    $name = Get-MailLeaf $DraftPath
    $state = if ($Ctx) { Read-MailState $Ctx.root } else { $null }
    if ($state -and $state.drafts.ContainsKey($name)) {
        $uid = [long] $state.drafts[$name].uid
        if ($uid -gt 0) {
            foreach ($server in @($state.folders.Keys)) {
                if ($state.folders[$server].special -ne 'drafts') { continue }
                try { Remove-C4KMail $Ctx.account $server @($uid) } catch { Write-C4KLog "draft ${name}: server copy not removed: $(Get-MailErrorText $_)" }
                [void] $state.folders[$server].known.Remove($uid)
            }
        }
        $state.drafts.Remove($name)
        Save-MailState $Ctx.root $state
    }
    if ($null -ne (Get-C4KItem $DraftPath)) { Remove-C4KItem $DraftPath }
    Set-MailReplyLink $DraftPath $null
}

# ---- form actions ----

# preview.formChanged: keep the file in step with what the user typed.
function Save-MailFormEdit([string] $DraftPath, $Values) {
    if ($Values -isnot [hashtable] -or $null -eq (Get-C4KItem $DraftPath)) { return }
    $ctx = Get-MailContext $DraftPath
    if (-not (Test-MailIsDraftFile $ctx)) { return }  # only drafts are rewritten
    $draft = Read-MailDraft $DraftPath
    Write-MailDraftFile $DraftPath $ctx (Get-MailDraftFields $draft $Values) $draft (Get-MailAttachmentRefs $DraftPath $draft)
}

# attachSelected | removeAttachment | discard | send. Returns a render result.
function Invoke-MailDraftAction([string] $DraftPath, [string] $Action, $ActionArgs, $Values) {
    $ctx = Get-MailContext $DraftPath
    if ($null -eq $ctx) { throw 'Ten szkic nie leży w folderze konta poczty.' }
    if ($null -eq (Get-C4KItem $DraftPath)) { throw 'Szkic już nie istnieje.' }
    $draft = Read-MailDraft $DraftPath
    $fields = Get-MailDraftFields $draft $Values
    $refs = Get-MailAttachmentRefs $DraftPath $draft

    switch ($Action) {
        'attachSelected' {
            $files = @((Get-C4KContext).selection | Where-Object { $_ -and $_.kind -eq 'file' } | ForEach-Object { [string] $_.path })
            if ($files.Count -eq 0) { throw 'Zaznacz pliki w drugim panelu (Insert lub Spacja), a potem wybierz „Dodaj zaznaczone pliki”.' }
            Write-MailDraftFile $DraftPath $ctx $fields $draft ($refs + $files)
            return Get-MailDraftForm $DraftPath $fields $null $false
        }
        'removeAttachment' {
            $index = -1
            if (-not $ActionArgs -or -not [int]::TryParse([string] $ActionArgs['i'], [ref] $index) -or $index -lt 0 -or $index -ge $refs.Count) { throw 'Nie ma takiego załącznika.' }
            Write-MailDraftFile $DraftPath $ctx $fields $draft @($refs | Where-Object { $_ -ne "$DraftPath#$index" })
            return Get-MailDraftForm $DraftPath $fields $null $false
        }
        'discard' {
            $link = Get-MailReplyLink $DraftPath
            Remove-MailDraft $DraftPath $ctx
            return Get-MailAfterDraft $link 'Szkic odrzucony.'
        }
        'send' { return Invoke-MailSend $DraftPath $ctx $draft $fields $refs }
    }
    throw ('Nieznana akcja: ' + $Action)
}

# What the preview shows once the draft is gone: the message it answered, else a short note.
function Get-MailAfterDraft($Link, [string] $Note) {
    if ($Link -and $Link['original']) {
        $original = Find-MailOriginal $Link
        if ($original) { return Get-MailMessageResult $original $false $Note $null }
    }
    @{ kind = 'text'; content = $Note }
}

# The original message of a reply by its uid (its name may have changed since: read/unread), else its old path.
function Find-MailOriginal($Link) {
    $path = [string] $Link['original']
    if ($null -ne (Get-C4KItem $path)) { return $path }
    if ($Link['uid'] -and $Link['dir']) {
        try {
            $names = (Get-MailLocalFiles ([string] $Link['dir'])).files
            if ($names.ContainsKey([long] $Link['uid'])) { return Join-MailPath ([string] $Link['dir']) $names[[long] $Link['uid']] }
        }
        catch { Write-C4KLog "original of a reply not found: $(Get-MailErrorText $_)" }
    }
    $null
}

function Invoke-MailSend([string] $DraftPath, $Ctx, $Draft, $Fields, [string[]] $Refs) {
    $fail = { param($text) Get-MailDraftForm $DraftPath $Fields $text $true }
    $problems = [System.Collections.Generic.List[string]]::new()
    $count = 0
    foreach ($key in 'to', 'cc', 'bcc') {
        $parsed = ConvertFrom-MailAddressField $Fields[$key]
        $count += $parsed.valid.Count
        foreach ($bad in $parsed.invalid) { $problems.Add($bad) }
    }
    if ($problems.Count -gt 0) {
        Write-MailDraftFile $DraftPath $Ctx $Fields $Draft $Refs
        return & $fail ('Niepoprawny adres: ' + ($problems -join ', '))
    }
    if ($count -eq 0) { return & $fail 'Podaj odbiorcę w polu Do.' }
    if (-not $Ctx.options['smtp']) {
        Write-MailDraftFile $DraftPath $Ctx $Fields $Draft $Refs
        return & $fail 'Konto nie ma serwera SMTP: dodaj opcję smtp w c4k.dir.'
    }

    # Whatever the form holds goes out, and the file keeps it if sending fails.
    Write-MailDraftFile $DraftPath $Ctx $Fields $Draft $Refs
    try { [void] (Send-C4KMail $Ctx.account $DraftPath) }
    catch { return & $fail ('Nie wysłano: ' + (Get-MailErrorText $_)) }

    # Sent. From here nothing may make the user send it again, so every step is best effort.
    $link = Get-MailReplyLink $DraftPath
    $note = 'Wysłano.'
    if ($link -and $link['mode'] -ne 'forward' -and $link['uid']) {
        try {
            $original = Find-MailOriginal $link
            $originalCtx = if ($original) { Get-MailContext $original } else { $null }
            if ($originalCtx -and $originalCtx.server -and $originalCtx.uid) { Set-C4KMailFlags $originalCtx.account $originalCtx.server @($originalCtx.uid) @('answered') }
        }
        catch { Write-C4KLog "answered flag not set: $(Get-MailErrorText $_)"; $note = 'Wysłano (nie oznaczono oryginału jako odpowiedzianego).' }
    }
    try { Remove-MailDraft $DraftPath $Ctx }
    catch { Write-C4KLog "draft $DraftPath not removed after sending: $(Get-MailErrorText $_)"; $note = 'Wysłano, ale nie udało się usunąć szkicu.' }
    Get-MailAfterDraft $link $note
}
