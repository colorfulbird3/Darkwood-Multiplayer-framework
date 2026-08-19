using DarkwoodMultiplayerFramework.Core;
using HarmonyLib;
using Pathfinding;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// P1-c：客户端剥离 A* 图后，多处游戏系统仍会调用 WhereAmI.checkWhereAmI 和 AstarPath.StartPath，
/// 持续产生 NullReferenceException / "There are no graphs in the scene" 刷屏。
/// 目标：防御已知 NRE 前置条件 + 无害化无图路径请求（Host 仍是唯一 AI 权威；本地怪物 AI 保持冻结）。
/// </summary>
[HarmonyPatch]
internal static class DarkwoodWhereAmIDefensePatch
{
    // 反编译确认：public void WhereAmI.checkWhereAmI()。前置条件不满足时跳过原方法，避免 NRE。
    [HarmonyPatch(typeof(WhereAmI), "checkWhereAmI")]
    private static class WhereAmIPrefixPatch
    {
        private static bool Prefix()
        {
            var player = Player.Instance;
            if (player == null || player._transform == null) return false;
            return true;
        }
    }

    // 客户端无 A* 图时，AstarPath.StartPath 每次都会打 "There are no graphs in the scene"（内部已 return，但刷屏）。
    // 在客户端（strip graph 环境）直接跳过，避免日志噪音；主机环境保留原逻辑（有图）。
    [HarmonyPatch(typeof(AstarPath), "StartPath", new[] { typeof(Path), typeof(bool) })]
    private static class StartPathPrefixPatch
    {
        private static bool Prefix()
        {
            var runtime = DarkwoodAdapterRuntime.Instance;
            if (runtime == null || !runtime.IsClient || runtime.State != ConnectionState.Ready) return true;
            var active = AstarPath.active;
            if (active == null || active.graphs == null || active.graphs.Length == 0) return false; // 跳过：无本地图，请求必然失败
            return true;
        }
    }
}
