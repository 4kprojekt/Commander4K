using Commander4K.Core.Web;

namespace Commander4K.Core.Tests;

public sealed class RateWindowTests
{
    [Fact]
    public void Allows_the_limit_then_drops_until_the_window_moves_on()
    {
        var window = new RateWindow(5, 1000);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(window.TryTake(100 + i));
        }

        Assert.False(window.TryTake(500));
        Assert.False(window.TryTake(1099));
        Assert.True(window.TryTake(1100)); // The first one is a full second old.
        Assert.False(window.TryTake(1100));
        Assert.True(window.TryTake(2200));
    }
}
