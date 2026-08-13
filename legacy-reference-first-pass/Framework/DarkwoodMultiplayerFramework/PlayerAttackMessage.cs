using Mirror;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

public struct PlayerAttackMessage : NetworkMessage
{
	public int PlayerId;

	public uint Sequence;

	public byte Kind;

	public Vector3 Position;

	public Vector3 Direction;

	public string Scene;
}
