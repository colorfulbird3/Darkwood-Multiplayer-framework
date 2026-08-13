using Mirror;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

public struct EntityActionRequest : NetworkMessage
{
	public ulong Id;

	public byte Action;

	public float Amount;

	public bool BoolValue;

	public Vector3 Direction;
}
