using Commander4K.Core.Folders;

namespace Commander4K.Core.Tests;

public sealed class ViewHistoryTests
{
    [Fact]
    public void Push_back_and_trail()
    {
        var history = new ViewHistory<string>();
        Assert.False(history.CanGoBack);

        history.Push("Dom");
        history.Push("Serwery");
        history.Push("VPS");

        Assert.Equal("VPS", history.Current);
        Assert.Equal(["Dom", "Serwery", "VPS"], history.Trail);
        Assert.True(history.CanGoBack);
        Assert.Equal("Serwery", history.Back());
        Assert.Equal("Dom", history.Back());
        Assert.False(history.CanGoBack);
        Assert.Throws<InvalidOperationException>(() => { history.Back(); });
    }

    [Fact]
    public void Cycles_are_kept()
    {
        var history = new ViewHistory<string>();
        history.Push("A");
        history.Push("B");
        history.Push("A");

        Assert.Equal(["A", "B", "A"], history.Trail);
        Assert.Equal("B", history.Back());
    }

    [Fact]
    public void Replacing_current_keeps_previous_views()
    {
        var history = new ViewHistory<string>();
        history.Push("A");
        history.Push("B");

        history.ReplaceCurrent("B refreshed");

        Assert.Equal(["A", "B refreshed"], history.Trail);
        Assert.Equal("A", history.Back());
    }

    [Fact]
    public void Limit_drops_oldest_at_50()
    {
        var history = new ViewHistory<int>();
        for (var i = 0; i < 60; i++)
        {
            history.Push(i);
        }

        Assert.Equal(50, history.Trail.Count);
        Assert.Equal(10, history.Trail[0]);
        Assert.Equal(59, history.Current);
    }

    [Fact]
    public void Jump_to_breadcrumb_drops_later()
    {
        var history = new ViewHistory<string>();
        history.Push("A");
        history.Push("B");
        history.Push("C");

        Assert.Equal("A", history.JumpTo(0));
        Assert.Equal(["A"], history.Trail);
        Assert.Throws<ArgumentOutOfRangeException>(() => history.JumpTo(3));
    }

    [Fact]
    public void Empty_history_has_no_current()
    {
        var history = new ViewHistory<string>();
        Assert.Throws<InvalidOperationException>(() => history.Current);
        history.Push("A");
        history.Clear();
        Assert.Empty(history.Trail);
    }
}
