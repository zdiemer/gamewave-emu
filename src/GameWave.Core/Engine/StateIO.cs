using System.Text;
using System.Runtime.InteropServices;

namespace GameWave.Engine;

/// <summary>Bounded, little-endian primitives for versioned state files.</summary>
internal static class StateIO
{
    public const int MaxBytes = 128 << 20;
    public static int Count(BinaryReader reader, int maximum = 1 << 22)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException("Invalid save-state length.");
        return count;
    }
    public static byte[] Bytes(BinaryReader reader, int maximum = MaxBytes)
    {
        int count = Count(reader, maximum);
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return bytes;
    }
    public static void Bytes(BinaryWriter writer, byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
    public static void Numbers<T>(BinaryWriter writer, T[] values) where T : unmanaged
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("States require a little-endian host.");
        writer.Write(values.Length);
        writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }
    public static T[] Numbers<T>(BinaryReader reader, int maximum) where T : unmanaged
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("States require a little-endian host.");
        var values = new T[Count(reader, maximum)];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }
    public static string Text(BinaryReader reader) => Encoding.UTF8.GetString(Bytes(reader, 16 << 20));
    public static void Text(BinaryWriter writer, string value) => Bytes(writer, Encoding.UTF8.GetBytes(value));
    public static T[] Array<T>(BinaryReader reader, Func<T> read, int maximum = 1 << 22)
    {
        var result = new T[Count(reader, maximum)];
        for (int i = 0; i < result.Length; i++) result[i] = read();
        return result;
    }
    public static void Array<T>(BinaryWriter writer, IEnumerable<T> values, Action<T> write)
    {
        if (!values.TryGetNonEnumeratedCount(out int count))
        {
            values = values.ToArray();
            count = values.Count();
        }
        writer.Write(count);
        foreach (var item in values) write(item);
    }
}
