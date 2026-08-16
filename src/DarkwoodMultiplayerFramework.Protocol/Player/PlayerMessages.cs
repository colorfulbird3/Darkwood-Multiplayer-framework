using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Protocol;

public readonly struct PlayerPoseMessage
{
    public PlayerPoseMessage(int playerId,uint sequence,string scene,float x,float y,float z,float qx,float qy,float qz,float qw,float maxHealth,byte flags,string torsoClip,int torsoFrame,string legsClip,int legsFrame)
    { PlayerId=playerId;Sequence=sequence;Scene=scene??string.Empty;X=x;Y=y;Z=z;Qx=qx;Qy=qy;Qz=qz;Qw=qw;MaxHealth=maxHealth;Flags=flags;TorsoClip=torsoClip??string.Empty;TorsoFrame=torsoFrame;LegsClip=legsClip??string.Empty;LegsFrame=legsFrame; }
    public int PlayerId {get;} public uint Sequence {get;} public string Scene {get;} public float X {get;} public float Y {get;} public float Z {get;} public float Qx {get;} public float Qy {get;} public float Qz {get;} public float Qw {get;}
    /// <summary>Sender's maximum health, used by the host for revive scaling (downed flag is bit 4 of Flags).</summary>
    public float MaxHealth {get;} public byte Flags {get;} public string TorsoClip {get;} public int TorsoFrame {get;} public string LegsClip {get;} public int LegsFrame {get;}
}

/// <summary>Player flags carried in PlayerPoseMessage.</summary>
public static class PlayerPoseFlags
{
    public const byte Walking = 1;
    public const byte Running = 2;
    public const byte Aiming = 4;
    public const byte Attacking = 8;
    public const byte Downed = 16;
}

public readonly struct PlayerHealthMessage
{
    public PlayerHealthMessage(int playerId, float health, float maxHealth, bool downed)
    { PlayerId=playerId; Health=health; MaxHealth=maxHealth; Downed=downed; }
    public int PlayerId {get;} public float Health {get;} public float MaxHealth {get;} public bool Downed {get;}
}

/// <summary>Rescue intent from a living player. The host picks the nearest downed player within range.</summary>
