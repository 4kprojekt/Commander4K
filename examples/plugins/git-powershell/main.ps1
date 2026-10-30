# Example plugin: git status / log for the active folder. git runs through the app (shell.run permission),
# so every command line is checked against plugin.json and written to the audit log.
. "$PSScriptRoot/c4k.ps1"

function Invoke-Git([string] $Folder, [string[]] $Arguments) {
    $result = Invoke-C4KProcess -File 'git' -Arguments (@('-C', $Folder) + $Arguments) -WorkingDirectory $Folder
    if ($result.exitCode -ne 0) { throw ($result.error.Trim() ? $result.error.Trim() : "git zakończył się kodem $($result.exitCode)") }
    return $result.output
}

Register-C4KHandler 'command.execute' {
    param($p)
    $folder = $p.context.activePath
    @{ text = @{ title = "git status — $folder"; content = (Invoke-Git $folder @('status', '--short', '--branch')) } }
}

Register-C4KHandler 'panel.load' {
    param($p)
    $folder = $p.context.activePath
    $lines = (Invoke-Git $folder @('log', '-50', '--date=short', '--pretty=format:%h%x09%ad%x09%an%x09%s')) -split "`n" | Where-Object { $_ }
    $rows = foreach ($line in $lines) {
        $hash, $date, $author, $subject = $line -split "`t", 4
        @{ id = $hash; cells = @($hash, $date, $author, $subject) }
    }
    @{
        columns = @(
            @{ id = 'hash'; header = 'Commit'; width = 90 },
            @{ id = 'date'; header = 'Data'; width = 100 },
            @{ id = 'author'; header = 'Autor'; width = 160 },
            @{ id = 'subject'; header = 'Opis'; width = 520 }
        )
        rows = @($rows)
        status = "$(@($rows).Count) commitów · $folder"
    }
}

Register-C4KHandler 'panel.details' {
    param($p)
    $context = Get-C4KContext
    $stat = Invoke-Git $context.activePath @('show', '--stat', '--format=%an <%ae>%n%ad%n%n%B', $p.rowId)
    @{ sections = @(@{ title = "Commit $($p.rowId)"; items = @(, @('Zmiany', $stat.Trim())) }) }
}

Register-C4KHandler 'panel.action' {
    param($p)
    @{ text = (Invoke-Git $p.context.activePath (@('show', '--stat') + @($p.rowIds))) }
}

Start-C4KPlugin
