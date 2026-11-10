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
    public async Task<PreviewCard> LoadCardAsync(string sftpPath, string? optionsJson, PreviewCard? previous, CancellationToken cancellationToken,
        Func<string, string>? resolvePath = null, string? user = null, string? keyFile = null)
    {
        CardAction[] actions = [new(Loc.T("Otwórz"), sftpPath), new(Loc.T("Odśwież"), CardActions.Refresh)];
        RemoteEndpoint endpoint;
        try
        {
            endpoint = RemoteEndpoint.Parse(sftpPath);
        }
        catch (FormatException ex)
        {
            return Card(CardStatus.Error, ex.Message, [], []);
        }

        var login = Login(optionsJson, user, keyFile, endpoint, resolvePath) ?? remote.LoginFor(sftpPath);
        if (login is null)
        {
            return Card(CardStatus.Error, Loc.T("Wymaga logowania."), [], actions);
        }

        var key = endpoint.SessionKey;
        try
        {
            var client = await ClientAsync(key, endpoint, login, cancellationToken).ConfigureAwait(false);
            using var command = client.CreateCommand(SshStatsParser.Command);
            command.CommandTimeout = TimeSpan.FromSeconds(10);
            await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            audit.Record(AuditId, "ssh.monitor", endpoint.Host);
            return Describe(SshStatsParser.Parse(command.Result), previous, actions);
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
            var info = new ConnectionInfo(endpoint.Host, endpoint.Port, login.UserName, [.. SftpSession.AuthenticationMethods(login)]) { Timeout = TimeSpan.FromSeconds(10) };
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
    internal PreviewCard Describe(SshStats stats, PreviewCard? previous, CardAction[] actions)
    {
        var items = new List<CardItem> { new CardValue(Loc.T("Status"), Loc.T("połączony"), CardStatus.Ok) };
        if (!stats.IsLinux)
        {
            items.Add(new CardValue(Loc.T("System"), stats.System.Length > 0 ? stats.System : "?"));
            return Card(CardStatus.Ok, null, items, actions);
        }

        var cpu = stats.CpuTicks is { } now && previous is not null && _readings.TryGetValue(previous, out var before)
            ? SshStatsParser.CpuPercent(before, now)
            : null;

        if (cpu is { } percent)
        {
            items.Add(new CardValue("CPU", $"{percent:0} %", percent >= 90 ? CardStatus.Warning : CardStatus.None));
            var chart = (previous?.Items.OfType<CardChart>().FirstOrDefault()?.Chart ?? new ChartData(ChartKind.Line, [], "%", 0, 100))
                .Append("CPU", new ChartPoint(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, Math.Round(percent, 1)));
            items.Add(new CardChart(chart));
        }
        else if (stats.LoadAverage.Length == 3)
        {
            items.Add(new CardValue(Loc.T("Obciążenie"), string.Join(" ", stats.LoadAverage.Select(l => l.ToString("0.00", CultureInfo.InvariantCulture)))));
        }

        if (stats.MemTotalKb > 0)
        {
            var used = stats.MemTotalKb - stats.MemAvailableKb;
            items.Add(new CardProgress(Loc.T("Pamięć"), (double)used / stats.MemTotalKb, $"{Gb(used)} / {Gb(stats.MemTotalKb)}"));
        }

        foreach (var (mount, size, used) in stats.Disks.Where(d => d.SizeKb > 0).OrderByDescending(d => d.SizeKb).Take(3))
        {
            items.Add(new CardProgress(mount, (double)used / size, $"{(double)used / size:P0} · {Gb(size)}"));
        }

        if (stats.Uptime > TimeSpan.Zero)
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
