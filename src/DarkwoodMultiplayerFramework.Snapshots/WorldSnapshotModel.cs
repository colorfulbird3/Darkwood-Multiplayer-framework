using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Snapshots;

public sealed class WorldSnapshotModel
{
    public WorldSnapshotModel(string scene, string registryDigest, long serverTick, SnapshotEntityState[] entities)
    { Scene=scene??string.Empty; RegistryDigest=registryDigest??string.Empty; ServerTick=serverTick; Entities=entities??Array.Empty<SnapshotEntityState>(); }
    public string Scene { get; } public string RegistryDigest { get; } public long ServerTick { get; } public SnapshotEntityState[] Entities { get; }
    public byte[] Serialize() => WorldSnapshotCodec.Encode(this);
    public static WorldSnapshotModel Deserialize(byte[] data) => WorldSnapshotCodec.Decode(data);
}

public readonly struct SnapshotEntityState
{
    public SnapshotEntityState(ulong value, bool persistent, byte kind, float x, float y, float z, float qx, float qy, float qz, float qw, float health, int stateA, int stateB, byte flags, string animation, int frame, ulong revision)
    { Value=value; Persistent=persistent; Kind=kind; X=x; Y=y; Z=z; Qx=qx; Qy=qy; Qz=qz; Qw=qw; Health=health; StateA=stateA; StateB=stateB; Flags=flags; Animation=animation??string.Empty; Frame=frame; Revision=revision; }
    public ulong Value { get; } public bool Persistent { get; } public byte Kind { get; } public float X { get; } public float Y { get; } public float Z { get; } public float Qx { get; } public float Qy { get; } public float Qz { get; } public float Qw { get; } public float Health { get; } public int StateA { get; } public int StateB { get; } public byte Flags { get; } public string Animation { get; } public int Frame { get; } public ulong Revision { get; }
}

public static class WorldSnapshotCodec
{
    public static byte[] Encode(WorldSnapshotModel m)
    {
        if(m.Entities.Length>4096)throw new InvalidOperationException("World snapshot has too many entities.");
        using var s=new MemoryStream();using var w=new BinaryWriter(s,Encoding.UTF8);w.Write(1);WriteString(w,m.Scene);WriteString(w,m.RegistryDigest);w.Write(m.ServerTick);w.Write(m.Entities.Length);
        foreach(var e in m.Entities){w.Write(e.Value);w.Write(e.Persistent);w.Write(e.Kind);w.Write(e.X);w.Write(e.Y);w.Write(e.Z);w.Write(e.Qx);w.Write(e.Qy);w.Write(e.Qz);w.Write(e.Qw);w.Write(e.Health);w.Write(e.StateA);w.Write(e.StateB);w.Write(e.Flags);WriteString(w,e.Animation);w.Write(e.Frame);w.Write(e.Revision);}return s.ToArray();
    }
    public static WorldSnapshotModel Decode(byte[] data)
    {
        using var s=new MemoryStream(data??Array.Empty<byte>());using var r=new BinaryReader(s,Encoding.UTF8);if(r.ReadInt32()!=1)throw new InvalidDataException("World snapshot schema mismatch.");var scene=ReadString(r);var digest=ReadString(r);var tick=r.ReadInt64();var n=r.ReadInt32();if(n<0||n>4096)throw new InvalidDataException("World snapshot entity count is invalid.");var a=new SnapshotEntityState[n];for(var i=0;i<n;i++)a[i]=new SnapshotEntityState(r.ReadUInt64(),r.ReadBoolean(),r.ReadByte(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadInt32(),r.ReadInt32(),r.ReadByte(),ReadString(r),r.ReadInt32(),r.ReadUInt64());if(s.Position!=s.Length)throw new InvalidDataException("World snapshot has trailing data.");return new WorldSnapshotModel(scene,digest,tick,a);
    }
    private static void WriteString(BinaryWriter w,string v){var b=Encoding.UTF8.GetBytes(v??string.Empty);if(b.Length>4096)throw new InvalidOperationException("World snapshot string is too long.");w.Write(b.Length);w.Write(b);}
    private static string ReadString(BinaryReader r){var n=r.ReadInt32();if(n<0||n>4096)throw new InvalidDataException("World snapshot string is invalid.");var b=r.ReadBytes(n);if(b.Length!=n)throw new EndOfStreamException();return Encoding.UTF8.GetString(b);}
}
