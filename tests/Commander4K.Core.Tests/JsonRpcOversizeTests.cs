using Commander4K.Core.Plugins;

namespace Commander4K.Core.Tests;

public sealed class JsonRpcOversizeTests
{
    [Fact]
    public async Task Oversized_reply_fails_pending_calls_with_a_clear_message()
    {
        var token = TestContext.Current.CancellationToken;
        using var reader = new OneLineReader();
        await using var writer = new StringWriter();
        await using var connection = new JsonRpcConnection(reader, writer, (_, _, _) => Task.FromResult<object?>(null));
        connection.Start();

        var call = connection.RequestAsync("panel.provide", new { }, token);
        reader.Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"files\":[{\"name\":\"a\",\"text\":\"" + new string('x', JsonRpcConnection.MaxMessageLength) + "\"}]}}");

        var error = await Assert.ThrowsAsync<PluginMessageTooLargeException>(() => call);

        Assert.Contains("16 MB", error.Message, StringComparison.Ordinal);
        Assert.Contains("url", error.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(error);
        await Assert.ThrowsAsync<PluginMessageTooLargeException>(() => connection.Completion);
    }

    /// <summary>The plugin's stdout: one line once <see cref="Send"/> is called, then the stream stays open.</summary>
    private sealed class OneLineReader : TextReader
    {
        private readonly TaskCompletionSource<string> _line = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sent;

        public void Send(string line) => _line.TrySetResult(line);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_sent)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            _sent = true;
            return await _line.Task.WaitAsync(cancellationToken);
        }
    }
}
