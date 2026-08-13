using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public static class DarkwoodSaveBundle
{
    private const int Magic=0x44534650,Schema=3,MaxFiles=24; private const long MaxBytes=128L*1024*1024;
    private static readonly string[] Files={"sav.dat","sav.dat_bak","savs.dat","savs.dat_bak","savch.dat","savch.dat_bak"};
    private static readonly System.Text.RegularExpressions.Regex GraphField=new System.Text.RegularExpressions.Regex("(\"graph\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"",System.Text.RegularExpressions.RegexOptions.Compiled);
    /// <summary>客户端专用打包：剥离 savs.dat 中的 A* 导航图（实测约占总文件 63%），
    /// 客户端是视觉镜像（怪物 AI 冻结、无本地寻路），大幅缩短弱机 JSON 解析时间。</summary>
    public static byte[] BuildForClient(string baseDirectory,int profileId)=>BuildInternal(baseDirectory,profileId,StripGraphs);
    public static byte[] Build(string baseDirectory,int profileId)=>BuildInternal(baseDirectory,profileId,null);
    private static byte[] BuildInternal(string baseDirectory,int profileId,System.Func<string,byte[],byte[]>? transform)
    {
        ValidateProfile(profileId);var list=new List<KeyValuePair<string,byte[]>>();Add(list,baseDirectory,"profs.dat",true,transform);Add(list,baseDirectory,"profs.dat_bak",false,transform);Add(list,baseDirectory,"playerState.dat",false,transform);var profile=Path.Combine(baseDirectory,"prof"+profileId);foreach(var f in Files)Add(list,profile,Path.Combine("prof"+profileId,f),false,transform,f);
        if(!list.Any(x=>x.Key.EndsWith("sav.dat",StringComparison.OrdinalIgnoreCase))||!list.Any(x=>x.Key.EndsWith("savs.dat",StringComparison.OrdinalIgnoreCase)))throw new FileNotFoundException("Active profile save files are missing.");
        using var output=new MemoryStream();using(var gzip=new GZipStream(output,CompressionLevel.Optimal,true))using(var writer=new BinaryWriter(gzip,Encoding.UTF8,true)){writer.Write(Magic);writer.Write(Schema);writer.Write(profileId);writer.Write(list.Count);foreach(var file in list){writer.Write(file.Key.Replace('\\','/'));writer.Write(file.Value.Length);writer.Write(file.Value);}}return output.ToArray();
    }
    private static byte[] StripGraphs(string relative,byte[] data)
    {
        var normalized=relative.Replace('/','\\');
        var name=Path.GetFileName(normalized);
        if(!name.Equals("savs.dat",StringComparison.OrdinalIgnoreCase)&&!name.Equals("savs.dat_bak",StringComparison.OrdinalIgnoreCase))return data;
        var text=Encoding.UTF8.GetString(data);
        if(text.IndexOf("\"graph\"",System.StringComparison.Ordinal)<0)return data; // 加密或格式异常：原样传输
        var replaced=GraphField.Replace(text,"$1\"\"");
        return Encoding.UTF8.GetBytes(replaced);
    }
    public static int Extract(byte[] bundle,string destination)
    {
        Directory.CreateDirectory(destination);using var input=new MemoryStream(bundle,false);using var gzip=new GZipStream(input,CompressionMode.Decompress);using var reader=new BinaryReader(gzip,Encoding.UTF8);if(reader.ReadInt32()!=Magic||reader.ReadInt32()!=Schema)throw new InvalidDataException("Save bundle header mismatch.");var profile=reader.ReadInt32();var count=reader.ReadInt32();ValidateProfile(profile);if(count<=0||count>MaxFiles)throw new InvalidDataException("Save bundle file count is invalid.");var root=Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;long total=0;for(var i=0;i<count;i++){var relative=reader.ReadString().Replace('/',Path.DirectorySeparatorChar);if(!Allowed(relative,profile))throw new InvalidDataException("Rejected save path: "+relative);var length=reader.ReadInt32();total+=length;if(length<0||total>MaxBytes)throw new InvalidDataException("Extracted save size is invalid.");var data=reader.ReadBytes(length);if(data.Length!=length)throw new EndOfStreamException(relative);var path=Path.GetFullPath(Path.Combine(root,relative));if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Save path escaped destination.");var parent=Path.GetDirectoryName(path);if(parent==null)throw new InvalidDataException("Save path has no parent.");Directory.CreateDirectory(parent);File.WriteAllBytes(path,data);}return profile;
    }
    private static void Add(List<KeyValuePair<string,byte[]>> list,string directory,string relative,bool required,System.Func<string,byte[],byte[]>? transform,string? fileName=null){var path=Path.Combine(directory,fileName??relative);if(!File.Exists(path)){if(required)throw new FileNotFoundException("Required save file missing.",path);return;}using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);if(stream.Length<0||stream.Length>MaxBytes)throw new InvalidDataException("Save file too large.");var data=new byte[stream.Length];var offset=0;while(offset<data.Length){var read=stream.Read(data,offset,data.Length-offset);if(read<=0)throw new EndOfStreamException(path);offset+=read;}if(transform!=null)data=transform(relative,data);list.Add(new KeyValuePair<string,byte[]>(relative,data));}
    private static bool Allowed(string path,int profile){if(path.Equals("profs.dat",StringComparison.OrdinalIgnoreCase)||path.Equals("profs.dat_bak",StringComparison.OrdinalIgnoreCase)||path.Equals("playerState.dat",StringComparison.OrdinalIgnoreCase))return true;var prefix="prof"+profile+Path.DirectorySeparatorChar;if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))return false;var name=path.Substring(prefix.Length);return Files.Any(x=>x.Equals(name,StringComparison.OrdinalIgnoreCase));}
    private static void ValidateProfile(int id){if(id<1||id>5)throw new InvalidDataException("Invalid profile id.");}
}
