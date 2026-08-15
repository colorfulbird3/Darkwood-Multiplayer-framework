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

/// <summary>
/// FIX-004：客户端联机存档加载跳过 joinPaths()（A* 每个 GridGraph 的 OnPostScan，
/// 大世界在弱机上可能耗时数分钟——实测卡在 92% 且 onFinishedLoading 不触发）。
/// 客户端是视觉镜像：怪物 AI 冻结、玩家移动不依赖寻路，跳过是安全的。
/// </summary>
[HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.joinPaths))]
public static class DarkwoodJoinPathsPatch
{
    public static bool Prefix()
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || !runtime.ClientSaveLoadPending) return true;
        DarkwoodAdapterRuntime.LogMessage("客户端已跳过 joinPaths()（联机视觉镜像无需本地寻路图连接，避免加载卡 92%）。");
        return false;
    }
}

/// <summary>FIX-005：客户端跳过 AstarData.DeserializeGraphs——主机打包已剥离导航图
/// （savs.dat 的 graph 字段为空），客户端世界恢复无需路径图；跳过可防止空图反序列化
/// 在 A* 内部抛异常或阻塞。</summary>
[HarmonyPatch(typeof(Pathfinding.AstarData), "DeserializeGraphs", new[]{ typeof(byte[]) })]
public static class DarkwoodGraphDeserializePatch
{
    public static bool Prefix()
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || !runtime.ClientSaveLoadPending) return true;
        DarkwoodAdapterRuntime.LogMessage("客户端已跳过 AstarData.DeserializeGraphs（导航图已由主机剥离）。");
        return false;
    }
}

/// <summary>
/// FIX-006：onFinishedLoading 回调必须挂到“真正执行 Load 的 SaveManager 实例”上。
/// SaveManager 是场景内单例（Awake 里 registerMe，无 DontDestroyOnLoad）：主菜单场景
/// 的实例在 LoadScene("chapter1") 后随场景销毁，而 Load() 跑在 chapter1 场景的新实例上。
/// 在新实例上只有 WorldChunk 等场景对象的订阅，我们的完成回调从未触发 → 客户端永远
/// 停在 LoadingSave（实测：加载界面卡 92%，且 timeScale=0 无人恢复，hideLoadingScreen
/// 的 timeScaleDependent Invoke 永不执行）。本补丁在 Load 入口把回调幂等挂到 __instance。
/// </summary>
[HarmonyPatch(typeof(SaveManager), "Load", new[]{ typeof(bool), typeof(bool) })]
public static class DarkwoodLoadFinishedPatch
{
    public static void Prefix(SaveManager __instance)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || !runtime.ClientSaveLoadPending) return;
        DarkwoodAdapterRuntime.LogMessage("客户端 SaveManager.Load 入口已触发（FIX-006 挂载完成回调）。");
        DarkwoodAdapterRuntime.AttachLoadFinishedCallback(__instance);
    }
}
