using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

public readonly struct GuestProfileMessage
{
    public GuestProfileMessage(PlayerInventoryStatePayload inventory, float x, float y, float z, int day, int joinCount, float health, float maxHealth, bool downed)
    { Inventory=inventory; X=x; Y=y; Z=z; Day=day; JoinCount=joinCount; Health=health; MaxHealth=maxHealth; Downed=downed; }
    public PlayerInventoryStatePayload Inventory {get;}
    public float X {get;} public float Y {get;} public float Z {get;}
    public int Day {get;} public int JoinCount {get;}
    public float Health {get;} public float MaxHealth {get;} public bool Downed {get;}
}

/// <summary>Host-authoritative per-player health state. Clients apply it only for their own player id.</summary>

public readonly struct RescueRequestMessage
{
    public RescueRequestMessage(int playerId, bool cancel) { PlayerId=playerId; Cancel=cancel; }
    public int PlayerId {get;} public bool Cancel {get;}
}

/// <summary>Host-authoritative rescue progress (0..1). Active=false is the terminal state after completion or cancellation.</summary>
public readonly struct RescueProgressMessage
{
    public RescueProgressMessage(int targetId, int rescuerId, float progress, bool active)
    { TargetId=targetId; RescuerId=rescuerId; Progress=progress; Active=active; }
    public int TargetId {get;} public int RescuerId {get;} public float Progress {get;} public bool Active {get;}
}

/// <summary>Broadcast when every player is downed: each machine then runs the vanilla death ending locally.</summary>
public readonly struct AllDownedMessage
{
}

/// <summary>Persistent hot-join guest identity record stored by the host beside the save. Binary, format-versioned.</summary>
public readonly struct GuestProfileRecord
{
    public GuestProfileRecord(string guestKey, int day, int joinCount, float x, float y, float z, InventorySlotWire[] backpack, InventorySlotWire[] hotbar, long lastSeenUtcTicks)
    { GuestKey=guestKey??string.Empty; Day=day; JoinCount=joinCount; X=x; Y=y; Z=z; Backpack=backpack??Array.Empty<InventorySlotWire>(); Hotbar=hotbar??Array.Empty<InventorySlotWire>(); LastSeenUtcTicks=lastSeenUtcTicks; }
    public string GuestKey {get;} public int Day {get;} public int JoinCount {get;}
    public float X {get;} public float Y {get;} public float Z {get;}
    public InventorySlotWire[] Backpack {get;} public InventorySlotWire[] Hotbar {get;}
    public long LastSeenUtcTicks {get;}
    public bool HasPosition => X != 0f || Y != 0f || Z != 0f;
}

/// <summary>Client melee attack intent. The host derives damage from its own shadow inventory; the client never sends damage values.</summary>
