using System.Text.Json;
using System.Text.Json.Serialization;

namespace Commander4K.Plugins.BuiltIn.PowerShell;

/// <param name="Script">PowerShell code. Placeholders: <c>{path}</c> active folder, <c>{file}</c> entry under the cursor,
/// <c>{files}</c> selected entries (as an array), <c>{input:Question}</c> asks the user. Values are inserted as quoted
/// literals, so they cannot inject code.</param>
/// <param name="RequiresAdmin">Runs elevated in its own console window (output is not captured).</param>
/// <param name="Confirm">Shows the script and asks before running (use for anything that changes the system).</param>
public sealed record PowerShellCommand(
    string Id,
    string Category,
    string Title,
    string Description,
    string Script,
    bool RequiresAdmin = false,
    bool Confirm = false);

public sealed class PowerShellCatalogFile
{
    public List<PowerShellCommand> Commands { get; set; } = [];
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PowerShellCatalogFile))]
internal sealed partial class PowerShellJsonContext : JsonSerializerContext;

/// <summary>Loads <c>config/powershell.json</c>, creating it with <see cref="Defaults"/> on first run.</summary>
public static class PowerShellCatalog
{
    public static async Task<IReadOnlyList<PowerShellCommand>> LoadOrCreateAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using var output = File.Create(path);
            await JsonSerializer.SerializeAsync(output, new PowerShellCatalogFile { Commands = [.. Defaults] }, PowerShellJsonContext.Default.PowerShellCatalogFile, cancellationToken)
                .ConfigureAwait(false);
            return Defaults;
        }

        await using var input = File.OpenRead(path);
        var file = await JsonSerializer.DeserializeAsync(input, PowerShellJsonContext.Default.PowerShellCatalogFile, cancellationToken).ConfigureAwait(false);
        return file?.Commands ?? [];
    }

    public static IReadOnlyList<PowerShellCommand> Defaults { get; } =
    [
        new("sys-info", "System", "Podsumowanie systemu", "Najważniejsze informacje o systemie (Get-ComputerInfo).",
            "Get-ComputerInfo | Select-Object OsName, OsDisplayVersion, OsBuildNumber, CsName, CsProcessors, @{n='RAM (GB)';e={[math]::Round($_.CsTotalPhysicalMemory/1GB,1)}}, OsLastBootUpTime, OsUptime | Format-List"),
        new("sys-uptime", "System", "Czas pracy od uruchomienia", "Jak długo działa system.",
            "$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime; \"Start: $boot\"; \"Czas pracy: $((Get-Date) - $boot)\""),
        new("sys-programs", "System", "Zainstalowane programy", "Lista programów z rejestru (64- i 32-bitowe, dla komputera i użytkownika).",
            "Get-ItemProperty 'HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*','HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*','HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*' -ErrorAction SilentlyContinue | Where-Object DisplayName | Sort-Object DisplayName | Format-Table DisplayName, DisplayVersion, Publisher -AutoSize"),
        new("sys-services", "System", "Działające usługi", "Usługi w stanie Running.",
            "Get-Service | Where-Object Status -eq Running | Sort-Object DisplayName | Format-Table Name, DisplayName, StartType -AutoSize"),
        new("sys-errors", "System", "Błędy systemowe (24 h)", "Ostatnie zdarzenia o poziomie Błąd z dziennika System.",
            "Get-WinEvent -FilterHashtable @{LogName='System'; Level=1,2; StartTime=(Get-Date).AddDays(-1)} -MaxEvents 100 -ErrorAction SilentlyContinue | Format-Table TimeCreated, ProviderName, Id, Message -Wrap"),
        new("sys-sfc", "System", "Sprawdź pliki systemowe (SFC)", "sfc /verifyonly — sprawdzenie integralności plików Windows (bez naprawy).",
            "sfc /verifyonly", RequiresAdmin: true, Confirm: true),
        new("sys-battery", "System", "Raport baterii", "Generuje raport powercfg i otwiera go w przeglądarce.",
            "$out = Join-Path $env:TEMP 'battery-report.html'; powercfg /batteryreport /output $out | Out-Null; Start-Process $out; \"Raport: $out\""),

        new("net-listen", "Sieć", "Porty nasłuchujące", "Otwarte porty TCP z nazwą procesu.",
            "Get-NetTCPConnection -State Listen | Sort-Object LocalPort | Select-Object LocalAddress, LocalPort, OwningProcess, @{n='Proces';e={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}} | Format-Table -AutoSize"),
        new("net-ip", "Sieć", "Konfiguracja IP", "Adresy, bramy i DNS aktywnych kart sieciowych.",
            "Get-NetIPConfiguration | Where-Object NetAdapter.Status -eq 'Up' | Format-List InterfaceAlias, IPv4Address, IPv6Address, IPv4DefaultGateway, DNSServer"),
        new("net-test", "Sieć", "Test połączenia z hostem", "Ping, trasa i port 443 do podanego hosta.",
            "Test-NetConnection -ComputerName {input:Adres hosta (np. github.com)} -Port 443 -InformationLevel Detailed"),
        new("net-flushdns", "Sieć", "Wyczyść pamięć DNS", "Clear-DnsClientCache.",
            "Clear-DnsClientCache; 'Wyczyszczono pamięć podręczną DNS.'", RequiresAdmin: true, Confirm: true),
        new("net-wifi", "Sieć", "Profile Wi-Fi", "Zapisane sieci Wi-Fi.",
            "netsh wlan show profiles"),

        new("file-hash", "Pliki", "Suma SHA-256 zaznaczonych", "Get-FileHash dla zaznaczonych plików.",
            "Get-FileHash -Algorithm SHA256 -LiteralPath {files} | Format-Table Hash, Path -AutoSize"),
        new("file-signature", "Pliki", "Podpis cyfrowy", "Sprawdza podpis Authenticode zaznaczonych plików.",
            "Get-AuthenticodeSignature -LiteralPath {files} | Format-List Path, Status, StatusMessage, SignerCertificate"),
        new("file-largest", "Pliki", "Największe pliki w folderze (top 25)", "Rekurencyjnie, w bieżącym katalogu.",
            "Get-ChildItem -LiteralPath {path} -File -Recurse -Force -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 25 @{n='MB';e={[math]::Round($_.Length/1MB,1)}}, FullName | Format-Table -AutoSize"),
        new("file-unblock", "Pliki", "Odblokuj pobrane pliki", "Usuwa oznaczenie „pobrane z Internetu” (Unblock-File).",
            "Unblock-File -LiteralPath {files}; 'Odblokowano.'", Confirm: true),
        new("file-acl", "Pliki", "Uprawnienia (ACL)", "Lista uprawnień NTFS pliku pod kursorem.",
            "(Get-Acl -LiteralPath {file}).Access | Format-Table IdentityReference, FileSystemRights, AccessControlType, IsInherited -AutoSize"),

        new("proc-memory", "Procesy", "Top 20 wg pamięci", "Procesy zużywające najwięcej pamięci.",
            "Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 20 Name, Id, @{n='MB';e={[math]::Round($_.WorkingSet64/1MB)}}, CPU | Format-Table -AutoSize"),
        new("proc-cpu", "Procesy", "Top 20 wg czasu CPU", "Procesy o największym łącznym czasie procesora.",
            "Get-Process | Sort-Object CPU -Descending | Select-Object -First 20 Name, Id, CPU, @{n='MB';e={[math]::Round($_.WorkingSet64/1MB)}} | Format-Table -AutoSize"),
    ];
}
