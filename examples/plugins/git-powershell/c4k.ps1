# Commander4K plugin protocol v1 — PowerShell 7 client (copy next to your plugin's main.ps1 and dot-source it:
#   . "$PSScriptRoot/c4k.ps1"
# Register handlers with Register-C4KHandler 'command.execute' { param($p) ... }, call the app with the
# Invoke-C4K* functions (every call is permission-checked against plugin.json and audited), then Start-C4KPlugin.
# Messages are JSON-RPC 2.0, one per line on stdin/stdout.
#
# Open source (MIT): https://github.com/4kprojekt/Commander4K/tree/main/plugins-sdk — reference: docs/plugins.md.
#
# RULE: do ALL file, folder, process, network and system work through these functions — never with PowerShell's
# own Get-Content, Set-Content, Get-ChildItem, Remove-Item, Copy-Item, Start-Process, Get-Process, Invoke-WebRequest,
# Invoke-RestMethod, [System.IO.*] or & external.exe. The app checks permissions and audits these calls, works on
# every location the user can open (shares, SFTP, archives, registry) and Commander4K's installer warns about plugins
# that bypass them. Never Write-Output/Write-Host: stdout is the protocol channel (use Write-C4KLog).

$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$script:C4KHandlers = @{}
$script:C4KQueue = [System.Collections.Generic.Queue[hashtable]]::new()
$script:C4KNextId = 0

function Register-C4KHandler([string] $Method, [scriptblock] $Handler) {
    $script:C4KHandlers[$Method] = $Handler
}

function Send-C4KMessage($Message) {
    [Console]::Out.WriteLine(($Message | ConvertTo-Json -Compress -Depth 30))
    [Console]::Out.Flush()
}

function Read-C4KMessage {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { exit 0 }
    return $line | ConvertFrom-Json -AsHashtable
}

# Request to the app; blocks until it answers. Throws on refusal: the exception's Data['Code'] is the error code
# (-32001 = missing permission, -32010 = mail login needed, -32011 = untrusted mail certificate, -32012 = a sent mail may or may not have gone out) and Data['ErrorData']
# the structured detail (realm, host, fingerprint) or $null.
function Invoke-C4K([string] $Method, [hashtable] $Params = @{}) {
    $script:C4KNextId++
    $id = "p$($script:C4KNextId)"
    Send-C4KMessage @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Params }
    while ($true) {
        $message = Read-C4KMessage
        if ($message.id -eq $id -and -not $message.ContainsKey('method')) {
            if ($message.ContainsKey('error')) {
                $failure = [System.Exception]::new("C4K $($message.error.code): $($message.error.message)")
                $failure.Data['Code'] = $message.error.code
                $failure.Data['ErrorData'] = $message.error.data
                throw $failure
            }
            return $message.result
        }
        $script:C4KQueue.Enqueue($message)
    }
}

# ---- app API (see docs/plugins.md; the comment names the permission plugin.json must declare) ----
# Entries (Get-C4KChildItem, Get-C4KItem, context selection) are hashtables: name, path, kind ('file', 'directory',
# 'drive', 'archive'…), size (-1 = unknown), modified (ISO 8601 or $null), hidden.
function Get-C4KContext { Invoke-C4K 'context.get' }                                                                # activePath, otherPath, selection
function Write-C4KLog([string] $Message) { [void](Invoke-C4K 'log.write' @{ message = $Message }) }                 # audit log (instead of Write-Host)
function Show-C4KNotification([string] $Message, [switch] $IsError, [string] $Target) { [void](Invoke-C4K 'ui.notify' @{ message = $Message; error = [bool]$IsError; target = $(if ($Target) { $Target } else { $null }) }) }  # -Target: path/link opened when the toast is clicked
function Show-C4KText([string] $Title, [string] $Text) { [void](Invoke-C4K 'ui.showText' @{ title = $Title; text = $Text }) }
function Confirm-C4K([string] $Title, [string] $Message) { Invoke-C4K 'ui.confirm' @{ title = $Title; message = $Message } }  # $true/$false
function Read-C4KInput([string] $Title, [string] $Message, [string] $Initial = '') { Invoke-C4K 'ui.prompt' @{ title = $Title; message = $Message; initial = $Initial } }  # string or $null
function Show-C4KDialog([string] $Title, [object[]] $Fields, [string] $Description, [string] $Submit, [string] $Message, [hashtable] $Errors) {  # one window, many fields -> hashtable id = value, or $null
    Invoke-C4K 'ui.dialog' @{ title = $Title; fields = @($Fields); description = $Description; submit = $Submit; message = $Message; errors = $Errors }
}
function Open-C4KUrl([string] $Url) { [void](Invoke-C4K 'ui.openUrl' @{ url = $Url }) }                            # internal browser, http(s) only
function Open-C4KFolder([string] $Path) { [void](Invoke-C4K 'ui.openFolder' @{ path = $Path }) }                  # the user chooses: Explorer or the active panel
function Search-C4KWeb([string] $Query, [string] $Engine) { [void](Invoke-C4K 'ui.search' @{ query = $Query; engine = $(if ($Engine) { $Engine } else { $null }) }) }  # engine: assistant, google, chatgpt…
function Copy-C4KText([string] $Text) { [void](Invoke-C4K 'ui.copyText' @{ text = $Text }) }                        # clipboard (write only)
function Get-C4KActionTarget([string] $Action, [hashtable] $Arguments = @{}) {  # preview button target "c4k:plugin/<action>?k=v" -> comes back as preview.action
    $query = @($Arguments.GetEnumerator() | ForEach-Object { [uri]::EscapeDataString([string] $_.Key) + '=' + [uri]::EscapeDataString([string] $_.Value) }) -join '&'
    'c4k:plugin/' + [uri]::EscapeDataString($Action) + $(if ($query) { '?' + $query } else { '' })
}
function New-C4KWebPreview([string] $Page, $State = $null) { @{ kind = 'web'; page = $Page; state = $State } }                    # result of preview.render: an HTML page from the preview's webRoot
function New-C4KFolderSuggestion([double] $Score, [string] $Message, $Options = $null) { @{ score = $Score; message = $Message; options = $Options } }  # result of preview.suggest (score 0..1)
function Get-C4KStorage([string] $Key, $Default = $null) {                                                           # the plugin's own settings/data (no permission)
    $value = Invoke-C4K 'storage.get' @{ key = $Key }
    if ($null -eq $value) { $Default } else { $value }
}
function Set-C4KStorage([string] $Key, $Value) { [void](Invoke-C4K 'storage.set' @{ key = $Key; value = $Value }) }  # any JSON value; $null removes the key
function Get-C4KChildItem([string] $Path) { Invoke-C4K 'fs.list' @{ path = $Path } }                                # fs.read -> entries
function Get-C4KItem([string] $Path) { Invoke-C4K 'fs.stat' @{ path = $Path } }                                     # fs.read -> entry or $null
function Get-C4KText([string] $Path, [int] $MaxBytes = 1048576) { Invoke-C4K 'fs.readText' @{ path = $Path; maxBytes = $MaxBytes } }  # fs.read
function Read-C4KBytes([string] $Path, [long] $Offset = 0, [int] $Length = 65536) {                                 # fs.read -> byte[] (max 16 MB)
    [Convert]::FromBase64String((Invoke-C4K 'fs.readBytes' @{ path = $Path; offset = $Offset; length = $Length }).data)
}
function Set-C4KText([string] $Path, [string] $Content) { [void](Invoke-C4K 'fs.writeText' @{ path = $Path; content = $Content }) }   # fs.write
function Write-C4KBytes([string] $Path, [byte[]] $Data) { [void](Invoke-C4K 'fs.writeBytes' @{ path = $Path; data = [Convert]::ToBase64String($Data) }) }  # fs.write
function New-C4KDirectory([string] $Path, [switch] $Hidden) { [void](Invoke-C4K 'fs.mkdir' @{ path = $Path; hidden = [bool]$Hidden }) }  # fs.write (-Hidden: Hidden attribute on disks/shares)
function Remove-C4KItem([string] $Path, [switch] $Recurse) { [void](Invoke-C4K 'fs.delete' @{ path = $Path; recursive = [bool]$Recurse }) }  # fs.write
function Move-C4KItem([string] $Path, [string] $Destination, [switch] $Force) { [void](Invoke-C4K 'fs.move' @{ from = $Path; to = $Destination; overwrite = [bool]$Force }) }  # fs.write
function Get-C4KProcess { Invoke-C4K 'process.list' }                                                               # process.read -> pid, parentPid, name, path, memoryBytes, started
function Stop-C4KProcess([int] $Id, [switch] $Tree) { [void](Invoke-C4K 'process.kill' @{ pid = $Id; tree = [bool]$Tree }) }  # process.kill
function Invoke-C4KProcess([string] $File, [string[]] $Arguments = @(), [string] $WorkingDirectory, [switch] $Elevated) {  # shell.run -> exitCode, output, error
    Invoke-C4K 'shell.run' @{ file = $File; args = @($Arguments); cwd = $WorkingDirectory; elevated = [bool]$Elevated }
}
function Get-C4KSystemInfo { Invoke-C4K 'system.info' }                                                             # system.info -> os, machine, user, processors, memoryBytes, drives
function Invoke-C4KHttpGet([string] $Url) { Invoke-C4K 'net.get' @{ url = $Url } }                                 # net -> body text
# -BodyFile streams a file as the body; -MultipartField/-MultipartPath [-MultipartFileName] send it as a form part. The app
# reads the file (fs.read, or the files granted to panel.receive) — it never passes through the plugin.
function Invoke-C4KHttpRequest([string] $Url, [string] $Method = 'GET', [hashtable] $Headers = @{}, [string] $Body, [string] $ContentType,
                               [string] $BodyFile, [string] $MultipartField, [string] $MultipartPath, [string] $MultipartFileName) {  # net -> status, headers, body
    $multipart = if ($MultipartPath) { @{ field = $MultipartField; path = $MultipartPath; fileName = $(if ($MultipartFileName) { $MultipartFileName } else { $null }) } } else { $null }
    Invoke-C4K 'net.request' @{ url = $Url; method = $Method; headers = $Headers; body = $(if ($Body) { $Body } else { $null }); contentType = $(if ($ContentType) { $ContentType } else { $null })
                                bodyFile = $(if ($BodyFile) { $BodyFile } else { $null }); multipart = $multipart }
}
# Progress of panel.receive/panel.provide ($p.jobId); a notification, nothing comes back.
function Send-C4KProgress([string] $JobId, [int] $Done, [int] $Total, [long] $Bytes = 0, [string] $Text) {
    Send-C4KMessage @{ jsonrpc = '2.0'; method = 'job.progress'; params = @{ jobId = $JobId; done = $Done; total = $Total; bytes = $Bytes; text = $(if ($Text) { $Text } else { $null }) } }
}

# ---- mail (permission "mail"; Save-C4KMail, Write-C4KMailDraft and Save-C4KMailAttachment also need fs.write, the others that read files fs.read) ----
# $Account = @{ imap = 'host:993'; smtp = 'host:465' (or $null); user = 'name'; interactive = $true }. The app owns the
# password and certificates: interactive = $false never shows a dialog and throws (Data['Code'] -32010/-32011) instead.
# Flags are 'seen', 'flagged', 'answered', 'draft'. Message files are .eml paths (any location the app can open).
function Get-C4KMailFolders([hashtable] $Account) { Invoke-C4K 'mail.folders' @{ account = $Account } }               # -> serverName, localName, separator, special, uidValidity, count, unread
function Sync-C4KMail([hashtable] $Account, [string] $Folder, [long] $UidValidity = 0, [long] $LastUid = 0, [long] $ModSeq = 0, [string] $Since, [long[]] $KnownUids = @(), [int] $Limit = 0) {  # -> uidValidity, uidValidityChanged, modSeq, new, flagChanges, removed, hasMore; Limit > 0 = at most that many new messages (lowest uids first)
    Invoke-C4K 'mail.sync' @{ account = $Account; folder = $Folder; uidValidity = $UidValidity; lastUid = $LastUid; modSeq = $ModSeq; since = $(if ($Since) { $Since } else { $null }); knownUids = @($KnownUids); limit = $(if ($Limit -gt 0) { $Limit } else { $null }) }
}
function Save-C4KMail([hashtable] $Account, [string] $Folder, [long] $Uid, [string] $Path) { (Invoke-C4K 'mail.fetch' @{ account = $Account; folder = $Folder; uid = $Uid; path = $Path }).bytes }  # fs.write -> bytes written
function Set-C4KMailFlags([hashtable] $Account, [string] $Folder, [long[]] $Uids, [string[]] $Add = @(), [string[]] $Remove = @()) { [void](Invoke-C4K 'mail.setFlags' @{ account = $Account; folder = $Folder; uids = @($Uids); add = @($Add); remove = @($Remove) }) }
function Move-C4KMail([hashtable] $Account, [string] $Folder, [long[]] $Uids, [string] $Target) { [void](Invoke-C4K 'mail.move' @{ account = $Account; folder = $Folder; uids = @($Uids); target = $Target }) }
function Remove-C4KMail([hashtable] $Account, [string] $Folder, [long[]] $Uids) { [void](Invoke-C4K 'mail.delete' @{ account = $Account; folder = $Folder; uids = @($Uids) }) }  # Trash, or expunge inside Trash
function Add-C4KMail([hashtable] $Account, [string] $Folder, [string] $Path, [string[]] $Flags = @()) { (Invoke-C4K 'mail.append' @{ account = $Account; folder = $Folder; path = $Path; flags = @($Flags) }).uid }  # fs.read -> new uid or $null
function Read-C4KMail([string] $Path) { Invoke-C4K 'mail.parse' @{ path = $Path } }                                 # fs.read -> from, to, cc, subject, date, messageId, references, bodyText, attachments
function Save-C4KMailAttachment([string] $Path, [int] $Index, [string] $Target) { [void](Invoke-C4K 'mail.saveAttachment' @{ path = $Path; index = $Index; target = $Target }) }  # fs.read + fs.write
function New-C4KMailReply([string] $Path, [string] $Mode = 'reply', [string] $Self = '') { Invoke-C4K 'mail.reply' @{ path = $Path; mode = $Mode; self = $Self } }  # Mode: reply | replyAll | forward -> draft (fs.read)
function Write-C4KMailDraft([string] $Path, [hashtable] $Draft) { [void](Invoke-C4K 'mail.compose' @{ path = $Path; draft = $Draft }) }  # fs.write (+ fs.read for attachments); draft = to, cc, bcc, subject, body, inReplyTo, references, attachments, from
function Send-C4KMail([hashtable] $Account, [string] $Path) { (Invoke-C4K 'mail.send' @{ account = $Account; path = $Path }).messageId }  # fs.read -> Message-ID
function Find-C4KMailConfig([string] $Email) { Invoke-C4K 'mail.discover' @{ email = $Email } }  # -> imap, smtp, authMethods, source, confidence, requiresUserConfirmation, confirmationReason, attempts; never logs in
function Request-C4KBackgroundTick { [void](Invoke-C4K 'background.wake') }  # background: ask the app for a background.tick now (answer ticks with Register-C4KHandler 'background.tick' { @{ more = $false } })

function Start-C4KPlugin {
    while ($true) {
        $message = if ($script:C4KQueue.Count -gt 0) { $script:C4KQueue.Dequeue() } else { Read-C4KMessage }
        $method = $message.method
        if (-not $method -or $method.StartsWith('$/')) { continue }
        $id = $message.id
        if ($method -eq 'shutdown') { Send-C4KMessage @{ jsonrpc = '2.0'; id = $id; result = $null }; exit 0 }
        try {
            $handler = $script:C4KHandlers[$method]
            $result = if ($handler) { & $handler ($message.params ?? @{}) }
                      elseif ($method -eq 'initialize') { @{} }
                      else { throw "Unknown method $method" }
            if ($null -ne $id) { Send-C4KMessage @{ jsonrpc = '2.0'; id = $id; result = $result } }
        }
        catch {
            if ($null -ne $id) {
                $code = if ($_.Exception.Data.Contains('Code')) { $_.Exception.Data['Code'] } else { -32000 }
                $failure = @{ code = $code; message = "$_" }
                if ($null -ne $_.Exception.Data['ErrorData']) { $failure.data = $_.Exception.Data['ErrorData'] }
                Send-C4KMessage @{ jsonrpc = '2.0'; id = $id; error = $failure }
            }
        }
    }
}
