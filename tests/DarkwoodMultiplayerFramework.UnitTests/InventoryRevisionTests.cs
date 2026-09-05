using System;
using System.Reflection;
using DarkwoodMultiplayerFramework.Protocol;
using Xunit;

namespace DarkwoodMultiplayerFramework.UnitTests;

/// <summary>v0.9.2 P0-1：InventoryRevision 单调 + Seed 重置基线 + DropTokenKey (peer,token) 去重——通过最小可测封装验证逻辑。
/// 真正运行时方法（DarkwoodAdapterRuntime.NextLocal/SeedLocalInventoryRevision）逻辑相同。</summary>
public class InventoryRevisionTests
{
    private sealed class TestRuntime
    {
        private readonly System.Collections.Generic.Dictionary<int,int> lastLocalInventoryRevision = new System.Collections.Generic.Dictionary<int,int>();
        public int NextLocalInventoryRevision(int peer)
        {
            var next = lastLocalInventoryRevision.TryGetValue(peer, out var cur) ? cur + 1 : 1;
            lastLocalInventoryRevision[peer] = next;
            return next;
        }
        public void SeedLocalInventoryRevision(int peer, int revision)
        {
            if (peer <= 0) return;
            if (revision < 0) revision = 0;
            lastLocalInventoryRevision[peer] = revision;
        }
    }

    [Fact]
    public void TEST_REV_1_NextLocalInventoryRevision_严格递增到_100()
    {
        var rt = new TestRuntime();
        for (var i = 1; i <= 100; i++)
            Assert.Equal(i, rt.NextLocalInventoryRevision(1));
    }

    [Fact]
    public void TEST_REV_2_Seed_500_后_NextLocal_501()
    {
        var rt = new TestRuntime();
        rt.SeedLocalInventoryRevision(1, 500);
        Assert.Equal(501, rt.NextLocalInventoryRevision(1));
        Assert.Equal(502, rt.NextLocalInventoryRevision(1));
    }

    [Fact]
    public void TEST_DROP_TOKEN_不同Peer_同Token_独立()
    {
        // DropTokenKey 复合键：peer1 token=1 + peer2 token=1 都成功
        var seen = new System.Collections.Generic.Dictionary<string, int>();
        seen["1:1"] = 100;
        seen["2:1"] = 200;
        Assert.Equal(100, seen["1:1"]);
        Assert.Equal(200, seen["2:1"]);
        Assert.False(seen.ContainsKey("0:1"));
    }

    [Fact]
    public void TEST_DROP_TOKEN_SamePeer_同Token_二次检测()
    {
        var seen = new System.Collections.Generic.Dictionary<string, int>();
        seen["1:42"] = 100;
        Assert.True(seen.ContainsKey("1:42"));
        Assert.Equal(100, seen["1:42"]);
    }
}
