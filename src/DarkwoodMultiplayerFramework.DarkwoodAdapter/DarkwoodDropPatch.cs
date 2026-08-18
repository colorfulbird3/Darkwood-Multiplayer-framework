using HarmonyLib;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// 0.8.9 第 2 刀：Drop 全 Host Authority。
/// 联机状态下玩家扔东西不再直接产生世界结果：
/// - 客户端：拦截原版 mutation → DropRequest → Host 执行
/// - 主机：拦截原版 mutation → WorldAuthority.DropItem(0, ...) 本地执行
/// </summary>
[HarmonyPatch(typeof(InvSlot), "dropItem")]
internal static class DarkwoodDropPatch
{
    private static bool Prefix(InvSlot __instance)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsMultiplayerActive || runtime.State != ConnectionState.Ready)
            return true;

        if (runtime.IsHost)
        {
            runtime.World.DropItem(0, BuildPayload(__instance), default, (_, _, _, _) => { });
            return false;
        }

        if (runtime.IsClient)
        {
            runtime.TryRequestDrop(__instance);
            return false;
        }

        return true;
    }

    internal static DropItemPayload BuildPayload(InvSlot slot)
    {
        var player = Player.Instance;
        var fromHotbar = slot.inventory != null && slot.inventory.invType == Inventory.InvType.hotbar;
        var slotIndex = slot.inventory != null ? slot.inventory.slots.IndexOf(slot) : -1;
        var amount = !InvItemClass.isNull(slot.invItem) ? slot.invItem.amount : 0;
        var pos = player != null ? player._transform.position : Vector3.zero;
        var rot = player != null ? player._transform.rotation : Quaternion.identity;
        return new DropItemPayload(fromHotbar, slotIndex, amount, pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w);
    }
}
