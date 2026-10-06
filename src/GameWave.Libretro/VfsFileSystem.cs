using System.Runtime.InteropServices;
using GameWave.Disc;

namespace GameWave.Libretro;

sealed unsafe class VfsFileSystem(RetroVfs api) : FileSystem
{
    internal static bool Complete(RetroVfs api) => api.Open != null && api.Close != null && api.Size != null && api.Tell != null &&
        api.Seek != null && api.Read != null && api.Write != null && api.Flush != null && api.Remove != null && api.Rename != null &&
        api.Truncate != null && api.Stat != null && api.Mkdir != null && api.OpenDir != null && api.ReadDir != null &&
        api.DirName != null && api.IsDir != null && api.CloseDir != null;

    static byte[] Utf8(string path) => System.Text.Encoding.UTF8.GetBytes(path + "\0");
    public override Stream OpenRead(string path) => Open(path, 1);
    public override Stream Create(string path) => Open(path, 2);
    Stream Open(string path, uint mode)
    {
        fixed (byte* name = Utf8(path))
        {
            nint handle = api.Open(name, mode, 0);
            if (handle == 0) throw new IOException("VFS could not open " + path);
            return new VfsStream(api, handle, mode == 2);
        }
    }
    public override FileSystemEntry? Stat(string path)
    {
        int size = 0, flags;
        fixed (byte* name = Utf8(path)) flags = api.Stat(name, &size);
        if ((flags & 1) == 0) return null;
        bool directory = (flags & 2) != 0;
        long length = 0;
        // The v3 stat size is 32-bit; size() retains the full DVD image length.
        if (!directory) { using var stream = OpenRead(path); length = stream.Length; }
        return new(Path.GetFileName(path), directory, length);
    }
    public override IReadOnlyList<FileSystemEntry> List(string path)
    {
        nint directory;
        fixed (byte* name = Utf8(path)) directory = api.OpenDir(name, 1);
        if (directory == 0) throw new IOException("VFS could not list " + path);
        try
        {
            var entries = new List<FileSystemEntry>();
            while (api.ReadDir(directory) != 0)
            {
                string? name = Marshal.PtrToStringUTF8((nint)api.DirName(directory));
                if (name is null or "." or "..") continue;
                if (api.IsDir(directory) != 0) entries.Add(new(name, true, 0));
                else if (Stat(Path.Combine(path, name)) is { } entry) entries.Add(entry with { Name = name });
            }
            return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray();
        }
        finally { api.CloseDir(directory); }
    }
    public override void CreateDirectory(string path)
    {
        if (DirectoryExists(path)) return;
        if (Path.GetDirectoryName(path) is { Length: > 0 } parent && parent != path) CreateDirectory(parent);
        fixed (byte* name = Utf8(path))
            if (api.Mkdir(name) != 0 && !DirectoryExists(path)) throw new IOException("VFS could not create directory " + path);
    }
    public override void Delete(string path)
    {
        fixed (byte* name = Utf8(path))
            if (api.Remove(name) != 0 && FileExists(path)) throw new IOException("VFS could not remove " + path);
    }
    public override void Move(string source, string destination)
    {
        bool Rename(string from, string to)
        {
            fixed (byte* first = Utf8(from)) fixed (byte* second = Utf8(to)) return api.Rename(first, second) == 0;
        }
        if (Rename(source, destination)) return;
        if (!FileExists(source) || !FileExists(destination)) throw new IOException("VFS could not rename " + source);
        // Some frontends cannot replace an existing target. Keep the old complete
        // image until the replacement succeeds, and restore it if the second rename fails.
        string backup = destination + ".previous";
        if (FileExists(backup) || !Rename(destination, backup)) throw new IOException("VFS could not preserve " + destination);
        if (!Rename(source, destination))
        {
            Rename(backup, destination);
            throw new IOException("VFS could not rename " + source);
        }
        Delete(backup);
    }

    sealed class VfsStream(RetroVfs api, nint handle, bool writable) : Stream
    {
        nint _handle = handle;
        public override bool CanRead => _handle != 0 && !writable;
        public override bool CanWrite => _handle != 0 && writable;
        public override bool CanSeek => _handle != 0;
        void Check() => ObjectDisposedException.ThrowIf(_handle == 0, this);
        public override long Length { get { Check(); long size = api.Size(_handle); return size >= 0 ? size : throw new IOException("VFS size failed."); } }
        public override long Position { get { Check(); long position = api.Tell(_handle); return position >= 0 ? position : throw new IOException("VFS tell failed."); } set => Seek(value, SeekOrigin.Begin); }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Check(); if (api.Seek(_handle, offset, (int)origin) < 0) throw new IOException("VFS seek failed.");
            return Position;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            Check(); if (!CanRead) throw new NotSupportedException();
            fixed (byte* data = buffer)
            {
                long count = api.Read(_handle, data, (ulong)buffer.Length);
                if (count < 0 || count > buffer.Length) throw new IOException("VFS read failed.");
                return (int)count;
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(); if (!CanWrite) throw new NotSupportedException();
            while (!buffer.IsEmpty)
            {
                long count;
                fixed (byte* data = buffer) count = api.Write(_handle, data, (ulong)buffer.Length);
                if (count <= 0 || count > buffer.Length) throw new IOException("VFS write failed.");
                buffer = buffer[(int)count..];
            }
        }
        public override void Flush() { Check(); if (api.Flush(_handle) != 0) throw new IOException("VFS flush failed."); }
        public override void SetLength(long value) { Check(); if (!CanWrite || value < 0) throw new NotSupportedException(); if (api.Truncate(_handle, value) < 0) throw new IOException("VFS truncate failed."); }
        protected override void Dispose(bool disposing)
        {
            if (_handle == 0) return;
            try { if (writable) Flush(); }
            finally { api.Close(_handle); _handle = 0; base.Dispose(disposing); }
        }
    }
}
