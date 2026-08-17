using HarmonyLib;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 测试辅助：允许直接启动的多个游戏实例共存（本地双开联机测试）。
/// 原版 Steamworks.NET 的 SteamManager.Awake 在 SteamAPI.RestartAppIfNecessary 返回 true 时
/// 直接 Application.Quit()——这会阻止绕过 Steam 启动器启动的第二个实例。
/// 本补丁强制返回 false（不再要求"由 Steam 重启启动"）：
/// 通过 Steam 正常启动的实例本就不需要重启（原返回值就是 false），行为不变；
/// 直接启动 exe 的实例将跳过退出检查，从而支持双开。
/// 注意：仅影响启动检查，云存档/成就等 Steam 功能不受影响。
/// </summary>
[HarmonyPatch(typeof(Steamworks.SteamAPI), nameof(Steamworks.SteamAPI.RestartAppIfNecessary), new[]{ typeof(Steamworks.AppId_t) })]
public static class DarkwoodSteamMultiInstancePatch
{
    public static bool Prefix(ref bool __result)
    {
        __result = false;
        return false;
    }
}
