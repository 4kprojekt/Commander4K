using System.Text;
using Commander4K.Core.Execution;
using Commander4K.Core.Plugins;

namespace Commander4K.Plugins.Tests;

public sealed class HostThreadingTests
{
    [Theory]
    [InlineData("abc", 3, 3)]
    [InlineData("aż", 2, 1)] // ż = 2 bytes, cut after the first
    [InlineData("a€", 3, 1)] // € = 3 bytes, cut after two
    [InlineData("a€", 4, 4)]
    [InlineData("😀", 3, 0)]
    public void Utf8_cut_never_splits_a_character(string text, int count, int expected) =>
        Assert.Equal(expected, ExternalPlugin.Utf8Boundary(Encoding.UTF8.GetBytes(text), count));

    [Fact]
    public async Task LeaveUi_moves_off_a_thread_with_a_synchronization_context()
    {
        var previous = SynchronizationContext.Current;
        var ui = new SynchronizationContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(ui);
            Assert.Null(await Work()); // inline it would still see the caller's context
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        static async Task<SynchronizationContext?> Work()
        {
            await CallerThread.LeaveUi();
            return SynchronizationContext.Current;
        }
    }
}
