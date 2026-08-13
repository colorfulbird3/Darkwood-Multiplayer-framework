using System;

namespace DarkwoodMultiplayerFramework;

[Flags]
public enum EntityDirtyMask : ushort
{
	None = 0,
	Transform = 1,
	Vitals = 2,
	State = 4,
	Animation = 8,
	All = 0xF
}
