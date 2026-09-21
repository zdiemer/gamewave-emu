namespace GameWave.Graphics;

/// <summary>
/// The on-screen display: textures and overlays by id, composited over the video plane.
/// The game thread changes it through the <c>gl</c> and <c>text</c> modules; the display
/// thread composites it. Changes made between <c>BeginScene</c> and <c>EndScene</c> appear
/// together, so the display keeps showing the last complete scene until the batch ends.
/// </summary>
public sealed class Osd
{
    internal sealed class State
    {
        public required Dictionary<int, Texture> Textures { get; init; }
        public required List<Texture> DetachedTextures { get; init; }
        public required Dictionary<int, Overlay> Overlays { get; init; }
        public required int NextTextureId { get; init; }
        public required int NextOverlayId { get; init; }
        public required int SceneDepth { get; init; }
        public required bool Shown { get; init; }
        public required DrawState[] Snapshot { get; init; }
        public required bool SnapshotShown { get; init; }
    }

    internal readonly record struct DrawState(Texture Texture, int X, int Y, float Opacity);
    public const int Width = 720;
    public const int Height = 480;

    public object Sync { get; } = new();

    readonly Dictionary<int, Texture> _textures = new();
    readonly Dictionary<int, Overlay> _overlays = new();
    int _nextTextureId = 1;
    int _nextOverlayId = 1;
    int _sceneDepth;
    bool _shown = true;

    DrawItem[] _snapshot = [];
    bool _snapshotShown = true;

    readonly record struct DrawItem(Texture Texture, int X, int Y, float Opacity);

    public int TextureCount { get { lock (Sync) return _textures.Count; } }
    public int OverlayCount { get { lock (Sync) return _overlays.Count; } }

    public void Reset()
    {
        lock (Sync)
        {
            _textures.Clear();
            _overlays.Clear();
            _sceneDepth = 0;
            _shown = true;
            _snapshot = [];
            _snapshotShown = true;
        }
    }

    internal State CaptureState()
    {
        lock (Sync)
        {
            var textures = _textures.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
            var byObject = new Dictionary<Texture, Texture>(ReferenceEqualityComparer.Instance);
            foreach (var (id, texture) in _textures)
                byObject[texture] = textures[id];
            var detached = new List<Texture>();
            foreach (var texture in _overlays.Values.SelectMany(overlay => overlay.Frames)
                         .Concat(_snapshot.Select(item => item.Texture)))
            {
                if (byObject.ContainsKey(texture)) continue;
                var copy = texture.Clone();
                byObject[texture] = copy;
                detached.Add(copy);
            }
            var overlays = _overlays.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(byObject));
            return new State
            {
                Textures = textures,
                DetachedTextures = detached,
                Overlays = overlays,
                NextTextureId = _nextTextureId,
                NextOverlayId = _nextOverlayId,
                SceneDepth = _sceneDepth,
                Shown = _shown,
                Snapshot = _snapshot.Select(item => new DrawState(byObject[item.Texture], item.X, item.Y, item.Opacity)).ToArray(),
                SnapshotShown = _snapshotShown,
            };
        }
    }

    internal void RestoreState(State state)
    {
        lock (Sync)
        {
            _textures.Clear();
            var byObject = new Dictionary<Texture, Texture>(ReferenceEqualityComparer.Instance);
            foreach (var (id, texture) in state.Textures)
            {
                var copy = texture.Clone();
                _textures[id] = copy;
                byObject[texture] = copy;
            }
            foreach (var texture in state.DetachedTextures)
                byObject[texture] = texture.Clone();
            _overlays.Clear();
            foreach (var (id, overlay) in state.Overlays)
                _overlays[id] = overlay.Clone(byObject);
            _nextTextureId = state.NextTextureId;
            _nextOverlayId = state.NextOverlayId;
            _sceneDepth = state.SceneDepth;
            _shown = state.Shown;
            _snapshot = state.Snapshot.Select(item => new DrawItem(byObject[item.Texture], item.X, item.Y, item.Opacity)).ToArray();
            _snapshotShown = state.SnapshotShown;
        }
    }

    // ------------------------------------------------------------------ textures

    public int AddTexture(Texture t)
    {
        lock (Sync)
        {
            int id = _nextTextureId++;
            _textures[id] = t;
            return id;
        }
    }

    public Texture? GetTexture(int id)
    {
        lock (Sync)
            return _textures.GetValueOrDefault(id);
    }

    public void FreeTexture(int id)
    {
        lock (Sync)
            _textures.Remove(id);
    }

    // ------------------------------------------------------------------ overlays

    public int CreateOverlay(Texture? t)
    {
        lock (Sync)
        {
            int id = _nextOverlayId++;
            var o = new Overlay(id);
            if (t is not null)
                o.Frames.Add(t);
            _overlays[id] = o;
            return id;
        }
    }

    public Overlay? GetOverlay(int id)
    {
        lock (Sync)
            return _overlays.GetValueOrDefault(id);
    }

    public void FreeOverlay(int id)
    {
        lock (Sync)
        {
            _overlays.Remove(id);
            if (_sceneDepth == 0)
                TakeSnapshot();
        }
    }

    /// <summary>Runs a change to an overlay under the OSD lock, then refreshes the display.</summary>
    public bool Modify(int overlayId, Action<Overlay> change)
    {
        lock (Sync)
        {
            if (!_overlays.TryGetValue(overlayId, out var o))
                return false;
            change(o);
            if (_sceneDepth == 0)
                TakeSnapshot();
            return true;
        }
    }

    public void Changed()
    {
        lock (Sync)
        {
            if (_sceneDepth == 0)
                TakeSnapshot();
        }
    }

    public void BeginScene()
    {
        lock (Sync)
            _sceneDepth++;
    }

    public void EndScene()
    {
        lock (Sync)
        {
            if (_sceneDepth > 0)
                _sceneDepth--;
            if (_sceneDepth == 0)
                TakeSnapshot();
        }
    }

    public void HideAll()
    {
        lock (Sync)
        {
            foreach (var o in _overlays.Values)
                o.Visible = false;
            if (_sceneDepth == 0)
                TakeSnapshot();
        }
    }

    public bool Shown
    {
        get { lock (Sync) return _shown; }
        set
        {
            lock (Sync)
            {
                _shown = value;
                if (_sceneDepth == 0)
                    TakeSnapshot();
            }
        }
    }

    /// <summary>Advances every overlay's animations to <paramref name="now"/>.</summary>
    public void Animate(long now)
    {
        lock (Sync)
        {
            bool any = false;
            foreach (var o in _overlays.Values)
            {
                if (o.Animations.Count > 0 || o.TextureAnim is { Finished: false })
                {
                    o.Update(now);
                    any = true;
                }
            }
            if (any && _sceneDepth == 0)
                TakeSnapshot();
        }
    }

    void TakeSnapshot()
    {
        var list = new List<(Overlay O, Texture T)>();
        foreach (var o in _overlays.Values)
        {
            if (!o.Visible || o.Opacity <= 0f)
                continue;
            var t = o.CurrentTexture;
            if (t is null)
                continue;
            list.Add((o, t));
        }
        list.Sort((a, b) => a.O.Z != b.O.Z ? a.O.Z.CompareTo(b.O.Z) : a.O.Sequence.CompareTo(b.O.Sequence));
        var snap = new DrawItem[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            var (o, t) = list[i];
            snap[i] = new DrawItem(t, o.X, o.Y, o.Opacity * (t.AlphaLevel / 15f));
        }
        _snapshot = snap;
        _snapshotShown = _shown;
    }

    // ------------------------------------------------------------------ composition

    /// <summary>Draws the last complete scene over <paramref name="frame"/> (720x480, 0xAARRGGBB).</summary>
    public void Composite(uint[] frame)
    {
        DrawItem[] items;
        bool shown;
        lock (Sync)
        {
            items = _snapshot;
            shown = _snapshotShown;
        }
        if (!shown)
            return;
        foreach (var it in items)
            Draw(frame, it.Texture, it.X, it.Y, it.Opacity);
    }

    static void Draw(uint[] frame, Texture t, int x, int y, float opacity)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + t.Width), y1 = Math.Min(Height, y + t.Height);
        if (x0 >= x1 || y0 >= y1)
            return;
        uint op = (uint)Math.Clamp((int)(opacity * 256f), 0, 256);
        var src = t.Pixels;
        for (int py = y0; py < y1; py++)
        {
            int s = (py - y) * t.Width + (x0 - x);
            int d = py * Width + x0;
            for (int px = x0; px < x1; px++, s++, d++)
            {
                uint c = src[s];
                uint a = ((c >> 24) * op) >> 8;
                if (a == 0)
                    continue;
                if (a >= 255)
                {
                    frame[d] = c | 0xFF000000;
                    continue;
                }
                uint b = frame[d];
                uint ia = 255 - a;
                uint rb = ((c & 0xFF00FF) * a + (b & 0xFF00FF) * ia) >> 8 & 0xFF00FF;
                uint g = ((c & 0x00FF00) * a + (b & 0x00FF00) * ia) >> 8 & 0x00FF00;
                frame[d] = 0xFF000000 | rb | g;
            }
        }
    }
}
