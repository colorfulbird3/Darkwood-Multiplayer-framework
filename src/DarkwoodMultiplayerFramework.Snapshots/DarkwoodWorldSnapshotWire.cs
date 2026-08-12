using System;
using System.IO;
using System.Text;

namespace DarkwoodMultiplayerFramework.Snapshots;

public sealed class WorldSnapshotWire
{
    public WorldSnapshotWire(string scene,string registryDigest,long serverTick,byte[][] entityRecords,byte[][] inventoryRecords){Scene=scene??string.Empty;RegistryDigest=registryDigest??string.Empty;ServerTick=serverTick;EntityRecords=entityRecords??Array.Empty<byte[]>();InventoryRecords=inventoryRecords??Array.Empty<byte[]>();}
    public string Scene{get;} public string RegistryDigest{get;} public long ServerTick{get;} public byte[][] EntityRecords{get;} public byte[][] InventoryRecords{get;}
}

public static class WorldSnapshotWireCodec
{
    private const int Schema=2,MaxRecords=4096,MaxRecord=512*1024,MaxPayload=64*1024*1024,MaxString=4096;
    public static byte[] Encode(WorldSnapshotWire snapshot){if(snapshot.EntityRecords.Length>MaxRecords||snapshot.InventoryRecords.Length>MaxRecords)throw new InvalidOperationException("World snapshot exceeds record limits.");using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8);writer.Write(Schema);WriteString(writer,snapshot.Scene);WriteString(writer,snapshot.RegistryDigest);writer.Write(snapshot.ServerTick);WriteRecords(writer,snapshot.EntityRecords);WriteRecords(writer,snapshot.InventoryRecords);if(stream.Length>MaxPayload)throw new InvalidOperationException("World snapshot exceeds 64 MB.");return stream.ToArray();}
    public static WorldSnapshotWire Decode(byte[] data){if(data==null||data.Length==0||data.Length>MaxPayload)throw new InvalidDataException("World snapshot size is invalid.");using var stream=new MemoryStream(data,false);using var reader=new BinaryReader(stream,Encoding.UTF8);if(reader.ReadInt32()!=Schema)throw new InvalidDataException("World snapshot schema mismatch.");var result=new WorldSnapshotWire(ReadString(reader),ReadString(reader),reader.ReadInt64(),ReadRecords(reader),ReadRecords(reader));if(stream.Position!=stream.Length)throw new InvalidDataException("World snapshot contains trailing data.");return result;}
    private static void WriteRecords(BinaryWriter writer,byte[][] records){writer.Write(records.Length);foreach(var record in records){if(record==null||record.Length>MaxRecord)throw new InvalidOperationException("World snapshot record is invalid.");writer.Write(record.Length);writer.Write(record);}}
    private static byte[][] ReadRecords(BinaryReader reader){var count=ReadCount(reader,MaxRecords);var result=new byte[count][];for(var i=0;i<count;i++)result[i]=ReadExact(reader,ReadCount(reader,MaxRecord));return result;}
    private static void WriteString(BinaryWriter writer,string value){var bytes=Encoding.UTF8.GetBytes(value??string.Empty);if(bytes.Length>MaxString)throw new InvalidOperationException("World snapshot string is too long.");writer.Write(bytes.Length);writer.Write(bytes);}
    private static string ReadString(BinaryReader reader)=>Encoding.UTF8.GetString(ReadExact(reader,ReadCount(reader,MaxString)));
    private static int ReadCount(BinaryReader reader,int max){var value=reader.ReadInt32();if(value<0||value>max)throw new InvalidDataException("World snapshot count exceeds limit.");return value;}
    private static byte[] ReadExact(BinaryReader reader,int length){var value=reader.ReadBytes(length);if(value.Length!=length)throw new EndOfStreamException();return value;}
}
