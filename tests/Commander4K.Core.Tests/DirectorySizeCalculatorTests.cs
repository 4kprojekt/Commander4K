using Commander4K.Core.Execution;
using Commander4K.Core.Vfs;

namespace Commander4K.Core.Tests;

public sealed class DirectorySizeCalculatorTests
{
    [Fact]
    public async Task Cancellation_ends_the_workers_quietly_and_reaches_only_the_caller()
    {
        var provider = new FakeVfsProvider()
            .AddDirectory("mem://root/slow")
            .AddDirectory("mem://root/fast")
            .AddFile("mem://root/fast/a.bin", 10)
            .AddDirectory("mem://root/slow/x");
        provider.HangOn.Add("mem://root/slow");
        var calculator = new DirectorySizeCalculator(new VfsRouter(new ResilientAsyncExecutor(), [provider])) { MaxDegreeOfParallelism = 4 };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => calculator.CalculateAsync("mem://root", null, cancel.Token));

            // A cancellation thrown out of a worker passes through Task.Run's lambda, which is what made the debugger stop
            // ("user-unhandled") every time the preview moved on while folder sizes were being computed.
            Assert.DoesNotContain("WorkerAsync", error.StackTrace ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            provider.Release.Set();
        }
    }
}
