using System.Buffers.Binary;
using GameWave.Engine;

namespace GameWave.Libretro;

/// <summary>Stable, frontend-owned SRAM image of the console's named flash slots.</summary>
sealed class FrontendMemory
{
    public const int Size = 4 << 20;
    // POH arrays keep their address for the lifetime of the content session.
    public readonly byte[] Data = GC.AllocateArray<byte>(Size, pinned: true);
    readonly byte[] _mirror = new byte[Size];
    long _revision = -1;

    public void Import(SaveStore store)
    {
        if (Data.AsSpan().SequenceEqual(_mirror)) return;
        if (Data.AsSpan().IndexOfAnyExcept((byte)0) < 0) store.Clear();
        else
        {
            if (!Data.AsSpan(0, 8).SequenceEqual("GWSRAM1\0"u8)) throw new InvalidDataException("Invalid frontend SRAM header.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(8));
            if (length < 12 || length > Size - 16) throw new InvalidDataException("Invalid frontend SRAM length.");
            store.Import(Data.AsSpan(16, length));
        }
        Export(store, force: true);
    }

    public void Export(SaveStore store, bool force = false)
    {
        if (!force && _revision == store.Revision) return;
        var payload = store.Export();
        if (payload.Length > Size - 16) throw new InvalidDataException("The console flash exceeds the 4 MiB SRAM image.");
        Array.Clear(Data);
        "GWSRAM1\0"u8.CopyTo(Data);
        BinaryPrimitives.WriteInt32LittleEndian(Data.AsSpan(8), payload.Length);
        payload.CopyTo(Data, 16); Data.CopyTo(_mirror, 0); _revision = store.Revision;
    }
}
