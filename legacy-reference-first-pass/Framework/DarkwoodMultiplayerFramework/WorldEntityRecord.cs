using UnityEngine;

namespace DarkwoodMultiplayerFramework;

internal sealed class WorldEntityRecord
{
	public ulong Id;

	public WorldEntityKind Kind;

	public Component Component;

	public string Signature;
}
