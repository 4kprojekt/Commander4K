using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Commander4K.Core.Execution;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class StreamFaultTests
{
    [Fact]
    public async Task Iterator_faults_reach_the_consumer_without_passing_through_dotnets_AsTask_adapter()
    {
        // Every throw/rethrow of our exception, with the stack at that moment.
        var throws = new ConcurrentBag<string>();
        void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is VfsAuthenticationRequiredException { Path: "sftp://test/" })
            {
                // Method names only: resolving file/line info here takes the symbol reader's lock mid-throw and can hang.
                throws.Add(new System.Diagnostics.StackTrace(fNeedFileInfo: false).ToString());
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            var executor = new ResilientAsyncExecutor();
            var error = await Assert.ThrowsAsync<VfsAuthenticationRequiredException>(async () =>
            {
                await foreach (var item in executor.StreamAsync(FailsLater(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken))
                {
                    _ = item;
                }
            });

            Assert.Equal("sftp://test/", error.Path);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        // ValueTaskSourceAsTask reads the iterator's result inside .NET code; a throw there is what made the debugger
        // stop ("user-unhandled") on every refused login or denied folder.
        Assert.NotEmpty(throws);
        Assert.DoesNotContain(throws, stack => stack.Contains("ValueTaskSourceAsTask", StringComparison.Ordinal));
    }

    /// <summary>Like a provider whose connection fails after an await (SFTP login refused).</summary>
    private static async IAsyncEnumerable<int> FailsLater([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(20, cancellationToken);
        if (cancellationToken.CanBeCanceled)
        {
            throw new VfsAuthenticationRequiredException("sftp://test/", "sftp://@test:22");
        }

        yield return 1;
    }
}
