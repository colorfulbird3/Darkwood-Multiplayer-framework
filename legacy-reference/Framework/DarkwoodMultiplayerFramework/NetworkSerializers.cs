using Mirror;

namespace DarkwoodMultiplayerFramework;

internal static class NetworkSerializers
{
	public static void Install()
	{
		Writer<PlayerPoseMessage>.write = WritePose;
		Reader<PlayerPoseMessage>.read = ReadPose;
		Writer<PlayerAttackMessage>.write = WriteAttack;
		Reader<PlayerAttackMessage>.read = ReadAttack;
		Writer<ClientIdentityMessage>.write = delegate(NetworkWriter w, ClientIdentityMessage m)
		{
			w.WriteInt(m.PlayerId);
		};
		Reader<ClientIdentityMessage>.read = delegate(NetworkReader r)
		{
			ClientIdentityMessage result = default(ClientIdentityMessage);
			result.PlayerId = r.ReadInt();
			return result;
		};
		Writer<HostSceneMessage>.write = WriteScene;
		Reader<HostSceneMessage>.read = ReadScene;
	}

	private static void WritePose(NetworkWriter w, PlayerPoseMessage m)
	{
		w.WriteInt(m.PlayerId);
		w.WriteUInt(m.Sequence);
		w.WriteVector3(m.Position);
		w.WriteQuaternion(m.Rotation);
		w.WriteByte(m.Flags);
		w.WriteString(m.Scene);
		w.WriteString(m.TorsoClip);
		w.WriteInt(m.TorsoFrame);
		w.WriteString(m.LegsClip);
		w.WriteInt(m.LegsFrame);
	}

	private static PlayerPoseMessage ReadPose(NetworkReader r)
	{
		PlayerPoseMessage result = default(PlayerPoseMessage);
		result.PlayerId = r.ReadInt();
		result.Sequence = r.ReadUInt();
		result.Position = r.ReadVector3();
		result.Rotation = r.ReadQuaternion();
		result.Flags = r.ReadByte();
		result.Scene = r.ReadString();
		result.TorsoClip = r.ReadString();
		result.TorsoFrame = r.ReadInt();
		result.LegsClip = r.ReadString();
		result.LegsFrame = r.ReadInt();
		return result;
	}

	private static void WriteAttack(NetworkWriter w, PlayerAttackMessage m)
	{
		w.WriteInt(m.PlayerId);
		w.WriteUInt(m.Sequence);
		w.WriteByte(m.Kind);
		w.WriteVector3(m.Position);
		w.WriteVector3(m.Direction);
		w.WriteString(m.Scene);
	}

	private static PlayerAttackMessage ReadAttack(NetworkReader r)
	{
		PlayerAttackMessage result = default(PlayerAttackMessage);
		result.PlayerId = r.ReadInt();
		result.Sequence = r.ReadUInt();
		result.Kind = r.ReadByte();
		result.Position = r.ReadVector3();
		result.Direction = r.ReadVector3();
		result.Scene = r.ReadString();
		return result;
	}

	private static void WriteScene(NetworkWriter w, HostSceneMessage m)
	{
		w.WriteUInt(m.Revision);
		w.WriteString(m.Scene);
		w.WriteInt(m.BuildIndex);
	}

	private static HostSceneMessage ReadScene(NetworkReader r)
	{
		HostSceneMessage result = default(HostSceneMessage);
		result.Revision = r.ReadUInt();
		result.Scene = r.ReadString();
		result.BuildIndex = r.ReadInt();
		return result;
	}
}
