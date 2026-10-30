# Commander4K plugin protocol v1 — PowerShell 7 client (copy next to your plugin's main.ps1 and dot-source it:
#   . "$PSScriptRoot/c4k.ps1"
# Register handlers with Register-C4KHandler 'command.execute' { param($p) ... }, call the app with the
# Invoke-C4K* functions (every call is permission-checked against plugin.json and audited), then Start-C4KPlugin.
# Messages are JSON-RPC 2.0, one per line on stdin/stdout.

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

# Request to the app; blocks until it answers. Throws on refusal (code -32001 = missing permission).
function Invoke-C4K([string] $Method, [hashtable] $Params = @{}) {
    $script:C4KNextId++
    $id = "p$($script:C4KNextId)"
    Send-C4KMessage @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Params }
    while ($true) {
        $message = Read-C4KMessage
        if ($message.id -eq $id -and -not $message.ContainsKey('method')) {
            if ($message.ContainsKey('error')) { throw "C4K $($message.error.code): $($message.error.message)" }
            return $message.result
        }
        $script:C4KQueue.Enqueue($message)
    }
}

# ---- app API (see docs/plugins.md) ----
function Get-C4KContext { Invoke-C4K 'context.get' }
function Write-C4KLog([string] $Message) { [void](Invoke-C4K 'log.write' @{ message = $Message }) }
function Show-C4KNotification([string] $Message, [switch] $IsError) { [void](Invoke-C4K 'ui.notify' @{ message = $Message; error = [bool]$IsError }) }
function Show-C4KText([string] $Title, [string] $Text) { [void](Invoke-C4K 'ui.showText' @{ title = $Title; text = $Text }) }
function Confirm-C4K([string] $Title, [string] $Message) { Invoke-C4K 'ui.confirm' @{ title = $Title; message = $Message } }
function Read-C4KInput([string] $Title, [string] $Message, [string] $Initial = '') { Invoke-C4K 'ui.prompt' @{ title = $Title; message = $Message; initial = $Initial } }
function Open-C4KUrl([string] $Url) { [void](Invoke-C4K 'ui.openUrl' @{ url = $Url }) }                            # internal browser, http(s) only
function Search-C4KWeb([string] $Query, [string] $Engine) { [void](Invoke-C4K 'ui.search' @{ query = $Query; engine = $(if ($Engine) { $Engine } else { $null }) }) }  # engine: assistant, google, chatgpt…
function Get-C4KChildItem([string] $Path) { Invoke-C4K 'fs.list' @{ path = $Path } }                                # fs.read
function Get-C4KText([string] $Path, [int] $MaxBytes = 1048576) { Invoke-C4K 'fs.readText' @{ path = $Path; maxBytes = $MaxBytes } }  # fs.read
function Set-C4KText([string] $Path, [string] $Content) { [void](Invoke-C4K 'fs.writeText' @{ path = $Path; content = $Content }) }   # fs.write
function Invoke-C4KProcess([string] $File, [string[]] $Arguments = @(), [string] $WorkingDirectory) {               # shell.run
    Invoke-C4K 'shell.run' @{ file = $File; args = @($Arguments); cwd = $WorkingDirectory }
}
function Invoke-C4KHttpGet([string] $Url) { Invoke-C4K 'net.get' @{ url = $Url } }                                 # net

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
            if ($null -ne $id) { Send-C4KMessage @{ jsonrpc = '2.0'; id = $id; error = @{ code = -32000; message = "$_" } } }
        }
    }
}
