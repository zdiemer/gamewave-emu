using System.Runtime.InteropServices;

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
