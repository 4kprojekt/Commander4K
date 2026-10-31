using System.Text.Json.Nodes;
using Commander4K.Core.Configuration;
using Commander4K.Core.Mcp;
using Commander4K.Core.Plugins;

namespace Commander4K.Core.Tests;

public sealed class McpPolicyTests
{
    private const string Client = "claude-code";
    private const string Declined = "The user declined this action in Commander4K.";

    [Theory]
    [InlineData(@"C:\Repo\src\a.cs", @"C:\Repo", true)]
    [InlineData(@"c:\repo\SRC", @"C:\Repo\", true)]
    [InlineData(@"C:\Repos2\a", @"C:\Repo", false)]
    [InlineData(@"C:\Repo\..\Windows\x", @"C:\Repo", false)]
    [InlineData(@"C:\Repo", @"C:\Repo", true)]
    [InlineData(@"\\SRV\Share\x", @"\\srv\share", true)]
    [InlineData(@"\\srv\share2", @"\\srv\share", false)]
    [InlineData("sftp://h/home/a/b", "sftp://h/home/a", true)]
    [InlineData("sftp://h/home/ab", "sftp://h/home/a", false)]
    [InlineData("sftp://h/home/A", "sftp://h/home/a", false)]
    [InlineData(@"zip://C:\Repo\x.zip::/inner/f", @"C:\Repo", true)]
    [InlineData(@"reg://HKCU\Software\X", @"reg://HKCU\Software", true)]
    public void IsInside_handles_boundaries(string path, string root, bool expected) =>
        Assert.Equal(expected, McpPathPolicy.IsInside(path, root));

    [Theory]
    [InlineData(@"C:\Repo\a\..\..\..\..\Windows", @"C:\Repo", false)]
    [InlineData(@"C:/Repo/./src/../src/a.cs", @"C:\Repo\src", true)]
    [InlineData(@"\\?\C:\Repo\a", @"C:\Repo", true)]
    [InlineData(@"\\?\UNC\srv\share\a", @"\\srv\share", true)]
    [InlineData(@"\\srv\share\..\other\x", @"\\srv\share", true)] // ".." stops at the share: \\srv\share\other\x
    [InlineData(@"\\srv\share\..\..\other\x", @"\\srv\other", false)]
    [InlineData("sftp://h/home/a/../../etc/passwd", "sftp://h/home/a", false)]
    [InlineData("sftp://h/../../home/a/x", "sftp://h/home/a", true)]
    [InlineData("sftp://other/home/a/x", "sftp://h/home/a", false)]
    [InlineData(@"sftp://h/home/a/..\..\etc\x", "sftp://h/home/a", false)]
    [InlineData(@"ftp://h/home/a/..\..\etc\x", "ftp://h/home/a", false)]
    [InlineData(@"sftp://h/home/a\b", "sftp://h/home/a", true)]
    [InlineData(@"sftp://h\home\a\x", "sftp://h/home/a", true)]
    [InlineData("ftp://h/home/a/x", "sftp://h/home/a", false)]
    [InlineData(@"zip://C:\Other\x.zip::/inner/f", @"C:\Repo", false)]
    [InlineData(@"zip://C:\Repo\..\x.zip::/inner/f", @"C:\Repo", false)]
    [InlineData(@"zip://C:\Repo\x.zip::/a/../../../f", @"zip://C:\Repo\x.zip::/a", false)]
    [InlineData(@"zip://C:\Repo\x.zip::/a/b", @"zip://C:\Repo\x.zip::/a", true)]
    [InlineData(@"reg://HKCU\Software\X\\Value", @"reg://hkcu\software\x", true)]
    [InlineData(@"reg://HKCU\Software\..\..\HKLM\Software", @"reg://HKCU\Software", false)]
    [InlineData(@"reg://HKCU\SoftwareX", @"reg://HKCU\Software", false)]
    [InlineData("relative\\a.cs", @"C:\Repo", false)]
    [InlineData(@"C:Repo\a.cs", @"C:\Repo", false)]
    [InlineData(@"\\.\C:\Repo\a", @"C:\Repo", false)]
    [InlineData(@"D:\Repo\a", @"C:\Repo", false)]
    [InlineData(@"C:\Repo\a", @"C:\", true)]
    public void IsInside_resists_escapes(string path, string root, bool expected) =>
        Assert.Equal(expected, McpPathPolicy.IsInside(path, root));

    [Theory]
    [InlineData(@"C:\Repo\", @"C:\Repo")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:/Repo//a/./b/../c", @"C:\Repo\a\c")]
    [InlineData(@"\\srv\share\", @"\\srv\share")]
    [InlineData("sftp://me@h:2222/home/a/", "sftp://me@h:2222/home/a")]
    [InlineData("sftp://h", "sftp://h/")]
    [InlineData("SFTP://h/a/#1?/b%20", "sftp://h/a/#1?/b%20")]
    [InlineData(@"zip://C:\Repo\..\x.zip::/a//b/", @"zip://C:\x.zip::/a/b")]
    [InlineData(@"reg://HKCU\Software\.\X\\Va\lue", @"reg://HKCU\Software\X\\Va\lue")]
    public void Normalize_resolves_segments(string path, string expected) =>
        Assert.Equal(expected, McpPathPolicy.Normalize(path));

    [Theory]
    [InlineData(@"C:\Repo\config::$INDEX_ALLOCATION\ai\credentials.json")]
    [InlineData(@"C:\Repo\plugins::$INDEX_ALLOCATION\my-plugin\main.py")]
    [InlineData(@"C:\Repo\config:$I30:$INDEX_ALLOCATION\settings.json")]
    [InlineData(@"C:\Repo\notes.txt:secret")]
    [InlineData(@"C:\Repo\notes.txt::$DATA")]
    [InlineData(@"C:/Repo/src::$INDEX_ALLOCATION/a.cs")]
    [InlineData(@"\\?\C:\Repo\config::$INDEX_ALLOCATION\x")]
    [InlineData(@"\\srv\share\config::$INDEX_ALLOCATION\x")]
    [InlineData(@"zip://C:\Repo\x.zip:hidden::/inner/f")]
    public void Stream_syntax_paths_are_never_inside_anything(string path)
    {
        Assert.False(McpPathPolicy.IsInside(path, @"C:\Repo"));
        Assert.False(McpPathPolicy.IsInside(path, @"\\srv\share"));
        Assert.False(McpPathPolicy.IsAllowed(path, [@"C:\", @"C:\Repo", @"\\srv\share"]));
        Assert.NotNull(McpToolDispatcher.Refusal(path, [@"C:\Repo", @"\\srv\share"], [@"C:\Repo\config", @"C:\Repo\plugins"]));
    }

    [Fact]
    public void Drive_root_colon_is_still_allowed()
    {
        Assert.True(McpPathPolicy.IsInside(@"C:\Repo\a.cs", @"C:\"));
        Assert.True(McpPathPolicy.IsInside(@"zip://C:\Repo\x.zip::/a:b", @"C:\Repo")); // ':' inside an archive entry is just a name
    }

    [Fact]
    public async Task Stream_syntax_is_refused_for_every_tool_including_names_only()
    {
        var f = new Fixture();
        f.Protected.Add(@"C:\Repo\config");
        var reader = f.Add("read_file", McpToolMode.Allow);
        var lister = f.Add("list_directory", McpToolMode.Allow, namesOnly: true);

        var read = await f.CallAsync("read_file", Args(@"C:\Repo\config::$INDEX_ALLOCATION\ai\credentials.json"));
        var plain = await f.CallAsync("read_file", Args(@"C:\Repo\notes.txt:stream"));
        var list = await f.CallAsync("list_directory", Args(@"C:\Repo\config::$INDEX_ALLOCATION"));

        Assert.True(read.IsError);
        Assert.True(plain.IsError);
        Assert.True(list.IsError);
        Assert.Equal(0, reader.Runs);
        Assert.Equal(0, lister.Runs);
    }

    [Fact]
    public void Empty_roots_allow_nothing()
    {
        Assert.False(McpPathPolicy.IsAllowed(@"C:\Repo\a.cs", []));
        Assert.True(McpPathPolicy.IsAllowed(@"C:\Repo\a.cs", [@"D:\x", @"C:\Repo"]));
    }

    [Fact]
    public async Task Blocked_tool_never_runs_and_is_audited_as_denied()
    {
        var f = new Fixture();
        f.Settings.ToolModes["read_file"] = "block";
        var tool = f.Add("read_file", McpToolMode.Allow);

        var result = await f.CallAsync("read_file", Args(@"C:\Repo\a.cs"));

        Assert.True(result.IsError);
        Assert.Equal("This tool is disabled in Commander4K settings (MCP).", result.Text);
        Assert.Equal(0, tool.Runs);
        var entry = Assert.Single(f.Audit.Recent("mcp:" + Client));
        Assert.Equal("read_file", entry.Operation);
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(f.Dispatcher.Recent).Outcome);
        Assert.EndsWith(" (Disabled by the user in Commander4K.)", f.Dispatcher.List().Single(t => t.Name == "read_file").Description);
    }

    [Fact]
    public async Task Path_outside_roots_is_blocked_even_in_allow_mode()
    {
        var f = new Fixture();
        var tool = f.Add("read_file", McpToolMode.Allow);

        var result = await f.CallAsync("read_file", Args(@"C:\Windows\win.ini"));

        Assert.True(result.IsError);
        Assert.Equal(
            @"Path is outside the folders allowed for MCP: C:\Windows\win.ini. Ask the user to add a folder in Commander4K (MCP settings → allowed folders).",
            result.Text);
        Assert.Equal(0, tool.Runs);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(f.Audit.Recent("mcp:" + Client)).Outcome);

        // Every path in an array argument is checked.
        var many = new JsonObject { ["paths"] = new JsonArray(@"C:\Repo\a", @"C:\Other\b") };
        var arrayTool = f.Add("copy", McpToolMode.Allow, pathArguments: ["paths"]);
        Assert.True((await f.CallAsync("copy", many)).IsError);
        Assert.Equal(0, arrayTool.Runs);
    }

    [Fact]
    public async Task Allowed_call_runs_with_normalized_paths_and_is_audited()
    {
        var f = new Fixture();
        var tool = f.Add("read_file", McpToolMode.Allow);

        var result = await f.CallAsync("read_file", Args(@"C:/Repo/./src/a.cs"));

        Assert.False(result.IsError);
        Assert.Equal(1, tool.Runs);
        Assert.Equal(@"C:\Repo\src\a.cs", tool.LastPath);
        var entry = Assert.Single(f.Audit.Recent("mcp:" + Client));
        Assert.Equal(AuditOutcome.Allowed, entry.Outcome);
        Assert.Contains(@"C:\Repo\src\a.cs", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failing_handler_is_audited_as_failed()
    {
        var f = new Fixture();
        f.Add("bad", McpToolMode.Allow, handler: (_, _) => Task.FromResult(McpToolResult.Error("nope")));
        f.Add("throws", McpToolMode.Allow, handler: (_, _) => throw new IOException("disk"));

        Assert.True((await f.CallAsync("bad", Args(@"C:\Repo\a"))).IsError);
        await Assert.ThrowsAsync<IOException>(() => f.CallAsync("throws", Args(@"C:\Repo\a")));

        var entries = f.Audit.Recent("mcp:" + Client);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(AuditOutcome.Failed, e.Outcome));
        Assert.Equal(2, f.Dispatcher.Recent.Count);
    }

    [Fact]
    public async Task Write_into_protected_root_is_blocked()
    {
        var f = new Fixture();
        f.Protected.Add(@"C:\Repo\plugins");
        var writer = f.Add("write_file", McpToolMode.Allow, writes: true);
        var reader = f.Add("read_file", McpToolMode.Allow);

        var result = await f.CallAsync("write_file", Args(@"C:\Repo\Plugins\x\plugin.json"));
        var parent = await f.CallAsync("write_file", Args(@"C:\Repo"));
        var readParent = await f.CallAsync("read_file", Args(@"C:\Repo\notes.txt"));

        Assert.True(result.IsError);
        Assert.Equal(McpToolDispatcher.ProtectedMessage, result.Text);
        Assert.Equal("Commander4K's own plugin, config and data folders cannot be accessed through MCP. Use install_plugin to install plugins.", result.Text);
        Assert.True(parent.IsError);
        Assert.Equal(0, writer.Runs);
        Assert.False(readParent.IsError);
        Assert.Equal(1, reader.Runs);
    }

    [Fact]
    public async Task Reads_into_protected_root_are_refused_but_names_only_tools_work()
    {
        var f = new Fixture();
        f.Protected.Add(@"C:\Repo\config");
        var reader = f.Add("read_file", McpToolMode.Allow);
        var navigate = f.Add("navigate", McpToolMode.Allow);
        var lister = f.Add("list_directory", McpToolMode.Allow, namesOnly: true);
        var stat = f.Add("stat", McpToolMode.Allow, namesOnly: true);

        var read = await f.CallAsync("read_file", Args(@"C:\Repo\Config\ai\credentials.json"));
        var into = await f.CallAsync("navigate", Args(@"C:\Repo\config"));
        var above = await f.CallAsync("navigate", Args(@"C:\Repo"));

        Assert.True(read.IsError);
        Assert.Equal(McpToolDispatcher.ProtectedMessage, read.Text);
        Assert.True(into.IsError);
        Assert.False(above.IsError); // Reading tools may open a folder that merely contains a protected one.
        Assert.Equal(0, reader.Runs);
        Assert.Equal(1, navigate.Runs);
        Assert.False((await f.CallAsync("list_directory", Args(@"C:\Repo\config"))).IsError);
        Assert.False((await f.CallAsync("stat", Args(@"C:\Repo\config\settings.json"))).IsError);
        Assert.Equal(1, lister.Runs);
        Assert.Equal(1, stat.Runs);
        Assert.Contains(f.Audit.Recent("mcp:" + Client), e => e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Recursive_folder_tools_refuse_a_folder_that_contains_a_protected_root()
    {
        var f = new Fixture();
        f.Protected.Add(@"C:\Repo\data");
        var check = f.Add("check_plugin", McpToolMode.Allow, readsTree: true, pathArguments: ["folder"]);

        Assert.True((await f.CallAsync("check_plugin", new JsonObject { ["folder"] = @"C:\Repo" })).IsError);
        Assert.True((await f.CallAsync("check_plugin", new JsonObject { ["folder"] = @"C:\Repo\data\plugin-data" })).IsError);
        Assert.False((await f.CallAsync("check_plugin", new JsonObject { ["folder"] = @"C:\Repo\src\my-plugin" })).IsError);
        Assert.Equal(1, check.Runs);
    }

    [Fact]
    public void Refusal_helper_covers_roots_and_protected_folders()
    {
        string[] roots = [@"C:\Repo"];
        string[] protectedRoots = [@"C:\Repo\plugins"];

        Assert.Null(McpToolDispatcher.Refusal(@"C:\Repo\src", roots, protectedRoots));
        Assert.Null(McpToolDispatcher.Refusal(@"C:\Repo", roots, protectedRoots));
        Assert.Equal(McpToolDispatcher.ProtectedMessage, McpToolDispatcher.Refusal(@"C:\Repo\Plugins\x", roots, protectedRoots));
        Assert.Equal(McpToolDispatcher.OutsideRootsMessage(@"D:\x"), McpToolDispatcher.Refusal(@"D:\x", roots, protectedRoots));
        Assert.True(McpPathPolicy.IsProtected(@"C:\Repo", protectedRoots, orAbove: true));
        Assert.False(McpPathPolicy.IsProtected(@"C:\Repo", protectedRoots));
    }

    [Fact]
    public async Task Ask_mode_calls_confirm_and_No_returns_declined()
    {
        var f = new Fixture { Answer = McpConsent.No };
        var tool = f.Add("delete", McpToolMode.Ask, summary: a => "Delete " + a["path"]);

        var result = await f.CallAsync("delete", Args(@"C:\Repo\a"));

        Assert.True(result.IsError);
        Assert.Equal(Declined, result.Text);
        Assert.Equal(0, tool.Runs);
        var asked = Assert.Single(f.Asked);
        Assert.Equal(new McpConfirmation(Client, "delete", @"Delete C:\Repo\a", false), asked);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(f.Audit.Recent("mcp:" + Client)).Outcome);
    }

    [Fact]
    public async Task Confirmation_details_reach_the_dialog_but_not_the_audit()
    {
        var f = new Fixture();
        f.Add("write_file", McpToolMode.Ask, writes: true, details: a => "content: " + a["content"]);
        f.Add("broken", McpToolMode.Ask, details: _ => throw new InvalidOperationException("boom"));

        var args = Args(@"C:\Repo\a.txt");
        args["content"] = "SECRET-BODY";
        Assert.False((await f.CallAsync("write_file", args)).IsError);
        Assert.False((await f.CallAsync("broken", Args(@"C:\Repo\b.txt"))).IsError);

        Assert.Equal("content: SECRET-BODY", f.Asked[0].Details);
        Assert.Null(f.Asked[1].Details); // A failing preview is left out; the call is still asked about.
        Assert.DoesNotContain(f.Audit.Recent("mcp:" + Client), e => e.ToString().Contains("SECRET-BODY", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Dispatcher.Recent, r => r.Summary.Contains("SECRET-BODY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task YesForSession_skips_the_next_confirmation_for_that_client_only()
    {
        var f = new Fixture { Answer = McpConsent.YesForSession };
        var tool = f.Add("delete", McpToolMode.Ask);

        await f.CallAsync("delete", Args(@"C:\Repo\a"));
        await f.CallAsync("delete", Args(@"C:\Repo\b"));
        Assert.Single(f.Asked);

        f.Answer = McpConsent.Yes;
        await f.CallAsync("delete", Args(@"C:\Repo\c"), client: "other");
        await f.CallAsync("delete", Args(@"C:\Repo\d"), client: "other");
        Assert.Equal(3, f.Asked.Count);
        Assert.Equal(4, tool.Runs);
    }

    [Fact]
    public async Task AlwaysAsk_tool_set_to_allow_still_asks_and_never_gets_session_grant()
    {
        var f = new Fixture { Answer = McpConsent.YesForSession };
        f.Settings.ToolModes["RUN_COMMAND"] = "allow";
        var tool = f.Add("run_command", McpToolMode.Ask, alwaysAsk: true);

        Assert.Equal(McpToolMode.Ask, f.Dispatcher.EffectiveMode(tool.Definition));
        await f.CallAsync("run_command", Args(@"C:\Repo"));
        await f.CallAsync("run_command", Args(@"C:\Repo"));

        Assert.Equal(2, f.Asked.Count);
        Assert.All(f.Asked, a => Assert.True(a.AlwaysAsk));
        Assert.Equal(2, tool.Runs);
    }

    [Fact]
    public void EffectiveMode_reads_settings_case_insensitively()
    {
        var f = new Fixture();
        var tool = f.Add("read_file", McpToolMode.Allow);
        Assert.Equal(McpToolMode.Allow, f.Dispatcher.EffectiveMode(tool.Definition));
        f.Settings.ToolModes["Read_File"] = "Ask";
        Assert.Equal(McpToolMode.Ask, f.Dispatcher.EffectiveMode(tool.Definition));
        f.Settings.ToolModes["read_file"] = "nonsense";
        Assert.Equal(McpToolMode.Ask, f.Dispatcher.EffectiveMode(tool.Definition));

        var blocked = f.Add("dangerous", McpToolMode.Block);
        f.Settings.ToolModes["dangerous"] = "alow";
        Assert.Equal(McpToolMode.Block, f.Dispatcher.EffectiveMode(blocked.Definition));
    }

    [Fact]
    public async Task Unknown_tool_is_audited_as_denied()
    {
        var f = new Fixture();

        var result = await f.CallAsync("nope", Args(@"C:\Repo\a"));

        Assert.True(result.IsError);
        var entry = Assert.Single(f.Audit.Recent("mcp:" + Client));
        Assert.Equal(("nope", "unknown tool", AuditOutcome.Denied), (entry.Operation, entry.Detail, entry.Outcome));
    }

    [Fact]
    public async Task Throwing_confirm_is_audited_as_failed_and_surfaces()
    {
        var f = new Fixture { Confirm = (_, _) => throw new InvalidOperationException("dialog broke") };
        var tool = f.Add("delete", McpToolMode.Ask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.CallAsync("delete", Args(@"C:\Repo\a")));

        Assert.Equal(0, tool.Runs);
        Assert.Equal(AuditOutcome.Failed, Assert.Single(f.Audit.Recent("mcp:" + Client)).Outcome);

        // The confirmation slot was released.
        f.Confirm = (_, _) => Task.FromResult(McpConsent.Yes);
        Assert.False((await f.CallAsync("delete", Args(@"C:\Repo\a"))).IsError);
    }

    [Fact]
    public async Task Absent_optional_path_runs_but_explicit_null_is_refused()
    {
        var f = new Fixture();
        var tool = f.Add("check_plugin", McpToolMode.Allow, pathArguments: ["files", "folder"]);

        Assert.False((await f.CallAsync("check_plugin", new JsonObject { ["folder"] = @"C:\Repo\p" })).IsError);
        Assert.Equal(1, tool.Runs);

        var result = await f.CallAsync("check_plugin", new JsonObject { ["folder"] = null });
        Assert.True(result.IsError);
        Assert.Equal("Argument 'folder' must be a path.", result.Text);
        Assert.True((await f.CallAsync("check_plugin", new JsonObject { ["files"] = new JsonArray(@"C:\Repo\a", null) })).IsError);
        Assert.Equal(1, tool.Runs);
    }

    [Fact]
    public async Task Throwing_summary_falls_back_and_is_still_audited()
    {
        var f = new Fixture();
        f.Add("read_file", McpToolMode.Allow, summary: _ => throw new FormatException("bad"));
        f.Add("blocked", McpToolMode.Block, summary: _ => throw new FormatException("bad"));

        Assert.False((await f.CallAsync("read_file", Args("C:/Repo/a"))).IsError);
        Assert.True((await f.CallAsync("read_file", Args(@"C:\Other\a"))).IsError);
        Assert.True((await f.CallAsync("blocked", Args(@"C:\Raw\x"))).IsError);

        var entries = f.Audit.Recent("mcp:" + Client);
        Assert.Equal(3, entries.Count);
        Assert.Equal(@"read_file C:\Repo\a", entries[0].Detail);
        Assert.Equal(@"read_file C:\Other\a", entries[1].Detail);
        Assert.Equal(@"blocked C:\Raw\x", entries[2].Detail);
    }

    [Fact]
    public async Task Throwing_Called_subscriber_does_not_fail_the_call()
    {
        var f = new Fixture();
        var tool = f.Add("read_file", McpToolMode.Allow);
        f.Dispatcher.Called += () => throw new InvalidOperationException("ui");

        Assert.False((await f.CallAsync("read_file", Args(@"C:\Repo\a"))).IsError);
        Assert.Equal(1, tool.Runs);
    }

    [Fact]
    public async Task Concurrent_asks_are_serialized()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = 0;
        var max = 0;
        var entered = 0;
        var f = new Fixture
        {
            Confirm = async (_, _) =>
            {
                Interlocked.Increment(ref entered);
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref max, now);
                await release.Task;
                Interlocked.Decrement(ref current);
                return McpConsent.Yes;
            },
        };
        var tool = f.Add("delete", McpToolMode.Ask);

        // Everything before the confirmation is synchronous, so without serialization both calls would be inside
        // the confirmer by the time CallAsync returns.
        var first = f.CallAsync("delete", Args(@"C:\Repo\a"));
        var second = f.CallAsync("delete", Args(@"C:\Repo\b"));
        Assert.Equal(1, Volatile.Read(ref entered));
        Assert.False(second.IsCompleted);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, entered);
        Assert.Equal(1, max);
        Assert.Equal(2, tool.Runs);
    }

    [Fact]
    public async Task Cancellation_while_confirming_returns_declined()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = new Fixture
        {
            Confirm = async (_, ct) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return McpConsent.Yes;
            },
        };
        var tool = f.Add("delete", McpToolMode.Ask);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var call = f.Dispatcher.CallAsync("delete", new McpCall(Client, Args(@"C:\Repo\a")), cts.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        var result = await call;

        Assert.True(result.IsError);
        Assert.Equal(Declined, result.Text);
        Assert.Equal(0, tool.Runs);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(f.Audit.Recent("mcp:" + Client)).Outcome);

        // The confirmation slot is free again.
        f.Confirm = (_, _) => Task.FromResult(McpConsent.Yes);
        Assert.False((await f.CallAsync("delete", Args(@"C:\Repo\a"))).IsError);
    }

    [Fact]
    public async Task RevokeSessionGrants_asks_again()
    {
        var f = new Fixture { Answer = McpConsent.YesForSession };
        f.Add("delete", McpToolMode.Ask);

        await f.CallAsync("delete", Args(@"C:\Repo\a"));
        await f.CallAsync("delete", Args(@"C:\Repo\a"));
        Assert.Single(f.Asked);

        f.Dispatcher.RevokeSessionGrants();
        await f.CallAsync("delete", Args(@"C:\Repo\a"));
        Assert.Equal(2, f.Asked.Count);
    }

    [Fact]
    public async Task SelfConfirming_tool_skips_confirm_but_not_roots_or_block()
    {
        var f = new Fixture();
        f.Protected.Add(@"C:\Repo\plugins");
        var tool = f.Add("install_plugin", McpToolMode.Ask, writes: true, selfConfirming: true);

        Assert.False((await f.CallAsync("install_plugin", Args(@"C:\Repo\src"))).IsError);
        Assert.Empty(f.Asked);
        Assert.Equal(1, tool.Runs);

        Assert.True((await f.CallAsync("install_plugin", Args(@"C:\Elsewhere"))).IsError);
        Assert.True((await f.CallAsync("install_plugin", Args(@"C:\Repo\plugins\x"))).IsError);
        f.Settings.ToolModes["install_plugin"] = "block";
        Assert.True((await f.CallAsync("install_plugin", Args(@"C:\Repo\src"))).IsError);
        Assert.Equal(1, tool.Runs);
        Assert.Empty(f.Asked);
    }

    [Fact]
    public async Task Called_event_and_recent_keep_the_last_fifty()
    {
        var f = new Fixture();
        f.Add("read_file", McpToolMode.Allow);
        var raised = 0;
        f.Dispatcher.Called += () => raised++;

        for (var i = 0; i < 55; i++)
        {
            await f.CallAsync("read_file", Args($@"C:\Repo\{i}"));
        }

        Assert.Equal(55, raised);
        Assert.Equal(50, f.Dispatcher.Recent.Count);
        Assert.Contains(@"C:\Repo\54", f.Dispatcher.Recent[^1].Summary, StringComparison.Ordinal);
        Assert.Equal(Client, f.Dispatcher.Recent[0].Client);
    }

    [Fact]
    public async Task Settings_without_mcp_keys_get_case_insensitive_defaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "c4k-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var store = new ConfigStore(directory);
            var path = Path.Combine(directory, ConfigStore.SettingsFileName);

            await File.WriteAllTextAsync(path, "{}", ct);
            var missing = await store.LoadSettingsAsync(ct);
            Assert.NotNull(missing.Mcp);
            Assert.Equal(47800, missing.Mcp.Port);
            Assert.Empty(missing.Mcp.AllowedRoots);
            missing.Mcp.ToolModes["A"] = "block";
            Assert.True(missing.Mcp.ToolModes.ContainsKey("a"));

            await File.WriteAllTextAsync(path, """{ "mcp": { "enabled": true } }""", ct);
            var noModes = await store.LoadSettingsAsync(ct);
            Assert.True(noModes.Mcp.Enabled);
            Assert.NotNull(noModes.Mcp.ToolModes);
            Assert.NotNull(noModes.Mcp.AllowedRoots);

            await File.WriteAllTextAsync(path, """{ "mcp": { "toolModes": { "Read_File": "block" }, "allowedRoots": null } }""", ct);
            var loaded = await store.LoadSettingsAsync(ct);
            Assert.Equal("block", loaded.Mcp.ToolModes["read_file"]);
            Assert.NotNull(loaded.Mcp.AllowedRoots);

            await File.WriteAllTextAsync(path, """{ "mcp": null }""", ct);
            Assert.NotNull((await store.LoadSettingsAsync(ct)).Mcp);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Token_is_random_base64url_and_round_trips()
    {
        var a = McpSettings.NewToken();
        var b = McpSettings.NewToken();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);

        var settings = new McpSettings();
        Assert.Null(settings.ReadToken());
        settings.SetToken(a);
        Assert.NotEqual(a, settings.Token);
        Assert.Equal(a, settings.ReadToken());
    }

    private static JsonObject Args(string path) => new() { ["path"] = path };

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private sealed class FakeTool
    {
        public required McpToolDefinition Definition { get; init; }

        public int Runs;

        public string? LastPath;
    }

    private sealed class Fixture
    {
        private readonly List<McpTool> _tools = [];

        public Fixture()
        {
            Settings.AllowedRoots.Add(@"C:\Repo");
            Confirm = (request, _) =>
            {
                lock (Asked)
                {
                    Asked.Add(request);
                }

                return Task.FromResult(Answer);
            };
            Dispatcher = new McpToolDispatcher(_tools, () => Settings, () => Protected, (r, ct) => Confirm(r, ct), Audit);
        }

        public McpSettings Settings { get; } = new();

        public List<string> Protected { get; } = [];

        public AuditLog Audit { get; } = new(null);

        public List<McpConfirmation> Asked { get; } = [];

        public McpConsent Answer { get; set; } = McpConsent.Yes;

        public Func<McpConfirmation, CancellationToken, Task<McpConsent>> Confirm { get; set; }

        public McpToolDispatcher Dispatcher { get; }

        public FakeTool Add(
            string name,
            McpToolMode mode,
            bool writes = false,
            bool alwaysAsk = false,
            bool selfConfirming = false,
            IReadOnlyList<string>? pathArguments = null,
            bool namesOnly = false,
            bool readsTree = false,
            Func<JsonObject, string>? summary = null,
            Func<McpCall, CancellationToken, Task<McpToolResult>>? handler = null,
            Func<JsonObject, string?>? details = null)
        {
            var definition = new McpToolDefinition(
                name, "Tool " + name, new JsonObject(), mode, alwaysAsk, pathArguments ?? ["path"], writes, selfConfirming, namesOnly, readsTree);
            var fake = new FakeTool { Definition = definition };
            _tools.Add(new McpTool(
                definition,
                handler ?? ((call, _) =>
                {
                    Interlocked.Increment(ref fake.Runs);
                    fake.LastPath = call.Optional("path");
                    return Task.FromResult(new McpToolResult("ok"));
                }),
                summary,
                details));
            return fake;
        }

        public Task<McpToolResult> CallAsync(string name, JsonObject arguments, string client = Client) =>
            Dispatcher.CallAsync(name, new McpCall(client, arguments), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Mcp_settings_are_searchable()
    {
        var catalog = SettingsCatalog.Create(() => []);
        Assert.Contains(SettingsCatalog.Search(catalog, "claude code"), d => d.Key == "mcp.enabled");
        Assert.Contains(SettingsCatalog.Search(catalog, "port mcp"), d => d.Key == "mcp.port");
    }

    [Fact]
    public void Mcp_port_and_roots_settings_parse_input()
    {
        var catalog = SettingsCatalog.Create(() => []);
        var settings = new AppSettings();
        var port = catalog.Single(d => d.Key == "mcp.port");
        var roots = catalog.Single(d => d.Key == "mcp.roots");
        port.Set(settings, "50000");
        Assert.Equal(50000, settings.Mcp.Port);
        port.Set(settings, "80");
        port.Set(settings, "abc");
        Assert.Equal(50000, settings.Mcp.Port);
        roots.Set(settings, @" C:\A ; ;D:\B;C:\A ");
        Assert.Equal([@"C:\A", @"D:\B"], settings.Mcp.AllowedRoots);
        Assert.Equal(@"C:\A;D:\B", roots.Get(settings));
    }

    [Theory]
    [InlineData(true, McpToolMode.Allow, "allow", McpToolMode.Ask)]
    [InlineData(true, McpToolMode.Ask, null, McpToolMode.Ask)]
    [InlineData(false, McpToolMode.Allow, "bogus", McpToolMode.Ask)]
    [InlineData(false, McpToolMode.Block, "bogus", McpToolMode.Block)]
    [InlineData(false, McpToolMode.Block, "block", McpToolMode.Block)]
    [InlineData(false, McpToolMode.Allow, "ALLOW", McpToolMode.Allow)]
    public void Effective_mode_is_clamped_to_what_is_enforced(bool alwaysAsk, McpToolMode defaultMode, string? stored, McpToolMode expected)
    {
        var tool = new McpToolDefinition("t", "d", McpSchema.Object(), defaultMode, alwaysAsk);
        Assert.Equal(expected, McpToolDispatcher.EffectiveMode(tool, stored, hasConfigured: stored is not null));
    }
}
