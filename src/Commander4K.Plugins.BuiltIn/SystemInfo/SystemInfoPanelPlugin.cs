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
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using WinRegistry = Microsoft.Win32.Registry;

namespace Commander4K.Plugins.BuiltIn.SystemInfo;

/// <summary>
/// Collects as much as possible about the machine: OS, hardware (WMI), CPU features, memory modules, GPUs, disks,
/// network, security, hot-fixes, start-up programs, environment. Sections are gathered in parallel and each one
/// fails independently (e.g. without administrator rights), so one missing source never hides the others.
/// </summary>
public sealed class SystemInfoPanelPlugin : IPanelCardPlugin
{
    public const string PluginId = "builtin.sysinfo";

    public string Id => PluginId;

    public string DisplayName => "System information";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.sysinfo", Loc.T("Informacje o systemie"), Loc.T("System, sprzęt, sieć, bezpieczeństwo, aktualizacje, autostart."), PluginPermissions.SystemInfo);

    public string Title => Loc.T("Informacje o systemie");

    public string Description => Loc.T("System operacyjny, sprzęt, procesor, pamięć, dyski, sieć, bezpieczeństwo, aktualizacje, autostart.");

    public IPanelSession CreateSession(PanelRequest request, IPluginHost host) => new Session();

    /// <summary>The dashboard widget: processor, memory in use (a chart that grows on each refresh), system disk, uptime.</summary>
    public async Task<PreviewCard> LoadCardAsync(PreviewCardRequest request, IPluginHost host, CancellationToken cancellationToken)
    {
        var info = await host.GetSystemInfoAsync(cancellationToken).ConfigureAwait(false);
        var memory = GC.GetGCMemoryInfo();
        var total = memory.TotalAvailableMemoryBytes > 0 ? memory.TotalAvailableMemoryBytes : info.TotalMemoryBytes;
        var used = total > 0 ? Math.Clamp((double)memory.MemoryLoadBytes / total, 0, 1) : 0;
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var items = new List<CardItem>
        {
            new CardValue(Loc.T("Procesor"), Loc.T("{0} wątków, {1}", info.ProcessorCount, info.Architecture)),
            new CardValue(Loc.T("Czas pracy"), Loc.T("{0} d {1} h {2} min", uptime.Days, uptime.Hours, uptime.Minutes)),
            new CardProgress(Loc.T("Pamięć"), used, Loc.T("{0} z {1}", Bytes(memory.MemoryLoadBytes), Bytes(total))),
        };
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        if (info.Drives.FirstOrDefault(d => string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase)) is { TotalBytes: > 0 } drive)
        {
            var free = drive.FreeBytes ?? 0;
            items.Add(new CardProgress(drive.Name.TrimEnd('\\'), Math.Clamp((double)(drive.TotalBytes.Value - free) / drive.TotalBytes.Value, 0, 1),
                Loc.T("{0} wolne z {1}", Bytes(free), Bytes(drive.TotalBytes.Value))));
        }

        var chart = request.Previous?.Items.OfType<CardChart>().FirstOrDefault()?.Chart ?? new ChartData(ChartKind.Line, [], "%", 0, 100);
        items.Add(new CardChart(chart.Append(Loc.T("Pamięć"), new ChartPoint(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Math.Round(used * 100, 1)))));
        return new PreviewCard(null, info.MachineName, "🖥", used >= 0.9 ? CardStatus.Warning : CardStatus.Ok, items,
            [new CardAction(Loc.T("Otwórz"), CardActions.OpenPanel(PluginId)), new CardAction(Loc.T("Odśwież"), CardActions.Refresh)]);
    }

    private sealed class Session : IPanelSession
    {
        private static readonly ColumnDefinition[] Columns =
        [
            new("property", Loc.T("Właściwość"), ColumnAlignment.Left, 300),
            new("value", Loc.T("Wartość"), ColumnAlignment.Left, 600),
        ];

        private IReadOnlyList<Section> _sections = [];

        public TimeSpan? RefreshInterval => null;

        public IReadOnlyList<PanelAction> Actions { get; } =
        [
            new("report", Loc.T("Raport tekstowy"), "F3", AppliesToRows: false),
        ];

        public async Task<PanelSnapshot> LoadAsync(PanelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(Loc.T("Zbieranie informacji o systemie…"));
            var collectors = new (string Title, Func<List<KeyValuePair<string, string>>> Collect)[]
            {
                (Loc.T("System operacyjny"), OperatingSystem),
                (Loc.T("Komputer"), Computer),
                (Loc.T("Procesor"), Processor),
                (Loc.T("Pamięć"), Memory),
                (Loc.T("Karty graficzne"), Graphics),
                (Loc.T("Dyski fizyczne"), PhysicalDisks),
                (Loc.T("Woluminy"), Volumes),
                (Loc.T("Sieć"), Network),
                (Loc.T("Bezpieczeństwo"), Security),
                (Loc.T("Bateria"), Battery),
                (Loc.T("Aktualizacje (hotfixy)"), HotFixes),
                (Loc.T("Autostart"), Startup),
                (".NET i aplikacja", Runtime),
                (Loc.T("Zmienne środowiskowe"), EnvironmentVariables),
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
                        items = [new(Loc.T("Błąd"), ex.Message)];
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
                    rows.Add(new PanelRow($"{sectionId}/{i}", sectionId, [key, value], key == Loc.T("Błąd") ? RowSeverity.Error : RowSeverity.Normal));
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
            return Task.FromResult<PanelDetails?>(new PanelDetails([new PanelDetailSection(item.Key, [new(Loc.T("Wartość"), item.Value)])]));
        }

        public Task<string?> ExecuteActionAsync(string actionId, IReadOnlyList<string> rowIds, CancellationToken cancellationToken)
        {
            var report = new StringBuilder();
            report.AppendLine(Loc.T("Commander4K — raport systemu {0}, {1:yyyy-MM-dd HH:mm}", Environment.MachineName, DateTime.Now));
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
            Add(items, Loc.T("Nazwa"), os["Caption"]);
            using var current = WinRegistry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            Add(items, Loc.T("Wersja"), $"{current?.GetValue("DisplayVersion")} (kompilacja {os["BuildNumber"]}.{current?.GetValue("UBR")})");
            Add(items, Loc.T("Architektura"), os["OSArchitecture"]);
            Add(items, Loc.T("Język / region"), $"{CultureInfo.InstalledUICulture.DisplayName} / {RegionInfo.CurrentRegion.DisplayName}");
            Add(items, Loc.T("Zainstalowano"), FormatDate(os["InstallDate"]));
            var boot = ManagementDateTimeConverter.ToDateTime((string)os["LastBootUpTime"]);
            Add(items, Loc.T("Ostatni start"), boot.ToString("g", CultureInfo.CurrentCulture));
            Add(items, Loc.T("Czas pracy"), FormatDuration(DateTime.Now - boot));
            Add(items, Loc.T("Zarejestrowany użytkownik"), os["RegisteredUser"]);
            Add(items, Loc.T("Katalog Windows"), os["WindowsDirectory"]);
            Add(items, Loc.T("Strefa czasowa"), TimeZoneInfo.Local.DisplayName);
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> Computer()
    {
        var items = new List<KeyValuePair<string, string>>
        {
            new(Loc.T("Nazwa komputera"), Environment.MachineName),
            new(Loc.T("Użytkownik"), $@"{Environment.UserDomainName}\{Environment.UserName}"),
        };
        foreach (var cs in Query("SELECT * FROM Win32_ComputerSystem"))
        {
            Add(items, Loc.T("Producent"), cs["Manufacturer"]);
            Add(items, "Model", cs["Model"]);
            Add(items, Loc.T("Typ systemu"), cs["SystemType"]);
            Add(items, (bool)(cs["PartOfDomain"] ?? false) ? Loc.T("Domena") : Loc.T("Grupa robocza"), cs["PartOfDomain"] is true ? cs["Domain"] : cs["Workgroup"]);
            Add(items, Loc.T("Hiperwizor obecny"), YesNo(cs["HypervisorPresent"]));
        }

        foreach (var bios in Query("SELECT * FROM Win32_BIOS"))
        {
            Add(items, "BIOS / UEFI", $"{bios["Manufacturer"]} {bios["SMBIOSBIOSVersion"]} ({FormatDate(bios["ReleaseDate"])})");
            Add(items, Loc.T("Numer seryjny"), bios["SerialNumber"]);
        }

        foreach (var board in Query("SELECT * FROM Win32_BaseBoard"))
        {
            Add(items, Loc.T("Płyta główna"), $"{board["Manufacturer"]} {board["Product"]}");
        }

        return items;
    }

    private static List<KeyValuePair<string, string>> Processor()
    {
        var items = new List<KeyValuePair<string, string>>();
        foreach (var cpu in Query("SELECT * FROM Win32_Processor"))
        {
            Add(items, "Model", (cpu["Name"] as string)?.Trim());
            Add(items, Loc.T("Rdzenie / wątki"), $"{cpu["NumberOfCores"]} / {cpu["NumberOfLogicalProcessors"]}");
            Add(items, Loc.T("Taktowanie maks."), $"{cpu["MaxClockSpeed"]} MHz");
            Add(items, Loc.T("Cache L2 / L3"), $"{cpu["L2CacheSize"]} KB / {cpu["L3CacheSize"]} KB");
            Add(items, Loc.T("Wirtualizacja w firmware"), YesNo(cpu["VirtualizationFirmwareEnabled"]));
        }

        Add(items, Loc.T("Architektura procesu"), RuntimeInformation.ProcessArchitecture);
        Add(items, Loc.T("Architektura systemu"), RuntimeInformation.OSArchitecture);
        Add(items, Loc.T("Akceleracja wektorowa"), $"128-bit: {YesNo(Vector128.IsHardwareAccelerated)}, 256-bit: {YesNo(Vector256.IsHardwareAccelerated)}, 512-bit: {YesNo(Vector512.IsHardwareAccelerated)}");
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            Add(items, Loc.T("Rozszerzenia ARM"), $"AdvSIMD: {YesNo(AdvSimd.IsSupported)}, AES: {YesNo(System.Runtime.Intrinsics.Arm.Aes.IsSupported)}, CRC32: {YesNo(Crc32.IsSupported)}, SHA256: {YesNo(System.Runtime.Intrinsics.Arm.Sha256.IsSupported)}, Dp: {YesNo(Dp.IsSupported)}");
        }
        else
        {
            Add(items, Loc.T("Rozszerzenia x86"), $"AVX2: {YesNo(Avx2.IsSupported)}, AVX-512F: {YesNo(Avx512F.IsSupported)}, AES-NI: {YesNo(System.Runtime.Intrinsics.X86.Aes.IsSupported)}, BMI2: {YesNo(Bmi2.IsSupported)}");
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
            Add(items, Loc.T("Dostępna dla systemu"), Bytes(total));
            Add(items, Loc.T("Wolna"), $"{Bytes(free)} ({100.0 * free / total:0}%)");
        }

        var slot = 0;
        foreach (var module in Query("SELECT * FROM Win32_PhysicalMemory"))
        {
            slot++;
            Add(items, Loc.T("Moduł {0} ({1})", slot, module["DeviceLocator"]),
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
            Add(items, disk["Model"] as string ?? Loc.T("Dysk"),
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
                    ? Loc.T("{0} [{1}] {2}: wolne {3} z {4}", drive.VolumeLabel, drive.DriveFormat, drive.DriveType, Bytes(drive.AvailableFreeSpace), Bytes(drive.TotalSize))
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
        Add(items, Loc.T("Uruchomiono jako administrator"), YesNo(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)));
        using (var secureBoot = WinRegistry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
        {
            Add(items, "Secure Boot", secureBoot?.GetValue("UEFISecureBootEnabled") is int enabled ? YesNo(enabled == 1) : "nieznany (BIOS legacy?)");
        }

        using (var uac = WinRegistry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
        {
            Add(items, Loc.T("Kontrola konta (UAC)"), YesNo(uac?.GetValue("EnableLUA") is 1));
        }

        try
        {
            foreach (var av in Query("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2"))
            {
                Add(items, Loc.T("Antywirus"), av["displayName"]);
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
            Add(items, battery["Name"] as string ?? Loc.T("Bateria"), Loc.T("naładowanie {0}%, pozostało ok. {1} min, status {2}", battery["EstimatedChargeRemaining"], battery["EstimatedRunTime"], battery["BatteryStatus"]));
        }

        if (items.Count == 0)
        {
            items.Add(new(Loc.T("Bateria"), "brak (komputer stacjonarny)"));
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
        new(Loc.T("Środowisko"), RuntimeInformation.RuntimeIdentifier),
        new(Loc.T("System (wg .NET)"), RuntimeInformation.OSDescription),
        new(Loc.T("Procesory logiczne (wg .NET)"), Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)),
        new(Loc.T("Katalog aplikacji"), AppContext.BaseDirectory),
        new(Loc.T("Tryb serwerowy GC"), YesNo(System.Runtime.GCSettings.IsServerGC)),
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

    private static string YesNo(object? value) => value is true ? Loc.T("tak") : value is false ? Loc.T("nie") : "?";

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
