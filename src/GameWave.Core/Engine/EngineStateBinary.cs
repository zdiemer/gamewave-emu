using GameWave.Graphics;
using GameWave.Lua;
using GameWave.Media;

namespace GameWave.Engine;

public sealed partial class Machine
{
    static void WriteSlot(BinaryWriter w, SaveStore.Slot slot)
    { w.Write(slot.Id); StateIO.Text(w, slot.GameName); StateIO.Text(w, slot.SlotName); StateIO.Bytes(w, slot.Data); }
    static SaveStore.Slot ReadSlot(BinaryReader r) => new()
    { Id = r.ReadInt32(), GameName = StateIO.Text(r), SlotName = StateIO.Text(r), Data = StateIO.Bytes(r) };

    sealed class ResourceWriter(BinaryWriter w)
    {
        readonly Dictionary<Sound, int> _sounds = new(ReferenceEqualityComparer.Instance);
        public void Sound(Sound sound)
        {
            if (_sounds.TryGetValue(sound, out int id)) { w.Write(id); return; }
            id = _sounds.Count + 1; _sounds.Add(sound, id); w.Write(-id); StateIO.Text(w, sound.Name);
            StateIO.Array(w, sound.Samples, w.Write);
        }
        public void Texture(Texture? texture)
        {
            w.Write(texture is not null); if (texture is null) return;
            w.Write(texture.Width); w.Write(texture.Height); StateIO.Text(w, texture.Name); w.Write(texture.AlphaLevel);
            StateIO.Array(w, texture.Pixels, w.Write);
        }
        public void Picture(Picture? picture)
        {
            w.Write(picture is not null); if (picture is null) return;
            w.Write(picture.Width); w.Write(picture.Height); w.Write(picture.Pts); w.Write(picture.Duration);
            w.Write(picture.Progressive); w.Write(picture.TopFieldFirst);
            StateIO.Bytes(w, picture.Y); StateIO.Bytes(w, picture.Cb); StateIO.Bytes(w, picture.Cr);
        }
        public void Animation(OverlayAnimation animation)
        {
            byte type = animation switch { PositionAnimation => 0, ParabolaAnimation => 1, VisibilityAnimation => 2, AlphaAnimation => 3, BlinkAnimation => 4, _ => throw new InvalidDataException("Unknown animation.") };
            w.Write(type); w.Write(animation.Start); w.Write(animation.Duration);
            switch (animation)
            {
                case PositionAnimation a: w.Write(a.X0); w.Write(a.Y0); w.Write(a.X1); w.Write(a.Y1); break;
                case ParabolaAnimation a: w.Write(a.X0); w.Write(a.Y0); w.Write(a.X1); w.Write(a.Y1); w.Write(a.Height); break;
                case VisibilityAnimation a: w.Write(a.Visible); break;
                case AlphaAnimation a: w.Write(a.Kind); break;
                case BlinkAnimation a: w.Write(a.Period); break;
            }
        }
        public void Osd(Osd.State state)
        {
            var textures = state.Textures.Values.Concat(state.DetachedTextures).ToArray();
            var ids = new Dictionary<Texture, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < textures.Length; i++) ids.Add(textures[i], i);
            StateIO.Array(w, textures, Texture);
            StateIO.Array(w, state.Textures, p => { w.Write(p.Key); w.Write(ids[p.Value]); });
            StateIO.Array(w, state.DetachedTextures, t => w.Write(ids[t]));
            StateIO.Array(w, state.Overlays.Values.OrderBy(o => o.Sequence), o =>
            {
                w.Write(o.Id); w.Write(o.ActiveFrame); w.Write(o.X); w.Write(o.Y); w.Write(o.Z); w.Write(o.Visible); w.Write(o.Opacity);
                StateIO.Array(w, o.Frames, t => w.Write(ids[t])); StateIO.Array(w, o.Animations, Animation);
                w.Write(o.TextureAnim is not null); o.TextureAnim?.WriteState(w);
            });
            w.Write(state.NextTextureId); w.Write(state.NextOverlayId); w.Write(state.SceneDepth); w.Write(state.Shown); w.Write(state.SnapshotShown);
            StateIO.Array(w, state.Snapshot, s => { w.Write(ids[s.Texture]); w.Write(s.X); w.Write(s.Y); w.Write(s.Opacity); });
        }
        public void Audio(AudioMixer.State state)
        {
            void Voice(AudioMixer.VoiceState voice) { Sound(voice.Sound); w.Write(voice.Position); w.Write(voice.Loop); w.Write(voice.Id); }
            StateIO.Array(w, state.Voices, Voice); StateIO.Array(w, state.Waiting, Voice); w.Write(state.NextVoiceId);
            w.Write(state.Movie is not null);
            if (state.Movie is { } movie)
            {
                StateIO.Array(w, movie.Ring, w.Write); w.Write(movie.Written); w.Write(movie.Played);
                w.Write(movie.Active); w.Write(movie.ClockRunning);
            }
        }
    }

    sealed class ResourceReader(BinaryReader r, Machine machine)
    {
        readonly Dictionary<int, Sound> _sounds = new();
        public Sound ReadSound()
        {
            int id = r.ReadInt32(); if (id > 0) return _sounds.GetValueOrDefault(id) ?? throw new InvalidDataException("Invalid sound reference.");
            string name = StateIO.Text(r); var sound = new Sound(StateIO.Array(r, r.ReadSingle, StateIO.MaxBytes / 4), name);
            _sounds.Add(checked(-id), sound); return sound;
        }
        public Texture? Texture()
        {
            if (!r.ReadBoolean()) return null;
            int width = StateIO.Count(r, 4096), height = StateIO.Count(r, 4096);
            if (width == 0 || height == 0) throw new InvalidDataException("Invalid texture size.");
            var texture = new Texture(width, height, StateIO.Text(r)) { AlphaLevel = r.ReadInt32() };
            var pixels = StateIO.Array(r, r.ReadUInt32, 4096 * 4096);
            if (pixels.Length != texture.Pixels.Length) throw new InvalidDataException("Invalid texture pixels.");
            pixels.CopyTo(texture.Pixels, 0); return texture;
        }
        public Picture? Picture()
        {
            if (!r.ReadBoolean()) return null;
            int width = StateIO.Count(r, 4096), height = StateIO.Count(r, 4096);
            if (width == 0 || height == 0) throw new InvalidDataException("Invalid picture size.");
            var p = new Picture(width, height) { Pts = r.ReadDouble(), Duration = r.ReadDouble(), Progressive = r.ReadBoolean(), TopFieldFirst = r.ReadBoolean() };
            void Plane(byte[] plane) { var bytes = StateIO.Bytes(r, plane.Length); if (bytes.Length != plane.Length) throw new InvalidDataException("Invalid picture plane."); bytes.CopyTo(plane, 0); }
            Plane(p.Y); Plane(p.Cb); Plane(p.Cr); return p;
        }
        public OverlayAnimation Animation()
        {
            byte type = r.ReadByte(); long start = r.ReadInt64(), duration = r.ReadInt64();
            OverlayAnimation animation = type switch
            {
                0 => new PositionAnimation { X0 = r.ReadInt32(), Y0 = r.ReadInt32(), X1 = r.ReadInt32(), Y1 = r.ReadInt32() },
                1 => new ParabolaAnimation { X0 = r.ReadInt32(), Y0 = r.ReadInt32(), X1 = r.ReadInt32(), Y1 = r.ReadInt32(), Height = r.ReadInt32() },
                2 => new VisibilityAnimation { Visible = r.ReadBoolean() },
                3 => new AlphaAnimation { Kind = r.ReadInt32() },
                4 => new BlinkAnimation { Period = r.ReadInt64() },
                _ => throw new InvalidDataException("Invalid animation type."),
            }; animation.Start = start; animation.Duration = duration; return animation;
        }
        public Osd.State Osd()
        {
            var textures = StateIO.Array(r, () => Texture() ?? throw new InvalidDataException("Missing texture."));
            Texture Ref() { int id = StateIO.Count(r, textures.Length); return id < textures.Length ? textures[id] : throw new InvalidDataException("Invalid texture reference."); }
            var registered = new Dictionary<int, Texture>(); int count = StateIO.Count(r); for (int i = 0; i < count; i++) registered.Add(r.ReadInt32(), Ref());
            var detached = StateIO.Array(r, Ref).ToList(); var overlays = new Dictionary<int, Overlay>(); count = StateIO.Count(r);
            for (int i = 0; i < count; i++)
            {
                var overlay = new Overlay(r.ReadInt32()) { ActiveFrame = r.ReadInt32(), X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(), Visible = r.ReadBoolean(), Opacity = r.ReadSingle() };
                overlay.Frames.AddRange(StateIO.Array(r, Ref)); overlay.Animations.AddRange(StateIO.Array(r, Animation));
                if (r.ReadBoolean()) overlay.TextureAnim = TextureAnimation.ReadState(r); overlays.Add(overlay.Id, overlay);
            }
            return new Osd.State
            {
                Textures = registered,
                DetachedTextures = detached,
                Overlays = overlays,
                NextTextureId = r.ReadInt32(),
                NextOverlayId = r.ReadInt32(),
                SceneDepth = r.ReadInt32(),
                Shown = r.ReadBoolean(),
                SnapshotShown = r.ReadBoolean(),
                Snapshot = StateIO.Array(r, () => new Graphics.Osd.DrawState(Ref(), r.ReadInt32(), r.ReadInt32(), r.ReadSingle())),
            };
        }
        public AudioMixer.State Audio()
        {
            AudioMixer.VoiceState Voice() { var sound = ReadSound(); int position = StateIO.Count(r, sound.Samples.Length); return new(sound, position, r.ReadBoolean(), r.ReadInt32()); }
            var voices = StateIO.Array(r, Voice, 8); var waiting = StateIO.Array(r, Voice); int id = r.ReadInt32();
            AudioMixer.MovieState? movie = null;
            if (r.ReadBoolean())
            {
                var ring = StateIO.Array(r, r.ReadSingle, AudioMixer.SampleRate * 4);
                if (ring.Length != AudioMixer.SampleRate * 4) throw new InvalidDataException("Invalid movie audio buffer.");
                long written = r.ReadInt64(), played = r.ReadInt64();
                if (played < 0 || written < 0 || written - played > AudioMixer.SampleRate * 2) throw new InvalidDataException("Invalid movie audio position.");
                movie = new(ring, written, played, r.ReadBoolean(), r.ReadBoolean());
            }
            return new(voices, waiting, id, movie);
        }
        public GameWave.Disc.DiscFile? File()
        {
            string path = StateIO.Text(r); return path.Length == 0 ? null : machine.Disc.Find(path) ?? throw new InvalidDataException("State resource is missing: " + path);
        }
    }

    static void WriteApi(BinaryWriter w, ResourceWriter resources, ApiState api)
    {
        StateIO.Array(w, api.Resources, p => { w.Write(p.Key); StateIO.Text(w, p.Value); });
        StateIO.Array(w, api.Fonts, p => { w.Write(p.Key); p.Value.WriteState(w); resources.Texture(p.Value.Sheet); });
        StateIO.Array(w, api.Texts, p => { w.Write(p.Key); w.Write(p.Value.Texture); w.Write(p.Value.Overlay); });
        StateIO.Array(w, api.Iframes, p => { w.Write(p.Key); resources.Picture(p.Value); });
        StateIO.Array(w, api.Sounds, p => { w.Write(p.Key); resources.Sound(p.Value); });
        StateIO.Array(w, api.Files, p => { w.Write(p.Key); StateIO.Text(w, p.Value.File.Path); w.Write(p.Value.Position); });
        StateIO.Array(w, api.Dictionaries, p => { w.Write(p.Key); StateIO.Bytes(w, p.Value.StateData); });
        StateIO.Array(w, api.EnumeratedSaves, s => WriteSlot(w, s));
        w.Write(api.NextId); w.Write(api.BuiltinFontId); w.Write(api.RandSeed); w.Write(api.TrayState);
    }

    static ApiState ReadApi(BinaryReader r, ResourceReader resources)
    {
        Dictionary<int, T> Map<T>(Func<T> read)
        { var map = new Dictionary<int, T>(); int count = StateIO.Count(r); for (int i = 0; i < count; i++) map.Add(r.ReadInt32(), read()); return map; }
        return new ApiState
        {
            Resources = Map(() => StateIO.Text(r)),
            Fonts = Map(() => { var font = Font.ReadState(r); font.Sheet = resources.Texture(); return font; }),
            Texts = Map(() => (r.ReadInt32(), r.ReadInt32())),
            Iframes = Map(() => resources.Picture() ?? throw new InvalidDataException("Missing still.")),
            Sounds = Map(resources.ReadSound),
            Files = Map(() => new FileState(resources.File() ?? throw new InvalidDataException("Missing file."), r.ReadInt64())),
            Dictionaries = Map(() => WordList.Load(StateIO.Bytes(r))),
            EnumeratedSaves = StateIO.Array(r, () => ReadSlot(r)).ToList(),
            NextId = r.ReadInt32(),
            BuiltinFontId = r.ReadInt32(),
            RandSeed = r.ReadInt32(),
            TrayState = r.ReadInt32(),
        };
    }
}
