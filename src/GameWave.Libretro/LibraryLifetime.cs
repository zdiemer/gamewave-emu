using System.Runtime.InteropServices;

namespace GameWave.Libretro;

/// <summary>Native AOT runtimes cannot be unloaded. Keep the module resident after deinit.</summary>
static class LibraryLifetime
{
    [StructLayout(LayoutKind.Sequential)]
    struct DlInfo { public nint FileName, Base, SymbolName, SymbolAddress; }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PinWindows(uint flags, nint address, out nint module);
    [DllImport("libdl.so.2", EntryPoint = "dladdr")]
    static extern int AddressLinux(nint address, out DlInfo info);
    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    static extern nint OpenLinux(nint path, int flags);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dladdr")]
    static extern int AddressMac(nint address, out DlInfo info);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dlopen")]
    static extern nint OpenMac(nint path, int flags);

    public static void Pin(nint address)
    {
        bool pinned;
        if (OperatingSystem.IsWindows())
            pinned = PinWindows(1 | 4, address, out _); // PIN | FROM_ADDRESS
        else if (OperatingSystem.IsLinux())
            pinned = AddressLinux(address, out var info) != 0 && OpenLinux(info.FileName, 2 | 0x1000) != 0; // NOW | NODELETE
        else if (OperatingSystem.IsMacOS())
            pinned = AddressMac(address, out var info) != 0 && OpenMac(info.FileName, 2 | 0x80) != 0;
        else
            pinned = false;
        if (!pinned)
            throw new PlatformNotSupportedException("Could not retain the Native AOT module.");
    }
}
