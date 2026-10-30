using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Interop;
using Commander4K.Plugins.BuiltIn.SystemInfo;

namespace Commander4K.Plugins.BuiltIn.TaskManager;

/// <summary>
/// Task manager with sub-tasks: processes shown as a tree of parents and children, live CPU and memory, and per-process
/// details covering the executable (version, signature-relevant metadata), command line, resources (memory, handles,
/// GDI/USER objects, threads) and the loaded libraries (DLLs) with their versions.
/// </summary>
public sealed class TaskManagerPanelPlugin : IPanelPlugin
{
    public const string PluginId = "builtin.taskmanager";

    public string Id => PluginId;

    public string DisplayName => "Task manager";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.taskmanager", "Menedżer zadań", "Drzewo procesów, zasoby i biblioteki; zamykanie procesów.", PluginPermissions.ProcessRead, PluginPermissions.ProcessControl);

    public string Title => "Menedżer zadań";

    public string Description => "Drzewo procesów z podprocesami, CPU, pamięć, zasoby i powiązane biblioteki DLL.";

    public IPanelSession CreateSession(PanelRequest request, IPluginHost host) => new Session(host);

    private sealed class Session(IPluginHost host) : IPanelSession
    {
        private const int MaxModules = 400;
        private const int MaxThreads = 60;

        private static readonly ColumnDefinition[] Columns =
        [
            new("name", "Proces", ColumnAlignment.Left, 280),
            new("pid", "PID", ColumnAlignment.Right, 70),
            new("cpu", "CPU", ColumnAlignment.Right, 70),
            new("memory", "Pamięć", ColumnAlignment.Right, 100),
            new("threads", "Wątki", ColumnAlignment.Right, 60),
            new("handles", "Uchwyty", ColumnAlignment.Right, 70),
        ];

        // PID → (CPU time, sample time) from the previous refresh, for CPU %.
        private readonly Dictionary<int, (TimeSpan Cpu, long Timestamp)> _cpuSamples = [];

        public TimeSpan? RefreshInterval => TimeSpan.FromSeconds(2);

        public IReadOnlyList<PanelAction> Actions { get; } =
        [
            new("kill", "Zakończ proces", "Delete", RequiresConfirmation: true),
            new("killTree", "Zakończ z podprocesami", "Shift+Delete", RequiresConfirmation: true),
            new("goto", "Pokaż plik w panelu", "Ctrl+Enter"),
            new("modules", "Lista bibliotek (raport)", "F3"),
        ];

        public Task<PanelSnapshot> LoadAsync(PanelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            var parents = ProcessNative.GetParentProcessIds();
            var processes = Process.GetProcesses();
            var now = Stopwatch.GetTimestamp();
            var cores = Environment.ProcessorCount;
            var alive = new HashSet<int>(processes.Select(p => p.Id));
            var rows = new List<PanelRow>(processes.Length);
            long totalMemory = 0;
            double totalCpu = 0;

            foreach (var process in processes)
            {
                using (process)
                {
                    var pid = process.Id;
                    var cpuText = "-";
                    double cpu = -1;
                    try
                    {
                        var cpuTime = process.TotalProcessorTime;
                        if (_cpuSamples.TryGetValue(pid, out var previous))
                        {
                            var elapsed = Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalMilliseconds;
                            cpu = elapsed > 0 ? Math.Clamp((cpuTime - previous.Cpu).TotalMilliseconds / elapsed / cores * 100, 0, 100) : 0;
                            cpuText = cpu.ToString("0.0", CultureInfo.CurrentCulture) + "%";
                            totalCpu += cpu;
                        }

                        _cpuSamples[pid] = (cpuTime, now);
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Protected process: no CPU time without elevation.
                    }

                    var memory = process.WorkingSet64;
                    totalMemory += memory;
                    var parent = parents.TryGetValue(pid, out var p) && p != pid && alive.Contains(p) ? p.ToString(CultureInfo.InvariantCulture) : null;
                    var severity = cpu >= 50 ? RowSeverity.Warning : process.SessionId == 0 ? RowSeverity.Muted : RowSeverity.Normal;
                    rows.Add(new PanelRow(
                        pid.ToString(CultureInfo.InvariantCulture),
                        parent,
                        [process.ProcessName, pid.ToString(CultureInfo.InvariantCulture), cpuText, SystemInfoPanelPlugin.Bytes(memory),
                            process.Threads.Count.ToString(CultureInfo.CurrentCulture), process.HandleCount.ToString(CultureInfo.CurrentCulture)],
                        severity,
                        [null, pid, (long)(Math.Max(cpu, 0) * 1000), memory, process.Threads.Count, process.HandleCount]));
                }
            }

            foreach (var gone in _cpuSamples.Keys.Where(pid => !alive.Contains(pid)).ToList())
            {
                _cpuSamples.Remove(gone);
            }

            return Task.FromResult(new PanelSnapshot(Columns, rows,
                $"{rows.Count} procesów · CPU {totalCpu:0}% · pamięć robocza {SystemInfoPanelPlugin.Bytes(totalMemory)} · odświeżanie co 2 s"));
        }

        public Task<PanelDetails?> GetDetailsAsync(string rowId, CancellationToken cancellationToken) =>
            Task.Run<PanelDetails?>(() =>
            {
                var pid = int.Parse(rowId, CultureInfo.InvariantCulture);
                using var process = TryGetProcess(pid);
                if (process is null)
                {
                    return new PanelDetails([new PanelDetailSection("Proces", [new("Stan", "zakończony")])]);
                }

                var sections = new List<PanelDetailSection>();
                var path = ProcessNative.GetImagePath(pid);
                var file = new List<KeyValuePair<string, string>> { new("Ścieżka", path ?? "(brak dostępu)") };
                if (path is not null && File.Exists(path))
                {
                    var version = FileVersionInfo.GetVersionInfo(path);
                    var info = new FileInfo(path);
                    file.Add(new("Opis", version.FileDescription ?? string.Empty));
                    file.Add(new("Firma", version.CompanyName ?? string.Empty));
                    file.Add(new("Produkt", $"{version.ProductName} {version.ProductVersion}"));
                    file.Add(new("Wersja pliku", version.FileVersion ?? string.Empty));
                    file.Add(new("Rozmiar", SystemInfoPanelPlugin.Bytes(info.Length)));
                    file.Add(new("Zmodyfikowano", info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)));
                }

                file.Add(new("Linia poleceń", CommandLine(pid) ?? "(brak dostępu)"));
                sections.Add(new PanelDetailSection("Plik wykonywalny", file));

                var resources = new List<KeyValuePair<string, string>>
                {
                    new("Pamięć robocza", SystemInfoPanelPlugin.Bytes(process.WorkingSet64)),
                    new("Pamięć prywatna", SystemInfoPanelPlugin.Bytes(process.PrivateMemorySize64)),
                    new("Szczyt pamięci roboczej", SystemInfoPanelPlugin.Bytes(process.PeakWorkingSet64)),
                    new("Pamięć wirtualna", SystemInfoPanelPlugin.Bytes(process.VirtualMemorySize64)),
                    new("Uchwyty", process.HandleCount.ToString(CultureInfo.CurrentCulture)),
                    new("Wątki", process.Threads.Count.ToString(CultureInfo.CurrentCulture)),
                    new("Sesja", process.SessionId.ToString(CultureInfo.CurrentCulture)),
                };
                if (ProcessNative.GetGuiObjects(pid) is var (gdi, user))
                {
                    resources.Add(new("Obiekty GDI / USER", $"{gdi} / {user}"));
                }

                TryAdd(resources, "Uruchomiono", () => process.StartTime.ToString("g", CultureInfo.CurrentCulture));
                TryAdd(resources, "Czas procesora", () => process.TotalProcessorTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
                TryAdd(resources, "Priorytet", () => process.PriorityClass.ToString());
                sections.Add(new PanelDetailSection("Zasoby", resources));

                var threads = new List<KeyValuePair<string, string>>();
                foreach (ProcessThread thread in process.Threads)
                {
                    if (threads.Count >= MaxThreads)
                    {
                        threads.Add(new("…", $"i {process.Threads.Count - MaxThreads} kolejnych"));
                        break;
                    }

                    var state = thread.ThreadState == System.Diagnostics.ThreadState.Wait ? $"Wait ({SafeWaitReason(thread)})" : thread.ThreadState.ToString();
                    threads.Add(new(thread.Id.ToString(CultureInfo.InvariantCulture), $"{state}, priorytet {thread.CurrentPriority}"));
                }

                sections.Add(new PanelDetailSection($"Wątki ({process.Threads.Count})", threads));
                sections.Add(new PanelDetailSection("Biblioteki (DLL)", Modules(process)));
                return new PanelDetails(sections);
            }, cancellationToken);

        public async Task<string?> ExecuteActionAsync(string actionId, IReadOnlyList<string> rowIds, CancellationToken cancellationToken)
        {
            var pids = rowIds.Select(id => int.Parse(id, CultureInfo.InvariantCulture)).ToList();
            switch (actionId)
            {
                case "kill":
                case "killTree":
                    var failures = new List<string>();
                    foreach (var pid in pids)
                    {
                        try
                        {
                            // Through the host: checks process.kill and records the PID in the audit log.
                            await host.KillProcessAsync(pid, entireTree: actionId == "killTree", cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
                        {
                            failures.Add($"{pid}: {ex.Message}");
                        }
                    }

                    return failures.Count == 0 ? null : "Nie udało się zakończyć:\n" + string.Join('\n', failures);

                case "goto":
                    if (ProcessNative.GetImagePath(pids[0]) is { } path)
                    {
                        await host.NavigateAsync(Path.GetDirectoryName(path)!, Path.GetFileName(path)).ConfigureAwait(false);
                        return null;
                    }

                    return "Brak dostępu do ścieżki tego procesu.";

                case "modules":
                    using (var process = TryGetProcess(pids[0]))
                    {
                        if (process is null)
                        {
                            return "Proces już nie istnieje.";
                        }

                        var lines = Modules(process).Select(m => $"{m.Key,-40} {m.Value}");
                        return $"Biblioteki procesu {process.ProcessName} ({process.Id}):\n\n" + string.Join('\n', lines);
                    }

                default:
                    return null;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static List<KeyValuePair<string, string>> Modules(Process process)
        {
            var modules = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (modules.Count >= MaxModules)
                    {
                        modules.Add(new("…", "lista skrócona"));
                        break;
                    }

                    modules.Add(new(module.ModuleName, $"{module.FileVersionInfo.FileVersion} · {SystemInfoPanelPlugin.Bytes(module.ModuleMemorySize)} · {module.FileName}"));
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Access denied (elevated / protected process) or a process of another architecture.
                modules.Add(new("Brak dostępu", ex.Message));
            }

            return modules;
        }

        private static string? CommandLine(int pid)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
                using var results = searcher.Get();
                return results.Cast<ManagementBaseObject>().FirstOrDefault()?["CommandLine"] as string;
            }
            catch (ManagementException)
            {
                return null;
            }
        }

        private static string SafeWaitReason(ProcessThread thread)
        {
            try
            {
                return thread.WaitReason.ToString();
            }
            catch (InvalidOperationException)
            {
                return "?";
            }
        }

        private static void TryAdd(List<KeyValuePair<string, string>> items, string key, Func<string> value)
        {
            try
            {
                items.Add(new(key, value()));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                items.Add(new(key, "(brak dostępu)"));
            }
        }

        private static Process? TryGetProcess(int pid)
        {
            try
            {
                return Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
