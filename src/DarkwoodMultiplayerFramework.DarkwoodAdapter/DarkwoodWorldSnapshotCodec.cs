using System;
using System.IO;
using DarkwoodMultiplayerFramework.Protocol;
using DarkwoodMultiplayerFramework.Snapshots;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed class DarkwoodWorldSnapshot
{
    public DarkwoodWorldSnapshot(string scene, string registryDigest, long serverTick, EntityStateWire[] entities, InventoryStateMessage[] inventories)
    {
        Scene = scene ?? string.Empty;
        RegistryDigest = registryDigest ?? string.Empty;
        ServerTick = serverTick;
        Entities = entities ?? Array.Empty<EntityStateWire>();
        Inventories = inventories ?? Array.Empty<InventoryStateMessage>();
    }

    public string Scene { get; }
    public string RegistryDigest { get; }
    public long ServerTick { get; }
    public EntityStateWire[] Entities { get; }
    public InventoryStateMessage[] Inventories { get; }
}

/// <summary>Dedicated schema for a complete join snapshot; deltas are deliberately not reused.</summary>
public static class DarkwoodWorldSnapshotCodec
{
    private const int MaxEntities = 4096;
    private const int MaxInventories = 2048;
    private const int MaxPayload = 64 * 1024 * 1024;

    public static byte[] Encode(string scene, string registryDigest, long serverTick, EntityStateWire[] entities, InventoryStateMessage[] inventories)
    {
        entities ??= Array.Empty<EntityStateWire>();
        inventories ??= Array.Empty<InventoryStateMessage>();
        if (entities.Length > MaxEntities || inventories.Length > MaxInventories) throw new InvalidOperationException("World snapshot exceeds entity limits.");
        var entityRecords=new byte[entities.Length][];for(var i=0;i<entities.Length;i++)entityRecords[i]=ReplicationProtocolCodec.Encode(new EntityDeltaMessage(string.Empty,0,new[]{entities[i]},Array.Empty<EntityStateWire>()));
        var inventoryRecords=new byte[inventories.Length][];for(var i=0;i<inventories.Length;i++)inventoryRecords[i]=ReplicationProtocolCodec.Encode(inventories[i]);
        return WorldSnapshotWireCodec.Encode(new WorldSnapshotWire(scene,registryDigest,serverTick,entityRecords,inventoryRecords));
    }

    public static DarkwoodWorldSnapshot Decode(byte[] data)
    {
        if (data == null || data.Length == 0 || data.Length > MaxPayload) throw new InvalidDataException("World snapshot size is invalid.");
        var wire=WorldSnapshotWireCodec.Decode(data);var entities = new EntityStateWire[wire.EntityRecords.Length];
        for (var i = 0; i < entities.Length; i++)
        {
            var decoded = ReplicationProtocolCodec.DecodeEntityDelta(wire.EntityRecords[i]);
            if (decoded.Entities.Length != 1 || decoded.Despawns.Length != 0) throw new InvalidDataException("World snapshot entity record is invalid.");
            entities[i] = decoded.Entities[0];
        }
        var inventories = new InventoryStateMessage[wire.InventoryRecords.Length];
        for (var i = 0; i < inventories.Length; i++) inventories[i] = ReplicationProtocolCodec.DecodeInventoryState(wire.InventoryRecords[i]);
        return new DarkwoodWorldSnapshot(wire.Scene,wire.RegistryDigest,wire.ServerTick, entities, inventories);
    }

}
