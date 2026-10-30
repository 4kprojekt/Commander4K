using System.Collections.Concurrent;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Security.Principal;
using System.Text;
using Commander4K.PluginSdk;
using WinRegistry = Microsoft.Win32.Registry;

namespace Commander4K.Plugins.BuiltIn.SystemInfo;

/// <summary>
/// Collects as much as possible about the machine: OS, hardware (WMI), CPU features, memory modules, GPUs, disks,
/// network, security, hot-fixes, start-up programs, environment. Sections are gathered in parallel and each one
/// fails independently (e.g. without administrator rights), so one missing source never hides the others.
/// </summary>
public sealed class SystemInfoPanelPlugin : IPanelPlugin
{
    public const string PluginId = "builtin.sysinfo";

    public string Id => PluginId;

    public string DisplayName => "System information";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.sysinfo", "Informacje o systemie", "System, sprzęt, sieć, bezpieczeństwo, aktualizacje, autostart.", PluginPermissions.SystemInfo);

    public string Title => "Informacje o systemie";

    public string Description => "System operacyjny, sprzęt, procesor, pamięć, dyski, sieć, bezpieczeństwo, aktualizacje, autostart.";

    public IPanelSession CreateSession(PanelRequest request, IPluginHost host) => new Session();

    private sealed class Session : IPanelSession
    {
        private static readonly ColumnDefinition[] Columns =
        [
            new("property", "Właściwość", ColumnAlignment.Left, 300),
            new("value", "Wartość", ColumnAlignment.Left, 600),
        ];

        private IReadOnlyList<Section> _sections = [];

        public TimeSpan? RefreshInterval => null;

        public IReadOnlyList<PanelAction> Actions { get; } =
        [
            new("report", "Raport tekstowy", "F3", AppliesToRows: false),
        ];

        public async Task<PanelSnapshot> LoadAsync(PanelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            progress?.Report("Zbieranie informacji o systemie…");
            var collectors = new (string Title, Func<List<KeyValuePair<string, string>>> Collect)[]
            {
                ("System operacyjny", OperatingSystem),
                ("Komputer", Computer),
                ("Procesor", Processor),
                ("Pamięć", Memory),
                ("Karty graficzne", Graphics),
                ("Dyski fizyczne", PhysicalDisks),
                ("Woluminy", Volumes),
                ("Sieć", Network),
                ("Bezpieczeństwo", Security),
                ("Bateria", Battery),
                ("Aktualizacje (hotfixy)", HotFixes),
                ("Autostart", Startup),
                (".NET i aplikacja", Runtime),
                ("Zmienne środowiskowe", EnvironmentVariables),
            };

            var results = new ConcurrentDictionary<int, Section>();
            await Parallel.ForEachAsync(
                Enumerable.Range(0, collectors.Length),
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 6 },
                (index, _) =>
                {
                    var (title, collect) = collectors[index];
                    List<KeyValuePair<string, string>> items;
                    try
                    {
                        items = collect();
                    }
                    catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or IOException or InvalidOperationException)
                    {
                        items = [new("Błąd", ex.Message)];
                    }

                    results[index] = new Section(title, items);
                    return ValueTask.CompletedTask;
                });

            _sections = [.. results.OrderBy(r => r.Key).Select(r => r.Value)];
            var rows = new List<PanelRow>();
            foreach (var section in _sections)
            {
                var sectionId = section.Title;
                rows.Add(new PanelRow(sectionId, null, [section.Title, $"{section.Items.Count} pozycji"], RowSeverity.Highlight, ExpandedByDefault: section.Items.Count <= 20));
                for (var i = 0; i < section.Items.Count; i++)
                {
                    var (key, value) = section.Items[i];
                    rows.Add(new PanelRow($"{sectionId}/{i}", sectionId, [key, value], key == "Błąd" ? RowSeverity.Error : RowSeverity.Normal));
                }
            }

            return new PanelSnapshot(Columns, rows, $"{Environment.MachineName} · {_sections.Sum(s => s.Items.Count)} informacji w {_sections.Count} sekcjach");
        }

        public Task<PanelDetails?> GetDetailsAsync(string rowId, CancellationToken cancellationToken)
        {
            var slash = rowId.LastIndexOf('/');
            var section = _sections.FirstOrDefault(s => s.Title == (slash < 0 ? rowId : rowId[..slash]));
            if (section is null)
            {
                return Task.FromResult<PanelDetails?>(null);
            }

            if (slash < 0)
            {
                return Task.FromResult<PanelDetails?>(new PanelDetails([new PanelDetailSection(section.Title, section.Items)]));
            }

            var item = section.Items[int.Parse(rowId[(slash + 1)..], CultureInfo.InvariantCulture)];
            return Task.FromResult<PanelDetails?>(new PanelDetails([new PanelDetailSection(item.Key, [new("Wartość", item.Value)])]));
        }

        public Task<string?> ExecuteActionAsync(string actionId, IReadOnlyList<string> rowIds, CancellationToken cancellationToken)
        {
            var report = new StringBuilder();
            report.AppendLine(CultureInfo.InvariantCulture, $"Commander4K — raport systemu {Environment.MachineName}, {DateTime.Now:yyyy-MM-dd HH:mm}");
            foreach (var section in _sections)
            {
                report.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"== {section.Title} ==");
                foreach (var (key, value) in section.Items)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"{key,-40} {value}");
                }
            }

            return Task.FromResult<string?>(report.ToString());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Section(string Title, List<KeyValuePair<string, string>> Items);

    // ---- collectors ----

    private static List<KeyValuePair<string, string>> OperatingSystem()
    {
        var items = new List<KeyValuePair<string, string>>();
        foreach (var os in Query("SELECT * FROM Win32_OperatingSystem"))
        {
            Add(items, "Nazwa", os["Caption"]);
            using var current = WinRegistry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            Add(items, "Wersja", $"{current?.GetValue("DisplayVersion")} (kompilacja {os["BuildNumber"]}.{current?.GetValue("UBR")})");
            Add(items, "Architektura", os["OSArchitecture"]);
            Add(items, "Język / region", $"{CultureInfo.InstalledUICulture.DisplayName} / {RegionInfo.CurrentRegion.DisplayName}");
            Add(items, "Zainstalowano", FormatDate(os["InstallDate"]));
            var boot = ManagementDateTimeConverter.ToDateTime((string)os["LastBootUpTime"]);
            Add(items, "Ostatni start", boot.ToString("g", CultureInfo.CurrentCulture));
            Add(items, "Czas pracy", FormatDuration(DateTime.Now - boot));
            Add(items, "Zarejestrowany użytkownik", os["RegisteredUser"]);
            Add(items, "Katalog Windows", os["WindowsDirectory"]);
            Add(items, "Strefa czasowa", TimeZoneInfo.Local.DisplayName);
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> Computer()
    {
        var items = new List<KeyValuePair<string, string>>
        {
            new("Nazwa komputera", Environment.MachineName),
            new("Użytkownik", $@"{Environment.UserDomainName}\{Environment.UserName}"),
        };
        foreach (var cs in Query("SELECT * FROM Win32_ComputerSystem"))
        {
            Add(items, "Producent", cs["Manufacturer"]);
            Add(items, "Model", cs["Model"]);
            Add(items, "Typ systemu", cs["SystemType"]);
            Add(items, (bool)(cs["PartOfDomain"] ?? false) ? "Domena" : "Grupa robocza", cs["PartOfDomain"] is true ? cs["Domain"] : cs["Workgroup"]);
            Add(items, "Hiperwizor obecny", YesNo(cs["HypervisorPresent"]));
        }

        foreach (var bios in Query("SELECT * FROM Win32_BIOS"))
        {
            Add(items, "BIOS / UEFI", $"{bios["Manufacturer"]} {bios["SMBIOSBIOSVersion"]} ({FormatDate(bios["ReleaseDate"])})");
            Add(items, "Numer seryjny", bios["SerialNumber"]);
        }

        foreach (var board in Query("SELECT * FROM Win32_BaseBoard"))
        {
            Add(items, "Płyta główna", $"{board["Manufacturer"]} {board["Product"]}");
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> Processor()
    {
        var items = new List<KeyValuePair<string, string>>();
        foreach (var cpu in Query("SELECT * FROM Win32_Processor"))
        {
            Add(items, "Model", (cpu["Name"] as string)?.Trim());
            Add(items, "Rdzenie / wątki", $"{cpu["NumberOfCores"]} / {cpu["NumberOfLogicalProcessors"]}");
            Add(items, "Taktowanie maks.", $"{cpu["MaxClockSpeed"]} MHz");
            Add(items, "Cache L2 / L3", $"{cpu["L2CacheSize"]} KB / {cpu["L3CacheSize"]} KB");
            Add(items, "Wirtualizacja w firmware", YesNo(cpu["VirtualizationFirmwareEnabled"]));
        }

        Add(items, "Architektura procesu", RuntimeInformation.ProcessArchitecture);
        Add(items, "Architektura systemu", RuntimeInformation.OSArchitecture);
        Add(items, "Akceleracja wektorowa", $"128-bit: {YesNo(Vector128.IsHardwareAccelerated)}, 256-bit: {YesNo(Vector256.IsHardwareAccelerated)}, 512-bit: {YesNo(Vector512.IsHardwareAccelerated)}");
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            Add(items, "Rozszerzenia ARM", $"AdvSIMD: {YesNo(AdvSimd.IsSupported)}, AES: {YesNo(System.Runtime.Intrinsics.Arm.Aes.IsSupported)}, CRC32: {YesNo(Crc32.IsSupported)}, SHA256: {YesNo(System.Runtime.Intrinsics.Arm.Sha256.IsSupported)}, Dp: {YesNo(Dp.IsSupported)}");
        }
        else
        {
            Add(items, "Rozszerzenia x86", $"AVX2: {YesNo(Avx2.IsSupported)}, AVX-512F: {YesNo(Avx512F.IsSupported)}, AES-NI: {YesNo(System.Runtime.Intrinsics.X86.Aes.IsSupported)}, BMI2: {YesNo(Bmi2.IsSupported)}");
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> Memory()
    {
        var items = new List<KeyValuePair<string, string>>();
        foreach (var os in Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
        {
            var total = Convert.ToInt64(os["TotalVisibleMemorySize"], CultureInfo.InvariantCulture) * 1024;
            var free = Convert.ToInt64(os["FreePhysicalMemory"], CultureInfo.InvariantCulture) * 1024;
            Add(items, "Dostępna dla systemu", Bytes(total));
            Add(items, "Wolna", $"{Bytes(free)} ({100.0 * free / total:0}%)");
        }

        var slot = 0;
        foreach (var module in Query("SELECT * FROM Win32_PhysicalMemory"))
        {
            slot++;
            Add(items, $"Moduł {slot} ({module["DeviceLocator"]})",
                $"{Bytes(Convert.ToInt64(module["Capacity"], CultureInfo.InvariantCulture))}, {module["ConfiguredClockSpeed"] ?? module["Speed"]} MT/s, {(module["Manufacturer"] as string)?.Trim()} {(module["PartNumber"] as string)?.Trim()}");
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> Graphics()
    {
        var items = new List<KeyValuePair<string, string>>();
        foreach (var gpu in Query("SELECT * FROM Win32_VideoController"))
        {
            Add(items, gpu["Name"] as string ?? "GPU",
                $"sterownik {gpu["DriverVersion"]}, {gpu["CurrentHorizontalResolution"]}×{gpu["CurrentVerticalResolution"]} @ {gpu["CurrentRefreshRate"]} Hz");
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> PhysicalDisks()
    {
        var items = new List<KeyValuePair<string, string>>();
        foreach (var disk in Query("SELECT * FROM Win32_DiskDrive"))
        {
            Add(items, disk["Model"] as string ?? "Dysk",
                $"{Bytes(Convert.ToInt64(disk["Size"] ?? 0L, CultureInfo.InvariantCulture))}, {disk["InterfaceType"]}, {disk["MediaType"]}, partycji: {disk["Partitions"]}");
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> Volumes()
    {
        var items = new List<KeyValuePair<string, string>>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                Add(items, drive.Name, drive.IsReady
                    ? $"{drive.VolumeLabel} [{drive.DriveFormat}] {drive.DriveType}: wolne {Bytes(drive.AvailableFreeSpace)} z {Bytes(drive.TotalSize)}"
                    : $"{drive.DriveType} (niegotowy)");
            }
            catch (IOException ex)
            {
                Add(items, drive.Name, ex.Message);
            }
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> Network()
    {
        var items = new List<KeyValuePair<string, string>>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var ip = nic.GetIPProperties();
            var addresses = string.Join(", ", ip.UnicastAddresses.Select(a => a.Address.ToString()));
            var gateways = string.Join(", ", ip.GatewayAddresses.Select(g => g.Address.ToString()));
            var dns = string.Join(", ", ip.DnsAddresses.Select(d => d.ToString()));
            var speed = nic.Speed > 0 ? $"{nic.Speed / 1_000_000} Mb/s" : "-";
            Add(items, nic.Name, $"{nic.OperationalStatus}, {nic.NetworkInterfaceType}, {speed}, MAC {FormatMac(nic.GetPhysicalAddress())}; IP: {addresses}; brama: {gateways}; DNS: {dns}");
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> Security()
    {
        var items = new List<KeyValuePair<string, string>>();
        using var identity = WindowsIdentity.GetCurrent();
        Add(items, "Uruchomiono jako administrator", YesNo(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)));
        using (var secureBoot = WinRegistry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
        {
            Add(items, "Secure Boot", secureBoot?.GetValue("UEFISecureBootEnabled") is int enabled ? YesNo(enabled == 1) : "nieznany (BIOS legacy?)");
        }

        using (var uac = WinRegistry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
        {
            Add(items, "Kontrola konta (UAC)", YesNo(uac?.GetValue("EnableLUA") is 1));
        }

        try
        {
            foreach (var av in Query("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2"))
            {
                Add(items, "Antywirus", av["displayName"]);
            }
        }
        catch (ManagementException)
        {
            // SecurityCenter2 is absent on Windows Server.
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> Battery()
    {
        var items = new List<KeyValuePair<string, string>>();
        foreach (var battery in Query("SELECT * FROM Win32_Battery"))
        {
            Add(items, battery["Name"] as string ?? "Bateria", $"naładowanie {battery["EstimatedChargeRemaining"]}%, pozostało ok. {battery["EstimatedRunTime"]} min, status {battery["BatteryStatus"]}");
        }

        if (items.Count == 0)
        {
            items.Add(new("Bateria", "brak (komputer stacjonarny)"));
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> HotFixes() =>
        [.. Query("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering")
            .Select(fix => new KeyValuePair<string, string>((string)fix["HotFixID"], $"{fix["Description"]}, {fix["InstalledOn"]}"))
            .OrderByDescending(f => f.Key, StringComparer.Ordinal)];

    private static List<KeyValuePair<string, string>> Startup() =>
        [.. Query("SELECT Name, Command, Location FROM Win32_StartupCommand")
            .Select(item => new KeyValuePair<string, string>((string)item["Name"], $"{item["Command"]}  [{item["Location"]}]"))];

    private static List<KeyValuePair<string, string>> Runtime() =>
    [
        new(".NET", RuntimeInformation.FrameworkDescription),
        new("Środowisko", RuntimeInformation.RuntimeIdentifier),
        new("System (wg .NET)", RuntimeInformation.OSDescription),
        new("Procesory logiczne (wg .NET)", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)),
        new("Katalog aplikacji", AppContext.BaseDirectory),
        new("Tryb serwerowy GC", YesNo(System.Runtime.GCSettings.IsServerGC)),
    ];

    private static List<KeyValuePair<string, string>> EnvironmentVariables() =>
        [.. Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => new KeyValuePair<string, string>((string)e.Key, e.Value as string ?? string.Empty))
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)];

    // ---- helpers ----

    private static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        using var results = searcher.Get();
        return [.. results.Cast<ManagementBaseObject>()];
    }

    private static void Add(List<KeyValuePair<string, string>> items, string key, object? value)
    {
        var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            items.Add(new(key, text));
        }
    }

    private static string YesNo(object? value) => value is true ? "tak" : value is false ? "nie" : "?";

    private static string FormatDate(object? wmiDate) =>
        wmiDate is string text && text.Length >= 8
            ? (text.Length >= 14 ? ManagementDateTimeConverter.ToDateTime(text).ToString("d", CultureInfo.CurrentCulture) : $"{text[..4]}-{text[4..6]}-{text[6..8]}")
            : string.Empty;

    private static string FormatDuration(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h {span.Minutes} min" : $"{span.Hours} h {span.Minutes} min";

    private static string FormatMac(PhysicalAddress address) =>
        string.Join(':', address.GetAddressBytes().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
