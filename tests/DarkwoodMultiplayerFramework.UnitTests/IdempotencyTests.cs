using System;
using DarkwoodMultiplayerFramework.Actions;
using Xunit;

namespace DarkwoodMultiplayerFramework.UnitTests;

/// <summary>0.8.9 第 6 刀：权威同步的重复请求防护（ActionIdempotencyCache）。</summary>
public class IdempotencyTests
{
    private static NetworkActionResult Ok(Guid id) => new NetworkActionResult(id, true, new DarkwoodMultiplayerFramework.Core.StateVersion(1), string.Empty);

    [Fact]
    public void Same_RequestId_Stores_Only_Once()
    {
        var cache = new ActionIdempotencyCache(64);
        var id = Guid.NewGuid();
        cache.Store(Ok(id));
        var evicted = cache.Store(Ok(id)); // 重复请求：不重新执行
        Assert.Equal(Guid.Empty, evicted);
    }

    [Fact]
    public void TryGet_Returns_First_Result_For_Duplicate_Request()
    {
        var cache = new ActionIdempotencyCache(64);
        var id = Guid.NewGuid();
        cache.Store(Ok(id));
        Assert.True(cache.TryGet(id, out var first));
        Assert.True(first.Accepted);
    }

    [Fact]
    public void Evicts_Oldest_When_Full()
    {
        var cache = new ActionIdempotencyCache(3);
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid(); var d = Guid.NewGuid();
        cache.Store(Ok(a)); cache.Store(Ok(b)); cache.Store(Ok(c));
        cache.Store(Ok(d)); // 驱逐最旧 a
        Assert.False(cache.TryGet(a, out _));
        Assert.True(cache.TryGet(d, out _));
    }
}
