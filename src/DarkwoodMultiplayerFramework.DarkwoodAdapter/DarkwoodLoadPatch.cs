using HarmonyLib;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// FIX-003：强制 WorldGenerator.Start 在客户端加载主机存档时看到 loadingGame=true。
/// alpha.15 实测：置标志后直接 LoadScene，客户端注册表仍为教学梦境的 8 个实体
/// （说明生成新世界分支仍被选中），但全程序集搜索确认没有任何代码在场景加载
/// 期间重置该标志。为消除一切时序不确定性，本补丁在 WorldGenerator.Start 入口
/// 再次强制标志，并记录强制前的值用于诊断。
/// </summary>
[HarmonyPatch(typeof(WorldGenerator), "Start")]
public static class DarkwoodLoadPatch
{
    public static void Prefix()
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.ClientSaveLoadPending) return;
        var before = global::Core.loadingGame;
        global::Core.loadingGame = true;
        global::Core.loadedGame = true;
        DarkwoodAdapterRuntime.LogMessage($"强制加载分支：WorldGenerator.Start 时 loadingGame 原值={before}，已强制置 true（客户端存档加载进行中）。");
    }
}
