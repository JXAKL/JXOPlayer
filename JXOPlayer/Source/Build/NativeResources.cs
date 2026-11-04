using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class NativeResources
{
    delegate bool ResourceNameCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    delegate bool ResourceLanguageCallback(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern bool EnumResourceNames(IntPtr module, IntPtr type, ResourceNameCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern bool EnumResourceLanguages(IntPtr module, IntPtr type, IntPtr name, ResourceLanguageCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr BeginUpdateResource(string path, bool deleteExisting);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool EndUpdateResource(IntPtr update, bool discard);

    static void Align(BinaryWriter writer) { while(writer.BaseStream.Position % 4 != 0) writer.Write((byte)0); }
    static byte[] Node(string key, ushort type, byte[] value, ushort valueLength, params byte[][] children)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.Unicode);
        writer.Write((ushort)0); writer.Write(valueLength); writer.Write(type);
        writer.Write(Encoding.Unicode.GetBytes(key + "\0")); Align(writer);
        writer.Write(value);
        foreach(var child in children) { Align(writer); writer.Write(child); }
        var length=(ushort)stream.Length; stream.Position=0; writer.Write(length); return stream.ToArray();
    }
    static byte[] Version(string filename)
    {
        var fields = new Dictionary<string,string> {
            ["CompanyName"]="JXOPlayer", ["FileDescription"]="JXOPlayer 2.0", ["FileVersion"]="2.0.0.0",
            ["InternalName"]="JXOPlayer", ["OriginalFilename"]=filename, ["ProductName"]="JXOPlayer 2.0", ["ProductVersion"]="2.0.0.0"
        };
        var strings=new List<byte[]>();
        foreach(var pair in fields) strings.Add(Node(pair.Key,1,Encoding.Unicode.GetBytes(pair.Value+"\0"),(ushort)(pair.Value.Length+1)));
        var stringInfo=Node("StringFileInfo",1,Array.Empty<byte>(),0,Node("040904B0",1,Array.Empty<byte>(),0,strings.ToArray()));
        var varInfo=Node("VarFileInfo",1,Array.Empty<byte>(),0,Node("Translation",0,new byte[]{9,4,176,4},4));
        using var fixedStream=new MemoryStream(); using var writer=new BinaryWriter(fixedStream);
        foreach(uint value in new uint[]{0xFEEF04BD,0x00010000,0x00020000,0,0x00020000,0,0x3F,0,0x00040004,1,0,0,0}) writer.Write(value);
        return Node("VS_VERSION_INFO",0,fixedStream.ToArray(),52,stringInfo,varInfo);
    }
    public static void Apply(string path, string icon)
    {
        var old=new List<(int Type,IntPtr Name,ushort Language)>();
        var module=LoadLibraryEx(path,IntPtr.Zero,2);
        if(module==IntPtr.Zero) throw new Win32Exception();
        foreach(int type in new[]{3,14,16})
            EnumResourceNames(module,(IntPtr)type,(m,t,n,p)=> {
                // PE icon/version names in this app are integer resource identifiers.
                if(n.ToInt64()>65535) throw new InvalidDataException("Unexpected named native resource");
                EnumResourceLanguages(m,t,n,(m2,t2,n2,l,p2)=>{old.Add((type,n,l));return true;},IntPtr.Zero); return true;
            },IntPtr.Zero);
        FreeLibrary(module);
        using var stream=File.OpenRead(icon); using var reader=new BinaryReader(stream);
        if(reader.ReadUInt16()!=0 || reader.ReadUInt16()!=1) throw new InvalidDataException("Invalid icon");
        ushort count=reader.ReadUInt16();
        var entries=new List<(byte[] Entry,int Size,int Offset)>();
        for(int i=0;i<count;i++) { var entry=reader.ReadBytes(12); int offset=reader.ReadInt32(); entries.Add((entry,BitConverter.ToInt32(entry,8),offset)); }
        using var groupStream=new MemoryStream(); using var group=new BinaryWriter(groupStream);
        group.Write((ushort)0); group.Write((ushort)1); group.Write(count);
        var update=BeginUpdateResource(path,false); if(update==IntPtr.Zero) throw new Win32Exception();
        bool ok=false;
        try {
            foreach(var r in old) if(!UpdateResource(update,(IntPtr)r.Type,r.Name,r.Language,null,0)) throw new Win32Exception();
            for(int i=0;i<count;i++) {
                var entry=entries[i]; stream.Position=entry.Offset; var data=reader.ReadBytes(entry.Size);
                if(!UpdateResource(update,(IntPtr)3,(IntPtr)(i+1),0,data,(uint)data.Length)) throw new Win32Exception();
                group.Write(entry.Entry); group.Write((ushort)(i+1));
            }
            var groupBytes=groupStream.ToArray();
            if(!UpdateResource(update,(IntPtr)14,(IntPtr)1,0,groupBytes,(uint)groupBytes.Length)) throw new Win32Exception();
            var version=Version(Path.GetFileName(path));
            if(!UpdateResource(update,(IntPtr)16,(IntPtr)1,0,version,(uint)version.Length)) throw new Win32Exception();
            ok=true;
        } finally { if(!EndUpdateResource(update,!ok) && ok) throw new Win32Exception(); }
    }
}
