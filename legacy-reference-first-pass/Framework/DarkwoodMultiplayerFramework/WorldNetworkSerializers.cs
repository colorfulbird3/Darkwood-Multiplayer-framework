using System;
using Mirror;

namespace DarkwoodMultiplayerFramework;

internal static class WorldNetworkSerializers
{
	public static void Install()
	{
		Writer<WorldStateBatchMessage>.write = WriteBatch;
		Reader<WorldStateBatchMessage>.read = ReadBatch;
		Writer<EntityRegistryDigestMessage>.write = delegate(NetworkWriter w, EntityRegistryDigestMessage x)
		{
			w.WriteString(x.Scene);
			w.WriteInt(x.Count);
			w.WriteULong(x.Digest);
		};
		Reader<EntityRegistryDigestMessage>.read = delegate(NetworkReader r)
		{
			EntityRegistryDigestMessage result2 = default(EntityRegistryDigestMessage);
			result2.Scene = r.ReadString();
			result2.Count = r.ReadInt();
			result2.Digest = r.ReadULong();
			return result2;
		};
		Writer<WorldSnapshotRequest>.write = delegate(NetworkWriter w, WorldSnapshotRequest x)
		{
			w.WriteString(x.Scene);
			w.WriteULong(x.RegistryDigest);
		};
		Reader<WorldSnapshotRequest>.read = delegate(NetworkReader r)
		{
			WorldSnapshotRequest result = default(WorldSnapshotRequest);
			result.Scene = r.ReadString();
			result.RegistryDigest = r.ReadULong();
			return result;
		};
		Writer<EntityActionRequest>.write = WriteAction;
		Reader<EntityActionRequest>.read = ReadAction;
		Writer<InventorySnapshotMessage>.write = WriteInventory;
		Reader<InventorySnapshotMessage>.read = ReadInventory;
		Writer<InventoryMutationRequest>.write = WriteMutation;
		Reader<InventoryMutationRequest>.read = ReadMutation;
	}

	private static void WriteState(NetworkWriter w, WorldEntityState s)
	{
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		w.WriteULong(s.Id);
		w.WriteByte(s.Kind);
		w.WriteVector3(s.Position);
		w.WriteQuaternion(s.Rotation);
		w.WriteFloat(s.Health);
		w.WriteInt(s.StateA);
		w.WriteInt(s.StateB);
		w.WriteByte(s.Flags);
		w.WriteString(s.Animation);
		w.WriteInt(s.Frame);
	}

	private static WorldEntityState ReadState(NetworkReader r)
	{
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		WorldEntityState result = default(WorldEntityState);
		result.Id = r.ReadULong();
		result.Kind = r.ReadByte();
		result.Position = r.ReadVector3();
		result.Rotation = r.ReadQuaternion();
		result.Health = r.ReadFloat();
		result.StateA = r.ReadInt();
		result.StateB = r.ReadInt();
		result.Flags = r.ReadByte();
		result.Animation = r.ReadString();
		result.Frame = r.ReadInt();
		return result;
	}

	private static void WriteBatch(NetworkWriter w, WorldStateBatchMessage m)
	{
		w.WriteUInt(m.Revision);
		w.WriteString(m.Scene);
		int num = ((m.States != null) ? m.States.Length : 0);
		w.WriteInt(num);
		for (int i = 0; i < num; i++)
		{
			WriteState(w, m.States[i]);
		}
	}

	private static WorldStateBatchMessage ReadBatch(NetworkReader r)
	{
		uint revision = r.ReadUInt();
		string scene = r.ReadString();
		int num = r.ReadInt();
		if (num < 0 || num > 256)
		{
			throw new InvalidOperationException("Invalid entity batch size: " + num);
		}
		WorldEntityState[] array = new WorldEntityState[num];
		for (int i = 0; i < num; i++)
		{
			array[i] = ReadState(r);
		}
		WorldStateBatchMessage result = default(WorldStateBatchMessage);
		result.Revision = revision;
		result.Scene = scene;
		result.States = array;
		return result;
	}

	private static void WriteAction(NetworkWriter w, EntityActionRequest m)
	{
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		w.WriteULong(m.Id);
		w.WriteByte(m.Action);
		w.WriteFloat(m.Amount);
		w.WriteBool(m.BoolValue);
		w.WriteVector3(m.Direction);
	}

	private static EntityActionRequest ReadAction(NetworkReader r)
	{
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		EntityActionRequest result = default(EntityActionRequest);
		result.Id = r.ReadULong();
		result.Action = r.ReadByte();
		result.Amount = r.ReadFloat();
		result.BoolValue = r.ReadBool();
		result.Direction = r.ReadVector3();
		return result;
	}

	private static void WriteSlot(NetworkWriter w, InventorySlotWire s)
	{
		w.WriteString(s.Type);
		w.WriteInt(s.Amount);
		w.WriteFloat(s.Durability);
		w.WriteInt(s.Quality);
		w.WriteBool(s.Recipe);
	}

	private static InventorySlotWire ReadSlot(NetworkReader r)
	{
		InventorySlotWire result = default(InventorySlotWire);
		result.Type = r.ReadString();
		result.Amount = r.ReadInt();
		result.Durability = r.ReadFloat();
		result.Quality = r.ReadInt();
		result.Recipe = r.ReadBool();
		return result;
	}

	private static void WriteInventory(NetworkWriter w, InventorySnapshotMessage m)
	{
		w.WriteULong(m.Id);
		w.WriteUInt(m.Revision);
		WriteSlots(w, m.Slots);
	}

	private static InventorySnapshotMessage ReadInventory(NetworkReader r)
	{
		InventorySnapshotMessage result = default(InventorySnapshotMessage);
		result.Id = r.ReadULong();
		result.Revision = r.ReadUInt();
		result.Slots = ReadSlots(r);
		return result;
	}

	private static void WriteMutation(NetworkWriter w, InventoryMutationRequest m)
	{
		w.WriteULong(m.Id);
		WriteSlots(w, m.Slots);
	}

	private static InventoryMutationRequest ReadMutation(NetworkReader r)
	{
		InventoryMutationRequest result = default(InventoryMutationRequest);
		result.Id = r.ReadULong();
		result.Slots = ReadSlots(r);
		return result;
	}

	private static void WriteSlots(NetworkWriter w, InventorySlotWire[] slots)
	{
		int num = ((slots != null) ? slots.Length : 0);
		w.WriteInt(num);
		for (int i = 0; i < num; i++)
		{
			WriteSlot(w, slots[i]);
		}
	}

	private static InventorySlotWire[] ReadSlots(NetworkReader r)
	{
		int num = r.ReadInt();
		if (num < 0 || num > 128)
		{
			throw new InvalidOperationException("Invalid inventory slot count: " + num);
		}
		InventorySlotWire[] array = new InventorySlotWire[num];
		for (int i = 0; i < num; i++)
		{
			array[i] = ReadSlot(r);
		}
		return array;
	}
}
