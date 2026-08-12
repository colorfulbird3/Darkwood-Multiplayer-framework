using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace DarkwoodMultiplayerFramework.Network;

public sealed class ChunkTransferAssembler
{
    private readonly byte[][] chunks;
    private readonly byte[] expectedHash;
    private readonly long expectedBytes;
    private long receivedBytes;
    private int receivedChunks;
    public ChunkTransferAssembler(Guid id, long totalBytes, int chunkCount, byte[] expectedHash, long maxBytes = 128L*1024*1024)
    {
        if(id==Guid.Empty||totalBytes<=0||totalBytes>maxBytes||chunkCount<=0||chunkCount>4096)throw new InvalidDataException("Invalid transfer manifest.");
        if(expectedHash==null||expectedHash.Length!=32)throw new InvalidDataException("Invalid transfer hash.");
        Id=id; expectedBytes=totalBytes; chunks=new byte[chunkCount][]; this.expectedHash=expectedHash;
    }
    public Guid Id { get; }
    public int ReceivedChunks => receivedChunks;
    public int ChunkCount => chunks.Length;
    public bool IsComplete => receivedChunks==chunks.Length;
    public void Add(Guid id,int index,int total,byte[] data,byte[] chunkHash)
    {
        if(id!=Id||total!=chunks.Length||index<0||index>=chunks.Length||data==null||data.Length>256*1024)throw new InvalidDataException("Invalid transfer chunk.");
        if(chunkHash==null||chunkHash.Length!=32||!FixedEquals(Hash(data),chunkHash))throw new InvalidDataException("Transfer chunk hash mismatch.");
        if(chunks[index]!=null)return;
        chunks[index]=data;receivedChunks++;receivedBytes+=data.Length;
        if(receivedBytes>expectedBytes)throw new InvalidDataException("Transfer exceeds declared size.");
    }
    public byte[] Build()
    {
        if(!IsComplete||receivedBytes!=expectedBytes||expectedBytes>int.MaxValue)throw new InvalidDataException("Transfer is incomplete.");
        var output=new byte[(int)expectedBytes];var offset=0;foreach(var chunk in chunks){Buffer.BlockCopy(chunk,0,output,offset,chunk.Length);offset+=chunk.Length;}
        if(!FixedEquals(Hash(output),expectedHash))throw new InvalidDataException("Transfer SHA-256 mismatch.");return output;
    }
    public static byte[][] Split(byte[] data,int size=128*1024){if(data==null||data.Length==0)throw new ArgumentException("Transfer data is empty.");var count=(data.Length+size-1)/size;var chunks=new byte[count][];for(var i=0;i<count;i++){var n=Math.Min(size,data.Length-i*size);chunks[i]=new byte[n];Buffer.BlockCopy(data,i*size,chunks[i],0,n);}return chunks;}
    public static byte[] Hash(byte[] data){using var sha=SHA256.Create();return sha.ComputeHash(data);}
    private static bool FixedEquals(byte[] a,byte[] b){if(a.Length!=b.Length)return false;var diff=0;for(var i=0;i<a.Length;i++)diff|=a[i]^b[i];return diff==0;}
}
