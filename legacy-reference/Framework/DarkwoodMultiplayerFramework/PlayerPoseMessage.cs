using Mirror;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

public struct PlayerPoseMessage : NetworkMessage
{
	public int PlayerId;

	public uint Sequence;

	public Vector3 Position;

	public Quaternion Rotation;

	public byte Flags;

	public string Scene;

	public string TorsoClip;

	public int TorsoFrame;

	public string LegsClip;

	public int LegsFrame;
}
