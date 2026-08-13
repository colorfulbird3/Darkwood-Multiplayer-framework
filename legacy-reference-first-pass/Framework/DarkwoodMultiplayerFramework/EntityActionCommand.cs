using Mirror;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

public struct EntityActionCommand : NetworkMessage
{
	public uint Epoch;

	public uint NetworkId;

	public uint Sequence;

	public uint ClientTick;

	public byte Action;

	public float Amount;

	public bool BoolValue;

	public Vector3 Direction;
}
