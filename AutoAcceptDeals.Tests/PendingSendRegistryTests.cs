using System;
using Xunit;

namespace AutoAcceptDeals.Tests;

public class PendingSendRegistryTests
{
    private readonly PendingSendRegistry<string> _reg = new();

    [Fact]
    public void TakeForKey_EmptyRegistry_ReturnsNull()
        => Assert.Null(_reg.TakeForKey(new IntPtr(1)));

    [Fact]
    public void Register_ThenTake_ReturnsValue()
    {
        var key = new IntPtr(1);
        _reg.Register(key, "hello");
        Assert.Equal("hello", _reg.TakeForKey(key));
    }

    [Fact]
    public void Take_RemovesEntry_SecondCallReturnsNull()
    {
        var key = new IntPtr(1);
        _reg.Register(key, "val");
        _reg.TakeForKey(key);
        Assert.Null(_reg.TakeForKey(key));
    }

    [Fact]
    public void DoubleRegister_OverwritesSilently_SecondValueWins()
    {
        var key = new IntPtr(1);
        _reg.Register(key, "first");
        _reg.Register(key, "second");
        Assert.Equal("second", _reg.TakeForKey(key));
    }

    [Fact]
    public void Clear_ResetsAllState()
    {
        var key = new IntPtr(1);
        _reg.Register(key, "val");
        _reg.Clear();
        Assert.Null(_reg.TakeForKey(key));
    }
}
