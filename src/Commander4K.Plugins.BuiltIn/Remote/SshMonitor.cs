using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>
/// Status, CPU, memory and disks of an SSH server for a dashboard card. It signs in like the SFTP provider (saved
/// login, a key from the tile's options, or the default keys), checks the host key the same way, and runs only
/// <see cref="SshStatsParser.Command"/>. One session per server stays open while a dashboard refreshes it.
/// </summary>
public sealed class SshMonitor(RemoteTransferVfsPlugin remote, AuditLog audit) : IAsyncDisposable
{
    private const string AuditId = "builtin.remote";

    /// <summary>The card's own action (<c>c4k:plugin/connect</c>): the "Połącz" button of a tile with <c>autoConnect: false</c>.</summary>
    public const string ConnectAction = "connect";

    /// <summary>What <c>options.show</c> may list; a card without it shows <see cref="DefaultShow"/>, the monitor view all of it.</summary>
    public static IReadOnlyList<string> ShowKeys { get; } = ["status", "load", "cpu", "memory", "disks", "uptime"];

    public static IReadOnlyList<string> DefaultShow { get; } = ["load", "memory", "disks"];

    /// <summary>Servers whose "Połącz" was pressed: their tiles connect until the last dashboard showing them goes away.</summary>
    private readonly ConcurrentDictionary<string, byte> _connectAsked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Połącz" on a tile with <c>autoConnect: false</c>: the next reads of that server connect.</summary>
    public void Connect(string sftpPath)
    {
        if (TryKey(sftpPath) is { } key)
        {
            _connectAsked[key] = 0;
        }
    }

    /// <summary><c>options.show</c> (a list of <see cref="ShowKeys"/>) and <c>options.autoConnect</c> (only <c>false</c> switches it off).</summary>
    internal static (IReadOnlySet<string> Show, bool AutoConnect) CardOptions(string? optionsJson)
    {
        IEnumerable<string> show = DefaultShow;
        var autoConnect = true;
        if (optionsJson is not null)
        {
            using var options = JsonDocument.Parse(optionsJson);
            if (options.RootElement.TryGetProperty("show", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                show = [.. list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)];
            }

            autoConnect = !(options.RootElement.TryGetProperty("autoConnect", out var auto) && auto.ValueKind == JsonValueKind.False);
        }

        return (show.ToHashSet(StringComparer.OrdinalIgnoreCase), autoConnect);
    }

    /// <summary>Card button target that opens the SSH terminal on the <c>sftp://</c> path after it.</summary>
    public const string TerminalPrefix = "c4k:ssh/";

    private readonly ConcurrentDictionary<string, SshClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One connection attempt per server at a time, so two cards cannot open (and leak) two sessions.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connecting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dashboards showing each server; its session closes when the last one lets go.</summary>
    private readonly ConcurrentDictionary<string, int> _holders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The CPU counters behind each card returned, so a card measures against its own previous reading.</summary>
    private readonly ConditionalWeakTable<PreviewCard, long[]> _readings = new();

    /// <summary>A dashboard starts showing this server: its session stays open until the matching <see cref="ReleaseAsync"/>.</summary>
    public void Retain(string sftpPath)
    {
        if (TryKey(sftpPath) is { } key)
        {
            _holders.AddOrUpdate(key, 1, (_, count) => count + 1);
        }
    }

    internal int Holders(string sftpPath) => TryKey(sftpPath) is { } key && _holders.TryGetValue(key, out var count) ? count : 0;

    /// <param name="optionsJson"><c>{ "user": "…", "keyFile": "~/.ssh/id_ed25519" }</c> from the tile.</param>
    /// <param name="resolvePath">Resolves a relative <c>keyFile</c> against the tile's folder; without it only <c>~</c> is expanded.</param>
    /// <param name="user">The item's own <c>user</c>, used when the options name none.</param>
    /// <param name="keyFile">The item's own <c>keyFile</c>, used when the options name none.</param>
    /// <param name="everything">The monitor view: every reading, and it connects whatever the options say.</param>
    public async Task<PreviewCard> LoadCardAsync(string sftpPath, string? optionsJson, PreviewCard? previous, CancellationToken cancellationToken,
        Func<string, string>? resolvePath = null, string? user = null, string? keyFile = null, bool everything = false)
    {
        CardAction[] actions = [new("SSH", TerminalPrefix + sftpPath), new("SFTP", sftpPath), new(Loc.T("Odśwież"), CardActions.Refresh)];
        RemoteEndpoint endpoint;
        try
        {
            endpoint = RemoteEndpoint.Parse(sftpPath);
        }
        catch (FormatException ex)
        {
            return Card(CardStatus.Error, ex.Message, [], []);
        }

        var key = endpoint.SessionKey;
        var (show, autoConnect) = CardOptions(optionsJson);
        if (!everything && !autoConnect && !_connectAsked.ContainsKey(key))
        {
            return Card(CardStatus.None, Loc.T("Nie połączono."), [], [new(Loc.T("Połącz"), PreviewActionTargets.PluginPrefix + ConnectAction), .. actions[..2]]);
        }

        var login = Login(optionsJson, user, keyFile, endpoint, resolvePath) ?? remote.LoginFor(sftpPath);
        if (login is null)
        {
            return Card(CardStatus.Error, Loc.T("Wymaga logowania."), [], actions);
        }

        try
        {
            var client = await ClientAsync(key, endpoint, login, cancellationToken).ConfigureAwait(false);
            using var command = client.CreateCommand(SshStatsParser.Command);
            command.CommandTimeout = TimeSpan.FromSeconds(10);
            await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            audit.Record(AuditId, "ssh.monitor", endpoint.Host);
            return Describe(SshStatsParser.Parse(command.Result), previous, actions, everything ? null : show);
        }
        catch (SshAuthenticationException)
        {
            await DropAsync(key).ConfigureAwait(false);
            return Card(CardStatus.Error, Loc.T("Wymaga logowania."), [], actions);
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException or TimeoutException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            await DropAsync(key).ConfigureAwait(false);
            audit.Record(AuditId, "ssh.monitor", $"{endpoint.Host} → {ex.GetType().Name}: {ex.Message}", AuditOutcome.Failed);
            return Card(CardStatus.Error, Loc.T("Nieosiągalny: {0}", ex.Message), [], actions);
        }
    }

    /// <summary>A dashboard showing this server went away: the session closes once no other dashboard holds it.</summary>
    public async ValueTask ReleaseAsync(string sftpPath)
    {
        if (TryKey(sftpPath) is not { } key)
        {
            return;
        }

        var left = _holders.AddOrUpdate(key, 0, (_, count) => Math.Max(0, count - 1));
        if (left == 0)
        {
            _holders.TryRemove(new KeyValuePair<string, int>(key, 0));
            _connectAsked.TryRemove(key, out _);
            await DropAsync(key).ConfigureAwait(false);
        }
    }

    private static string? TryKey(string sftpPath)
    {
        try
        {
            return RemoteEndpoint.Parse(sftpPath).SessionKey;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _clients.Keys)
        {
            await DropAsync(key).ConfigureAwait(false);
        }
    }

    private async Task<SshClient> ClientAsync(string key, RemoteEndpoint endpoint, RemoteLogin login, CancellationToken cancellationToken)
    {
        if (_clients.TryGetValue(key, out var existing) && existing.IsConnected)
        {
            return existing;
        }

        var gate = _connecting.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another card may have connected while this one waited.
            if (_clients.TryGetValue(key, out existing) && existing.IsConnected)
            {
                return existing;
            }

            await DropAsync(key).ConfigureAwait(false);
            var info = new ConnectionInfo(endpoint.Host, endpoint.Port, login.UserName, [.. SftpSession.AuthenticationMethods(login, endpoint.Host)]) { Timeout = TimeSpan.FromSeconds(10) };
            var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            client.HostKeyReceived += (_, e) => e.CanTrust = remote.HostKeyValidator(endpoint.Host, endpoint.Port, e.FingerPrintSHA256);
            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            audit.Record(AuditId, "ssh.connect", $"{login.UserName}@{endpoint.Host}:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}");
            _clients[key] = client;
            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    private Task DropAsync(string key)
    {
        if (_clients.TryRemove(key, out var client))
        {
            // Disconnecting waits for the server; it must not hold up the dashboard that is closing.
            return Task.Run(() =>
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException)
                {
                }
            });
        }

        return Task.CompletedTask;
    }

    private static RemoteLogin? Login(string? optionsJson, string? itemUser, string? itemKeyFile, RemoteEndpoint endpoint, Func<string, string>? resolvePath)
    {
        string? optionUser = null;
        string? optionKey = null;
        if (optionsJson is not null)
        {
            using var options = JsonDocument.Parse(optionsJson);
            string? Text(string name) => options.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            optionUser = Text("user");
            optionKey = Text("keyFile");
        }

        var keyFile = optionKey ?? itemKeyFile;
        var named = optionUser ?? itemUser;
        var user = named ?? endpoint.UserName;
        if (keyFile is null && named is null)
        {
            return null;
        }

        if (keyFile is not null && resolvePath is not null)
        {
            keyFile = resolvePath(keyFile);
        }
        else if (keyFile is not null && keyFile.StartsWith('~'))
        {
            keyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), keyFile.TrimStart('~').TrimStart('/', '\\'));
        }

        return new RemoteLogin(user ?? Environment.UserName, PrivateKeyFile: keyFile);
    }

    /// <param name="previous">This card's last result: CPU use is the difference from the counters read for it.</param>
    /// <param name="show">The readings to draw (<see cref="ShowKeys"/>); <c>null</c> = all, with the load only while there is no CPU reading yet.</param>
    internal PreviewCard Describe(SshStats stats, PreviewCard? previous, CardAction[] actions, IReadOnlySet<string>? show = null)
    {
        bool Shows(string reading) => show is null || show.Contains(reading);
        var items = new List<CardItem>();
        if (Shows("status") || !stats.IsLinux)
        {
            items.Add(new CardValue(Loc.T("Status"), Loc.T("połączony"), CardStatus.Ok));
        }

        if (!stats.IsLinux)
        {
            items.Add(new CardValue(Loc.T("System"), stats.System.Length > 0 ? stats.System : "?"));
            return Card(CardStatus.Ok, null, items, actions);
        }

        var cpu = stats.CpuTicks is { } now && previous is not null && _readings.TryGetValue(previous, out var before)
            ? SshStatsParser.CpuPercent(before, now)
            : null;

        if (stats.LoadAverage.Length == 3 && (show is null ? cpu is null : show.Contains("load")))
        {
            items.Add(new CardValue(Loc.T("Obciążenie"), string.Join(" ", stats.LoadAverage.Select(l => l.ToString("0.00", CultureInfo.InvariantCulture)))));
        }

        if (cpu is { } percent && Shows("cpu"))
        {
            items.Add(new CardValue("CPU", $"{percent:0} %", percent >= 90 ? CardStatus.Warning : CardStatus.None));
            var chart = (previous?.Items.OfType<CardChart>().FirstOrDefault()?.Chart ?? new ChartData(ChartKind.Line, [], "%", 0, 100))
                .Append("CPU", new ChartPoint(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, Math.Round(percent, 1)));
            items.Add(new CardChart(chart));
        }

        if (stats.MemTotalKb > 0 && Shows("memory"))
        {
            var used = stats.MemTotalKb - stats.MemAvailableKb;
            items.Add(new CardProgress(Loc.T("Pamięć"), (double)used / stats.MemTotalKb, $"{Gb(used)} / {Gb(stats.MemTotalKb)}"));
        }

        foreach (var (mount, size, used) in stats.Disks.Where(d => d.SizeKb > 0 && Shows("disks")).OrderByDescending(d => d.SizeKb).Take(3))
        {
            items.Add(new CardProgress(mount, (double)used / size, $"{(double)used / size:P0} · {Gb(size)}"));
        }

        if (stats.Uptime > TimeSpan.Zero && Shows("uptime"))
        {
            items.Add(new CardValue(Loc.T("Czas pracy"), stats.Uptime.TotalDays >= 1
                ? Loc.T("{0} d {1} h", (int)stats.Uptime.TotalDays, stats.Uptime.Hours)
                : Loc.T("{0} h {1} min", stats.Uptime.Hours, stats.Uptime.Minutes)));
        }

        var worst = items.OfType<CardProgress>().Any(p => p.Fraction >= 0.9) || cpu >= 90 ? CardStatus.Warning : CardStatus.Ok;
        var card = Card(worst, null, items, actions);
        if (stats.CpuTicks is { } ticks)
        {
            _readings.AddOrUpdate(card, ticks);
        }

        return card;
    }

    private static string Gb(long kilobytes) => (kilobytes / 1024.0 / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " GB";

    private static PreviewCard Card(CardStatus status, string? subtitle, IReadOnlyList<CardItem> items, IReadOnlyList<CardAction> actions) =>
        new(null, subtitle, "🖥", status, items, actions);
}
