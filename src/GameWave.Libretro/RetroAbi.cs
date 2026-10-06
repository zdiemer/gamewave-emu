using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace GameWave.Libretro;

// Layouts from libretro-common/include/libretro.h. C bool is one byte; size_t is nuint.
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroGameInfo { public byte* Path; public void* Data; public nuint Size; public byte* Meta; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroSystemInfo
{
    public byte* Name; public byte* Version; public byte* Extensions;
    public byte NeedFullPath; public byte BlockExtract;
}
[StructLayout(LayoutKind.Sequential)]
struct RetroGeometry { public uint Width, Height, MaxWidth, MaxHeight; public float Aspect; }
[StructLayout(LayoutKind.Sequential)]
struct RetroTiming { public double Fps, SampleRate; }
[StructLayout(LayoutKind.Sequential)]
struct RetroAvInfo { public RetroGeometry Geometry; public RetroTiming Timing; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroMessage { public byte* Text; public uint Frames; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroVariable { public byte* Key; public byte* Value; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroInputDescriptor { public uint Port, Device, Index, Id; public byte* Description; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroControllerDescription { public byte* Description; public uint Id; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroControllerInfo { public RetroControllerDescription* Types; public uint Count; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroDiskControl
{
    public delegate* unmanaged[Cdecl]<byte, byte> SetEject;
    public delegate* unmanaged[Cdecl]<byte> GetEject;
    public delegate* unmanaged[Cdecl]<uint> GetIndex;
    public delegate* unmanaged[Cdecl]<uint, byte> SetIndex;
    public delegate* unmanaged[Cdecl]<uint> GetCount;
    public delegate* unmanaged[Cdecl]<uint, RetroGameInfo*, byte> Replace;
    public delegate* unmanaged[Cdecl]<byte> Add;
}
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroKeyboard { public delegate* unmanaged[Cdecl]<byte, uint, uint, ushort, void> Callback; }

[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroDiskControlExt
{
    public RetroDiskControl Basic;
    public delegate* unmanaged[Cdecl]<uint, byte*, byte> SetInitial;
    public delegate* unmanaged[Cdecl]<uint, byte*, nuint, byte> GetPath, GetLabel;
}
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroOptionValue { public byte* Value, Label; }
[InlineArray(128)]
struct RetroOptionValues { RetroOptionValue _first; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroOptionCategory { public byte* Key, Description, Info; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroOptionDefinition
{
    public byte* Key, Description, CategorizedDescription, Info, CategorizedInfo, Category;
    public RetroOptionValues Values;
    public byte* Default;
}
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroOptionDefinitionV1
{
    public byte* Key, Description, Info;
    public RetroOptionValues Values;
    public byte* Default;
}
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroOptions { public RetroOptionCategory* Categories; public RetroOptionDefinition* Definitions; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroMemoryDescriptor
{
    public ulong Flags;
    public void* Pointer;
    public nuint Offset, Start, Select, Disconnect, Length;
    public byte* AddressSpace;
}
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroMemoryMap { public RetroMemoryDescriptor* Descriptors; public uint Count; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroLog { public delegate* unmanaged[Cdecl]<int, byte*, byte*, void> Callback; }
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroVfs
{
    public delegate* unmanaged[Cdecl]<nint, byte*> GetPath;
    public delegate* unmanaged[Cdecl]<byte*, uint, uint, nint> Open;
    public delegate* unmanaged[Cdecl]<nint, int> Close;
    public delegate* unmanaged[Cdecl]<nint, long> Size, Tell;
    public delegate* unmanaged[Cdecl]<nint, long, int, long> Seek;
    public delegate* unmanaged[Cdecl]<nint, void*, ulong, long> Read, Write;
    public delegate* unmanaged[Cdecl]<nint, int> Flush;
    public delegate* unmanaged[Cdecl]<byte*, int> Remove;
    public delegate* unmanaged[Cdecl]<byte*, byte*, int> Rename;
    public delegate* unmanaged[Cdecl]<nint, long, long> Truncate;
    public delegate* unmanaged[Cdecl]<byte*, int*, int> Stat;
    public delegate* unmanaged[Cdecl]<byte*, int> Mkdir;
    public delegate* unmanaged[Cdecl]<byte*, byte, nint> OpenDir;
    public delegate* unmanaged[Cdecl]<nint, byte> ReadDir;
    public delegate* unmanaged[Cdecl]<nint, byte*> DirName;
    public delegate* unmanaged[Cdecl]<nint, byte> IsDir;
    public delegate* unmanaged[Cdecl]<nint, int> CloseDir;
}
[StructLayout(LayoutKind.Sequential)]
unsafe struct RetroVfsInfo { public uint Version; public RetroVfs* Interface; }
