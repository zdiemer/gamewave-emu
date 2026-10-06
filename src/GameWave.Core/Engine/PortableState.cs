using System.IO.Compression;
using System.Security.Cryptography;
using GameWave.Disc;
using GameWave.Lua;
using GameWave.Media;

namespace GameWave.Engine;

sealed class PortableStateRestoredException : Exception;

public sealed partial class Machine
{
    byte[]? _stateIdentity;

    byte[] StateIdentity()
    {
        if (_stateIdentity is not null) return _stateIdentity;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Disc.Find("/gamewave.diz")!.ReadAll());
        hash.AppendData(Disc.Find(Info.AppFile)!.ReadAll());
        // Include the directory manifest to distinguish discs sharing a program.
        void Directory(string path)
        {
            Span<byte> length = stackalloc byte[8];
            foreach (var entry in (Disc.List(path) ?? []).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                string name = DiscPath.Combine(path, entry.Name);
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, entry.IsDirectory ? 0 : entry.Length);
                hash.AppendData(length);
                if (entry.IsDirectory) Directory(name);
            }
        }
        Directory("/");
        return _stateIdentity = hash.GetHashAndReset();
    }

    /// <summary>Captures a portable state while the frame-driven worker is parked.</summary>
    public byte[] SaveState()
    {
        if (_frames is null) throw new InvalidOperationException("Portable states require frame-driven emulation.");
        return _frames.AtBoundary(() =>
        {
            if (_lua is null || _lua.MainThread.NativeDepth != 1 ||
                (_lua.CurrentThread != _lua.MainThread && _lua.CurrentThread.NativeDepth != 0))
                throw new InvalidOperationException("The game is inside a nested Lua callback; try saving again after it returns.");
            var state = CaptureQuickState(_lua!, _frameContinuation != FrameContinuation.Boot);
            using var payload = new MemoryStream();
            using (var w = new BinaryWriter(payload, System.Text.Encoding.UTF8, true))
            {
                w.Write(Clock.NowSeconds);
                w.Write(_pollCount); w.Write(_frameInstructions);
                w.Write(_sleepUntil.HasValue); if (_sleepUntil is { } sleep) w.Write(sleep);
                w.Write((int)_frameContinuation); w.Write(_frames.Until);
                LuaStateBinary.Write(w, state.Lua);
                var resources = new ResourceWriter(w);
                WriteApi(w, resources, state.Api);
                resources.Osd(state.Osd);
                resources.Audio(state.Audio);
                var input = state.Input;
                StateIO.Array(w, input.Events, e => { w.Write(e.Key); w.Write(e.Remote); w.Write(e.Timestamp); });
                w.Write(input.Capacity); w.Write(input.Mode); w.Write(input.RemotesEnabled);
                StateIO.Array(w, input.RandomKeys, w.Write);
                w.Write(input.RandomState);
                w.Write(state.Video.SourceKind); resources.Picture(state.Video.Still);
                var movie = state.Video.Movie;
                StateIO.Text(w, movie.File?.Path ?? "");
                w.Write(movie.Loop); w.Write(movie.Playing); w.Write(movie.Paused); w.Write(movie.Time);
                resources.Picture(movie.Shown);
                w.Write(movie.Decoder is not null);
                if (movie.Decoder is not null) StateIO.Bytes(w, movie.Decoder);
                StateIO.Array(w, Saves.Slots, s => WriteSlot(w, s));
            }
            if (payload.Length > StateIO.MaxBytes) throw new InvalidDataException("The state exceeds the supported size.");
            using var compressed = new MemoryStream();
            using (var zip = new BrotliStream(compressed, CompressionLevel.Fastest, true))
                payload.WriteTo(zip);
            byte[] bytes = compressed.ToArray();
            using var result = new MemoryStream();
            using (var w = new BinaryWriter(result, System.Text.Encoding.UTF8, true))
            {
                w.Write("GWSTATE1"u8); w.Write(2); w.Write(bytes.Length); w.Write((int)payload.Length);
                w.Write(StateIdentity()); w.Write(SHA256.HashData(bytes)); w.Write(bytes);
            }
            return result.ToArray();
        });
    }

    /// <summary>Validates a portable state before replacing the running machine.</summary>
    public void LoadState(ReadOnlySpan<byte> data, bool persistSaves = true)
    {
        if (_frames is null) throw new InvalidOperationException("Portable states require frame-driven emulation.");
        using var stream = new MemoryStream(data.ToArray(), false);
        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual("GWSTATE1"u8))
            throw new InvalidDataException("Unsupported save-state format.");
        int version = reader.ReadInt32();
        if (version is not (1 or 2)) throw new InvalidDataException("Unsupported save-state version.");
        int length = StateIO.Count(reader, StateIO.MaxBytes), rawLength = StateIO.Count(reader, StateIO.MaxBytes);
        if (!reader.ReadBytes(32).AsSpan().SequenceEqual(StateIdentity()))
            throw new InvalidDataException("The save state belongs to a different disc.");
        var checksum = reader.ReadBytes(32);
        byte[] compressed = reader.ReadBytes(length);
        if (compressed.Length != length || !checksum.AsSpan().SequenceEqual(SHA256.HashData(compressed)))
            throw new InvalidDataException("The save state is truncated or damaged.");
        var raw = new byte[rawLength];
        using (var zip = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress))
        {
            zip.ReadExactly(raw);
            if (zip.ReadByte() != -1) throw new InvalidDataException("Invalid decompressed state length.");
        }
        using var payload = new MemoryStream(raw, false);
        using var r = new BinaryReader(payload);
        double seconds = r.ReadDouble();
        if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidDataException("Invalid saved clock.");
        int polls = StateIO.Count(r, 7), instructions = StateIO.Count(r, 9999);
        double? sleep = r.ReadBoolean() ? r.ReadDouble() : null;
        if (sleep.HasValue && (!double.IsFinite(sleep.Value) || sleep < seconds))
            throw new InvalidDataException("Invalid saved sleep deadline.");
        var lua = CreateLuaState(reset: false);
        var continuation = version >= 2 ? (FrameContinuation)StateIO.Count(r, 4) : FrameContinuation.Boot;
        double until = version >= 2 ? r.ReadDouble() : seconds;
        if (!double.IsFinite(until) || until < seconds) throw new InvalidDataException("Invalid frame continuation.");
        var savedLua = LuaStateBinary.Read(r, lua, version);
        var resources = new ResourceReader(r, this);
        var api = ReadApi(r, resources);
        var osd = resources.Osd(); var audio = resources.Audio();
        var events = StateIO.Array(r, () => new KeyEvent(r.ReadInt32(), r.ReadInt32(), r.ReadInt64()));
        int capacity = StateIO.Count(r);
        if (capacity < 1 || events.Length > capacity) throw new InvalidDataException("Invalid input queue.");
        var input = new InputQueue.State(events, capacity, r.ReadInt32(), r.ReadBoolean(), StateIO.Array(r, r.ReadInt32), version >= 2 ? r.ReadUInt32() : 1);
        int source = StateIO.Count(r, 2); var still = resources.Picture();
        var movie = new MoviePlayer.Snapshot(resources.File(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(), r.ReadDouble(), resources.Picture());
        if (version >= 2 && r.ReadBoolean()) movie = movie with { Decoder = StateIO.Bytes(r) };
        if (!double.IsFinite(movie.Time) || movie.Time < 0 || (source == 1 && still is null))
            throw new InvalidDataException("Invalid video state.");
        var video = new VideoPlane.State(source, still, movie);
        var slots = StateIO.Array(r, () => ReadSlot(r));
        if (payload.Position != payload.Length) throw new InvalidDataException("Unexpected state data.");
        // Enumerated save handles must refer to the restored flash slots.
        for (int i = 0; i < api.EnumeratedSaves.Count; i++)
        {
            var saved = api.EnumeratedSaves[i];
            api.EnumeratedSaves[i] = slots.FirstOrDefault(s => s.GameName == saved.GameName && s.SlotName == saved.SlotName) ?? saved;
        }
        if (State != MachineState.Running)
        {
            Stop();
            Start();
        }
        _frames.AtBoundary(() =>
        {
            RestoreApiState(api);
            Video.RestoreState(video); Audio.RestoreState(audio);
            Osd.RestoreState(osd); Input.RestoreState(input); Saves.RestoreSlots(slots, persistSaves);
            Clock.SetExternalTime(seconds);
            _pollCount = polls; _frameInstructions = instructions;
            _frameContinuation = continuation;
            _resumeUntil = version >= 2 ? until : null;
            _frames.Until = until;
            _skipPoll = continuation == FrameContinuation.Poll;
            _skipSleep = continuation == FrameContinuation.Sleep;
            _sleepUntil = sleep; _resumeSleepUntil = version == 1 ? sleep : null;
            _lua = savedLua.Copy; _portableResume = true;
            return true;
        }, restore: true);
    }
}
