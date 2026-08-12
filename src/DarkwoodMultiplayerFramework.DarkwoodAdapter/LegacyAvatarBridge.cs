using System;
using System.Reflection;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed class LegacyAvatarBridge : IDisposable
{
    private readonly object avatar;
    private readonly MethodInfo tick;
    private readonly MethodInfo attack;
    private readonly MethodInfo dispose;
    public LegacyAvatarBridge(int playerId, Player localPlayer)
    {
        var type = Type.GetType("DarkwoodMultiplayerFramework.RemoteAvatar, DarkwoodMultiplayerFramework", true)!;
        avatar = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { playerId, localPlayer }, null)!;
        tick = type.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        attack = type.GetMethod("Attack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        dispose = type.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    }
    public void Tick() => tick.Invoke(avatar, null);
    public void Attack(Vector3 direction) => attack.Invoke(avatar, new object[] { direction });
    public void Dispose() => dispose.Invoke(avatar, null);
}
