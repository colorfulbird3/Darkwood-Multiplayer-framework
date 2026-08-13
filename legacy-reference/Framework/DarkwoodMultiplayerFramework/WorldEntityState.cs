using UnityEngine;

namespace DarkwoodMultiplayerFramework;

public struct WorldEntityState
{
	public ulong Id;

	public byte Kind;

	public Vector3 Position;

	public Quaternion Rotation;

	public float Health;

	public int StateA;

	public int StateB;

	public byte Flags;

	public string Animation;

	public int Frame;
}
