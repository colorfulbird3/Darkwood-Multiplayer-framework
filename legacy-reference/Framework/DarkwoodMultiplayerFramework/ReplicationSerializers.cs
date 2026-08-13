using System;
using Mirror;

namespace DarkwoodMultiplayerFramework;

internal static class ReplicationSerializers
{
	public static void Install()
	{
		Writer<ReplicationHelloMessage>.write = delegate(NetworkWriter w, ReplicationHelloMessage m)
		{
			w.WriteInt(m.Protocol);
			w.WriteString(m.Version);
		};
		Reader<ReplicationHelloMessage>.read = delegate(NetworkReader r)
		{
			ReplicationHelloMessage result4 = default(ReplicationHelloMessage);
			result4.Protocol = r.ReadInt();
			result4.Version = r.ReadString();
			return result4;
		};
		Writer<ReplicationWelcomeMessage>.write = WriteWelcome;
		Reader<ReplicationWelcomeMessage>.read = ReadWelcome;
		Writer<ReplicationReadyMessage>.write = delegate(NetworkWriter w, ReplicationReadyMessage m)
		{
			w.WriteUInt(m.Epoch);
			w.WriteString(m.Scene);
			w.WriteULong(m.RegistryDigest);
		};
		Reader<ReplicationReadyMessage>.read = delegate(NetworkReader r)
		{
			ReplicationReadyMessage result3 = default(ReplicationReadyMessage);
			result3.Epoch = r.ReadUInt();
			result3.Scene = r.ReadString();
			result3.RegistryDigest = r.ReadULong();
			return result3;
		};
		Writer<EntitySpawnBatchMessage>.write = WriteSpawnBatch;
		Reader<EntitySpawnBatchMessage>.read = ReadSpawnBatch;
		Writer<EntityDeltaBatchMessage>.write = delegate(NetworkWriter w, EntityDeltaBatchMessage m)
		{
			WriteDeltaBatch(w, m.Epoch, m.ServerTick, m.Scene, m.Entities);
		};
		Reader<EntityDeltaBatchMessage>.read = ReadDeltaBatch;
		Writer<EntityKeyframeBatchMessage>.write = delegate(NetworkWriter w, EntityKeyframeBatchMessage m)
		{
			WriteDeltaBatch(w, m.Epoch, m.ServerTick, m.Scene, m.Entities);
		};
		Reader<EntityKeyframeBatchMessage>.read = ReadKeyframeBatch;
		Writer<EntityDespawnBatchMessage>.write = WriteDespawnBatch;
		Reader<EntityDespawnBatchMessage>.read = ReadDespawnBatch;
		Writer<InventoryTransactionRequest>.write = WriteInventoryRequest;
		Reader<InventoryTransactionRequest>.read = ReadInventoryRequest;
		Writer<InventoryStateMessage>.write = WriteInventoryState;
		Reader<InventoryStateMessage>.read = ReadInventoryState;
		Writer<EntityActionCommand>.write = WriteAction;
		Reader<EntityActionCommand>.read = ReadAction;
		Writer<ReplicationDigestMessage>.write = WriteDigest;
		Reader<ReplicationDigestMessage>.read = ReadDigest;
		Writer<NetworkStatsPingMessage>.write = delegate(NetworkWriter w, NetworkStatsPingMessage m)
		{
			w.WriteUInt(m.Sequence);
		};
		Reader<NetworkStatsPingMessage>.read = delegate(NetworkReader r)
		{
			NetworkStatsPingMessage result2 = default(NetworkStatsPingMessage);
			result2.Sequence = r.ReadUInt();
			return result2;
		};
		Writer<NetworkStatsPongMessage>.write = delegate(NetworkWriter w, NetworkStatsPongMessage m)
		{
			w.WriteUInt(m.Sequence);
		};
		Reader<NetworkStatsPongMessage>.read = delegate(NetworkReader r)
		{
			NetworkStatsPongMessage result = default(NetworkStatsPongMessage);
			result.Sequence = r.ReadUInt();
			return result;
		};
	}

	private static void WriteWelcome(NetworkWriter w, ReplicationWelcomeMessage m)
	{
		w.WriteInt(m.Protocol);
		w.WriteUInt(m.Epoch);
		w.WriteUInt(m.ServerTick);
		w.WriteString(m.Scene);
		w.WriteString(m.Error);
	}

	private static ReplicationWelcomeMessage ReadWelcome(NetworkReader r)
	{
		ReplicationWelcomeMessage result = default(ReplicationWelcomeMessage);
		result.Protocol = r.ReadInt();
		result.Epoch = r.ReadUInt();
		result.ServerTick = r.ReadUInt();
		result.Scene = r.ReadString();
		result.Error = r.ReadString();
		return result;
	}

	private static void WriteSpawnBatch(NetworkWriter w, EntitySpawnBatchMessage m)
	{
		WriteHeader(w, m.Epoch, m.ServerTick, m.Scene);
		int num = ((m.Entities != null) ? m.Entities.Length : 0);
		w.WriteInt(num);
		for (int i = 0; i < num; i++)
		{
			EntitySpawnWire entitySpawnWire = m.Entities[i];
			w.WriteUInt(entitySpawnWire.NetworkId);
			w.WriteULong(entitySpawnWire.PersistentId);
			w.WriteByte(entitySpawnWire.Kind);
			w.WriteUInt(entitySpawnWire.StateRevision);
			w.WriteUInt(entitySpawnWire.InventoryRevision);
			WriteState(w, entitySpawnWire.State, 15);
			WriteSlots(w, entitySpawnWire.Inventory);
		}
	}

	private static EntitySpawnBatchMessage ReadSpawnBatch(NetworkReader r)
	{
		ReadHeader(r, out var epoch, out var tick, out var scene);
		int num = ReadCount(r, 64, "spawn");
		EntitySpawnWire[] array = new EntitySpawnWire[num];
		for (int i = 0; i < num; i++)
		{
			array[i] = new EntitySpawnWire
			{
				NetworkId = r.ReadUInt(),
				PersistentId = r.ReadULong(),
				Kind = r.ReadByte(),
				StateRevision = r.ReadUInt(),
				InventoryRevision = r.ReadUInt(),
				State = ReadState(r, 15),
				Inventory = ReadSlots(r)
			};
		}
		EntitySpawnBatchMessage result = default(EntitySpawnBatchMessage);
		result.Epoch = epoch;
		result.ServerTick = tick;
		result.Scene = scene;
		result.Entities = array;
		return result;
	}

	private static void WriteDeltaBatch(NetworkWriter w, uint epoch, uint tick, string scene, EntityDeltaWire[] entities)
	{
		WriteHeader(w, epoch, tick, scene);
		int num = ((entities != null) ? entities.Length : 0);
		w.WriteInt(num);
		for (int i = 0; i < num; i++)
		{
			EntityDeltaWire entityDeltaWire = entities[i];
			w.WriteUInt(entityDeltaWire.NetworkId);
			w.WriteUInt(entityDeltaWire.Revision);
			w.WriteUShort(entityDeltaWire.DirtyMask);
			WriteState(w, entityDeltaWire.State, entityDeltaWire.DirtyMask);
		}
	}

	private static EntityDeltaWire[] ReadDeltaWires(NetworkReader r)
	{
		int num = ReadCount(r, 128, "delta");
		EntityDeltaWire[] array = new EntityDeltaWire[num];
		for (int i = 0; i < num; i++)
		{
			uint networkId = r.ReadUInt();
			uint revision = r.ReadUInt();
			ushort num2 = r.ReadUShort();
			if ((num2 & 0xFFFFFFF0u) != 0)
			{
				throw new InvalidOperationException("Invalid entity dirty mask: " + num2);
			}
			array[i] = new EntityDeltaWire
			{
				NetworkId = networkId,
				Revision = revision,
				DirtyMask = num2,
				State = ReadState(r, num2)
			};
		}
		return array;
	}

	private static EntityDeltaBatchMessage ReadDeltaBatch(NetworkReader r)
	{
		ReadHeader(r, out var epoch, out var tick, out var scene);
		EntityDeltaBatchMessage result = default(EntityDeltaBatchMessage);
		result.Epoch = epoch;
		result.ServerTick = tick;
		result.Scene = scene;
		result.Entities = ReadDeltaWires(r);
		return result;
	}

	private static EntityKeyframeBatchMessage ReadKeyframeBatch(NetworkReader r)
	{
		ReadHeader(r, out var epoch, out var tick, out var scene);
		EntityKeyframeBatchMessage result = default(EntityKeyframeBatchMessage);
		result.Epoch = epoch;
		result.ServerTick = tick;
		result.Scene = scene;
		result.Entities = ReadDeltaWires(r);
		return result;
	}

	private static void WriteDespawnBatch(NetworkWriter w, EntityDespawnBatchMessage m)
	{
		WriteHeader(w, m.Epoch, m.ServerTick, m.Scene);
		int num = ((m.Entities != null) ? m.Entities.Length : 0);
		w.WriteInt(num);
		for (int i = 0; i < num; i++)
		{
			w.WriteUInt(m.Entities[i].NetworkId);
			w.WriteUInt(m.Entities[i].Revision);
			w.WriteByte(m.Entities[i].Reason);
		}
	}

	private static EntityDespawnBatchMessage ReadDespawnBatch(NetworkReader r)
	{
		ReadHeader(r, out var epoch, out var tick, out var scene);
		int num = ReadCount(r, 128, "despawn");
		EntityDespawnWire[] array = new EntityDespawnWire[num];
		for (int i = 0; i < num; i++)
		{
			array[i] = new EntityDespawnWire
			{
				NetworkId = r.ReadUInt(),
				Revision = r.ReadUInt(),
				Reason = r.ReadByte()
			};
		}
		EntityDespawnBatchMessage result = default(EntityDespawnBatchMessage);
		result.Epoch = epoch;
		result.ServerTick = tick;
		result.Scene = scene;
		result.Entities = array;
		return result;
	}

	private static void WriteInventoryRequest(NetworkWriter w, InventoryTransactionRequest m)
	{
		w.WriteUInt(m.Epoch);
		w.WriteUInt(m.NetworkId);
		w.WriteUInt(m.ExpectedRevision);
		w.WriteUInt(m.OperationId);
		WriteSlots(w, m.Slots);
	}

	private static InventoryTransactionRequest ReadInventoryRequest(NetworkReader r)
	{
		InventoryTransactionRequest result = default(InventoryTransactionRequest);
		result.Epoch = r.ReadUInt();
		result.NetworkId = r.ReadUInt();
		result.ExpectedRevision = r.ReadUInt();
		result.OperationId = r.ReadUInt();
		result.Slots = ReadSlots(r);
		return result;
	}

	private static void WriteInventoryState(NetworkWriter w, InventoryStateMessage m)
	{
		w.WriteUInt(m.Epoch);
		w.WriteUInt(m.ServerTick);
		w.WriteUInt(m.NetworkId);
		w.WriteUInt(m.Revision);
		w.WriteUInt(m.OperationId);
		w.WriteBool(m.Accepted);
		WriteSlots(w, m.Slots);
	}

	private static InventoryStateMessage ReadInventoryState(NetworkReader r)
	{
		InventoryStateMessage result = default(InventoryStateMessage);
		result.Epoch = r.ReadUInt();
		result.ServerTick = r.ReadUInt();
		result.NetworkId = r.ReadUInt();
		result.Revision = r.ReadUInt();
		result.OperationId = r.ReadUInt();
		result.Accepted = r.ReadBool();
		result.Slots = ReadSlots(r);
		return result;
	}

	private static void WriteAction(NetworkWriter w, EntityActionCommand m)
	{
		w.WriteUInt(m.Epoch);
		w.WriteUInt(m.NetworkId);
		w.WriteUInt(m.Sequence);
		w.WriteUInt(m.ClientTick);
		w.WriteByte(m.Action);
		w.WriteFloat(m.Amount);
		w.WriteBool(m.BoolValue);
		w.WriteVector3(m.Direction);
	}

	private static EntityActionCommand ReadAction(NetworkReader r)
	{
		EntityActionCommand result = default(EntityActionCommand);
		result.Epoch = r.ReadUInt();
		result.NetworkId = r.ReadUInt();
		result.Sequence = r.ReadUInt();
		result.ClientTick = r.ReadUInt();
		result.Action = r.ReadByte();
		result.Amount = r.ReadFloat();
		result.BoolValue = r.ReadBool();
		result.Direction = r.ReadVector3();
		return result;
	}

	private static void WriteDigest(NetworkWriter w, ReplicationDigestMessage m)
	{
		WriteHeader(w, m.Epoch, m.ServerTick, m.Scene);
		w.WriteInt(m.Count);
		w.WriteULong(m.Digest);
	}

	private static ReplicationDigestMessage ReadDigest(NetworkReader r)
	{
		ReadHeader(r, out var epoch, out var tick, out var scene);
		ReplicationDigestMessage result = default(ReplicationDigestMessage);
		result.Epoch = epoch;
		result.ServerTick = tick;
		result.Scene = scene;
		result.Count = r.ReadInt();
		result.Digest = r.ReadULong();
		return result;
	}

	private static void WriteState(NetworkWriter w, WorldEntityState s, ushort mask)
	{
		if (((uint)mask & (true ? 1u : 0u)) != 0)
		{
			w.WriteVector3(s.Position);
			w.WriteQuaternion(s.Rotation);
		}
		if ((mask & 2u) != 0)
		{
			w.WriteFloat(s.Health);
		}
		if ((mask & 4u) != 0)
		{
			w.WriteInt(s.StateA);
			w.WriteInt(s.StateB);
			w.WriteByte(s.Flags);
		}
		if ((mask & 8u) != 0)
		{
			w.WriteString(s.Animation);
			w.WriteInt(s.Frame);
		}
	}

	private static WorldEntityState ReadState(NetworkReader r, ushort mask)
	{
		WorldEntityState result = new WorldEntityState
		{
			Animation = string.Empty
		};
		if (((uint)mask & (true ? 1u : 0u)) != 0)
		{
			result.Position = r.ReadVector3();
			result.Rotation = r.ReadQuaternion();
		}
		if ((mask & 2u) != 0)
		{
			result.Health = r.ReadFloat();
		}
		if ((mask & 4u) != 0)
		{
			result.StateA = r.ReadInt();
			result.StateB = r.ReadInt();
			result.Flags = r.ReadByte();
		}
		if ((mask & 8u) != 0)
		{
			result.Animation = r.ReadString();
			result.Frame = r.ReadInt();
		}
		return result;
	}

	private static void WriteSlots(NetworkWriter w, InventorySlotWire[] slots)
	{
		int num = ((slots != null) ? slots.Length : 0);
		w.WriteInt(num);
		for (int i = 0; i < num; i++)
		{
			InventorySlotWire inventorySlotWire = slots[i];
			w.WriteString(inventorySlotWire.Type);
			w.WriteInt(inventorySlotWire.Amount);
			w.WriteFloat(inventorySlotWire.Durability);
			w.WriteInt(inventorySlotWire.Quality);
			w.WriteBool(inventorySlotWire.Recipe);
		}
	}

	private static InventorySlotWire[] ReadSlots(NetworkReader r)
	{
		int num = ReadCount(r, 128, "inventory slot");
		InventorySlotWire[] array = new InventorySlotWire[num];
		for (int i = 0; i < num; i++)
		{
			array[i] = new InventorySlotWire
			{
				Type = r.ReadString(),
				Amount = r.ReadInt(),
				Durability = r.ReadFloat(),
				Quality = r.ReadInt(),
				Recipe = r.ReadBool()
			};
		}
		return array;
	}

	private static void WriteHeader(NetworkWriter w, uint epoch, uint tick, string scene)
	{
		w.WriteUInt(epoch);
		w.WriteUInt(tick);
		w.WriteString(scene);
	}

	private static void ReadHeader(NetworkReader r, out uint epoch, out uint tick, out string scene)
	{
		epoch = r.ReadUInt();
		tick = r.ReadUInt();
		scene = r.ReadString();
	}

	private static int ReadCount(NetworkReader r, int maximum, string label)
	{
		int num = r.ReadInt();
		if (num < 0 || num > maximum)
		{
			throw new InvalidOperationException("Invalid " + label + " count: " + num);
		}
		return num;
	}
}
