using System.Buffers.Binary;
using System.Text;
using GameWave.Disc;
using GameWave.Graphics;
using GameWave.Lua;
using GameWave.Media;

namespace GameWave.Engine;

/// <summary>The engine's Lua modules, reimplemented on the emulator's subsystems.</summary>
public sealed partial class Machine
{
    readonly Dictionary<int, string> _resources = new();
    readonly Dictionary<int, Font> _fonts = new();
    readonly Dictionary<int, (int Texture, int Overlay)> _texts = new();
    readonly Dictionary<int, Picture> _iframes = new();
    readonly Dictionary<int, Sound> _sounds = new();
    readonly Dictionary<int, (DiscFile File, Stream Stream)> _files = new();
    readonly Dictionary<int, WordList> _dictionaries = new();
    List<SaveStore.Slot> _enumeratedSaves = new();
    int _nextId = 1;
    int _builtinFontId = -1;
    int _randSeed = 1;
    int _trayState;

    int NewId() => _nextId++;

    void ResetApiState()
    {
        _resources.Clear();
        _fonts.Clear();
        _texts.Clear();
        _iframes.Clear();
        foreach (var f in _files.Values)
            f.Stream.Dispose();
        _files.Clear();
        _sounds.Clear();
        _dictionaries.Clear();
        _enumeratedSaves = new();
        _builtinFontId = -1;
        _trayState = 0;
    }

    string ResourceDir(int id) => _resources.GetValueOrDefault(id, "/");

    DiscFile? FindInResource(int resource, string name)
        => Disc.Find(DiscPath.Combine(ResourceDir(resource), name));

    void RegisterApi(LuaState L)
    {
        ResetApiState();
        RegisterGl(L);
        RegisterText(L);
        RegisterMedia(L);
        RegisterInput(L);
        RegisterSystem(L);
        RegisterData(L);
    }

    static LuaValue Nil => LuaValue.Nil;

    // ------------------------------------------------------------------ gl

    void RegisterGl(LuaState L)
    {
        L.RegisterModule("gl",
            ("SelectOSDMode", a => 0),
            ("BeginScene", a =>
            {
                Osd.BeginScene();
                return 0;
            }),
            ("EndScene", a =>
            {
                Osd.EndScene();
                return 0;
            }),
            ("LoadTexture", a =>
            {
                string name = a.Str(2);
                var f = FindInResource(a.OptInt(1, 0), name);
                if (f is null)
                {
                    Emit($"Error loading texture {name}");
                    return a.Return(-1);
                }
                try
                {
                    return a.Return(Osd.AddTexture(Texture.FromZbm(f.ReadAll(), name)));
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    Emit($"Error loading texture {name}: {e.Message}");
                    return a.Return(-1);
                }
            }),
            ("CreateEmptyTexture", a => a.Return(Osd.AddTexture(new Texture(a.Int(1), a.Int(2), a.OptStr(3, "") ?? "")))),
            ("FreeTexture", a =>
            {
                Osd.FreeTexture(a.Int(1));
                return 0;
            }),
            ("SetTextureAlphaLevel", a =>
            {
                var t = Osd.GetTexture(a.Int(1));
                if (t is not null)
                {
                    t.AlphaLevel = Math.Clamp(a.Int(2), 0, 15);
                    Osd.Changed();
                }
                return 0;
            }),
            ("CreateOverlayFromTexture", a =>
            {
                var t = Osd.GetTexture(a.Int(1));
                if (t is null)
                    return a.Return(-1);
                return a.Return(Osd.CreateOverlay(t));
            }),
            ("FreeOverlay", a =>
            {
                Osd.FreeOverlay(a.Int(1));
                return 0;
            }),
            ("AddTextureToOverlay", a =>
            {
                var t = Osd.GetTexture(a.Int(2));
                if (t is not null)
                    Osd.Modify(a.Int(1), o => o.Frames.Add(t));
                return 0;
            }),
            ("RemoveTextureFromOverlay", a =>
            {
                var t = Osd.GetTexture(a.Int(2));
                if (t is not null)
                    Osd.Modify(a.Int(1), o => o.Frames.Remove(t));
                return 0;
            }),
            ("SetTextureActiveFrame", a =>
            {
                int f = a.Int(2);
                Osd.Modify(a.Int(1), o => o.ActiveFrame = f);
                return 0;
            }),
            ("SetParameters", a =>
            {
                int x = a.Int(2), y = a.Int(3), z = a.Int(4);
                bool vis = a.OptInt(5, 1) != 0;
                Osd.Modify(a.Int(1), o =>
                {
                    o.X = x;
                    o.Y = y;
                    o.Z = z;
                    o.Visible = vis;
                });
                return 0;
            }),
            ("SetZorder", a =>
            {
                int z = a.Int(2);
                Osd.Modify(a.Int(1), o => o.Z = z);
                return 0;
            }),
            ("SetVisibility", a =>
            {
                bool vis = a.Int(2) != 0;
                Osd.Modify(a.Int(1), o => o.Visible = vis);
                return 0;
            }),
            ("SetPosition", a =>
            {
                int x = a.Int(2), y = a.Int(3);
                Osd.Modify(a.Int(1), o =>
                {
                    o.X = x;
                    o.Y = y;
                });
                return 0;
            }),
            ("SetClipInfo", a => 0),
            ("GetZorder", a =>
            {
                var o = Osd.GetOverlay(a.Int(1));
                return a.Return(o?.Z ?? 0);
            }),
            ("GetVisibility", a =>
            {
                Osd.Animate(Clock.Now);
                var o = Osd.GetOverlay(a.Int(1));
                return a.Return(o is { Visible: true } ? 1 : 0);
            }),
            ("GetPosition", a =>
            {
                Osd.Animate(Clock.Now);
                var o = Osd.GetOverlay(a.Int(1));
                return a.Return(o?.X ?? 0, o?.Y ?? 0);
            }),
            ("GetSize", a =>
            {
                var o = Osd.GetOverlay(a.Int(1));
                return a.Return(o?.Width ?? 0, o?.Height ?? 0);
            }),
            ("ClearOSD", a =>
            {
                Osd.HideAll();
                return 0;
            }),
            ("Show", a =>
            {
                Osd.Shown = a.Int(1) != 0;
                return 0;
            }),
            ("HasAnimations", a =>
            {
                Poll();
                Osd.Animate(Clock.Now);
                var o = Osd.GetOverlay(a.Int(1));
                int n = 0;
                if (o is not null)
                    lock (Osd.Sync)
                        n = o.AnimationCount;
                return a.Return(n);
            }),
            ("DeleteAllAnimations", a =>
            {
                Osd.Modify(a.Int(1), o =>
                {
                    o.Animations.Clear();
                    o.TextureAnim = null;
                });
                return 0;
            }),
            ("AddPositionAnimation", a =>
            {
                var anim = new PositionAnimation
                {
                    X0 = a.Int(2), Y0 = a.Int(3), X1 = a.Int(4), Y1 = a.Int(5),
                    Start = a.Int(6), Duration = a.Int(7),
                };
                Osd.Modify(a.Int(1), o => o.Animations.Add(anim));
                return 0;
            }),
            ("AddParabolaAnimation", a =>
            {
                var anim = new ParabolaAnimation
                {
                    X0 = a.Int(2), Y0 = a.Int(3), X1 = a.Int(4), Y1 = a.Int(5),
                    Height = a.OptInt(6, 0), Start = a.OptInt(7, (int)Clock.Now), Duration = a.OptInt(8, 0),
                };
                Osd.Modify(a.Int(1), o => o.Animations.Add(anim));
                return 0;
            }),
            ("AddVisibilityAnimation", a =>
            {
                var anim = new VisibilityAnimation { Start = a.Int(2), Visible = a.Int(3) != 0 };
                Osd.Modify(a.Int(1), o => o.Animations.Add(anim));
                return 0;
            }),
            ("AddBlinkingAnimation", a =>
            {
                var anim = new BlinkAnimation { Start = a.Int(2), Period = a.OptInt(3, 500), Duration = a.OptInt(4, 0) };
                Osd.Modify(a.Int(1), o => o.Animations.Add(anim));
                return 0;
            }),
            ("AddAlphaAnimation", a =>
            {
                var anim = new AlphaAnimation { Start = a.Int(2), Kind = a.Int(3), Duration = a.Int(4) };
                Osd.Modify(a.Int(1), o => o.Animations.Add(anim));
                return 0;
            }),
            ("CreateTextureAnimation", a =>
            {
                long start = a.Int(2);
                var table = a.Table(3);
                var steps = new List<TextureAnimation.Step>();
                for (int i = 1; ; i++)
                {
                    var row = table.Get(i).AsTable;
                    if (row is null)
                        break;
                    row.Get(1).TryToNumber(out int op);
                    row.Get(2).TryToNumber(out int arg);
                    row.Get(3).TryToNumber(out int time);
                    steps.Add(new TextureAnimation.Step(op, arg, time));
                }
                var anim = new TextureAnimation(steps.ToArray(), start);
                Osd.Modify(a.Int(1), o => o.TextureAnim = anim);
                return 0;
            }),
            ("BlitOverlay", a => Blit(a)),
            ("BlitOverlayWithCR", a => Blit(a)));
    }

    int Blit(LuaArgs a)
    {
        var src = Osd.GetOverlay(a.Int(1));
        var dst = Osd.GetOverlay(a.Int(2));
        int x = a.OptInt(3, 0), y = a.OptInt(4, 0);
        if (src?.CurrentTexture is { } s && dst?.CurrentTexture is { } d)
        {
            lock (Osd.Sync)
                d.Blit(s, x, y, true);
            Osd.Changed();
        }
        return 0;
    }

    // ------------------------------------------------------------------ text and fonts

    void RegisterText(LuaState L)
    {
        L.RegisterModule("font",
            ("Load", a =>
            {
                string name = a.Str(2);
                var f = FindInResource(a.OptInt(1, 0), name);
                if (f is null)
                {
                    Emit($"Error loading font [{name}]");
                    return a.Return(-1);
                }
                var font = LoadFont(f.ReadAll(), n => FindInResource(a.OptInt(1, 0), n)?.ReadAll());
                if (font is null)
                {
                    Emit($"Error loading font [{name}]");
                    return a.Return(-1);
                }
                int id = NewId();
                _fonts[id] = font;
                return a.Return(id);
            }),
            ("Free", a =>
            {
                int id = a.Int(1);
                if (id != _builtinFontId)
                    _fonts.Remove(id);
                return 0;
            }),
            ("GetBuiltinFontID", a => a.Return(BuiltinFont())));

        L.RegisterModule("text",
            ("RenderSimple", a =>
            {
                var font = _fonts.GetValueOrDefault(a.Int(1));
                string s = a.Str(2);
                var tex = font is null ? new Texture(1, 1) : TextRenderer.RenderSimple(font, s);
                return a.Return(AddText(tex));
            }),
            ("Render", a =>
            {
                string s = a.Str(1);
                var font = _fonts.GetValueOrDefault(a.Int(2));
                var style = new TextStyle
                {
                    Width = Math.Max(1, a.Int(3)),
                    Height = Math.Max(1, a.Int(4)),
                    HAlign = a.OptInt(5, 0),
                    VAlign = a.OptInt(6, 0),
                    Tracking = (sbyte)a.OptInt(7, 0),
                    Leading = (sbyte)a.OptInt(8, 0),
                    UseColor = a.OptInt(10, 0) != 0,
                    Y = (byte)a.OptInt(11, 235),
                    Cb = (byte)a.OptInt(12, 128),
                    Cr = (byte)a.OptInt(13, 128),
                };
                var tex = font is null ? new Texture(style.Width, style.Height) : TextRenderer.Render(font, s, style);
                return a.Return(AddText(tex));
            }),
            ("Remove", a =>
            {
                if (_texts.Remove(a.Int(1), out var t))
                {
                    Osd.FreeOverlay(t.Overlay);
                    Osd.FreeTexture(t.Texture);
                }
                return 0;
            }),
            ("GetOverlayId", a => a.Return(_texts.TryGetValue(a.Int(1), out var t) ? t.Overlay : -1)));
    }

    int AddText(Texture tex)
    {
        int tid = Osd.AddTexture(tex);
        int oid = Osd.CreateOverlay(tex);
        int id = NewId();
        _texts[id] = (tid, oid);
        return id;
    }

    Font? LoadFont(byte[] dat, Func<string, byte[]?> loadSheet)
    {
        try
        {
            var font = Font.FromDat(dat);
            var sheet = loadSheet(font.SheetName);
            if (sheet is null)
                return null;
            font.Sheet = Texture.FromZbm(sheet, font.SheetName);
            return font;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
        {
            return null;
        }
    }

    int BuiltinFont()
    {
        if (_builtinFontId >= 0)
            return _builtinFontId;
        var res = EngineResources;
        var datName = res.Names.FirstOrDefault(n => n.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && n.StartsWith("f_", StringComparison.OrdinalIgnoreCase));
        Font? font = datName is null ? null : LoadFont(res.Get(datName)!, n => res.Get(n));
        if (font is null)
            return -1;
        _builtinFontId = NewId();
        _fonts[_builtinFontId] = font;
        return _builtinFontId;
    }

    // ------------------------------------------------------------------ iframes, movies, sound

    void RegisterMedia(LuaState L)
    {
        L.RegisterModule("iframe",
            ("Load", a =>
            {
                string name = a.Str(2);
                var f = FindInResource(a.OptInt(1, 0), name);
                var pic = f is null ? null : DecodeStill(f.ReadAll());
                if (pic is null)
                {
                    Emit($"Error loading I-Frame [{name}]");
                    return a.Return(-1);
                }
                int id = NewId();
                _iframes[id] = pic;
                return a.Return(id);
            }),
            ("Unload", a =>
            {
                _iframes.Remove(a.Int(1));
                return 0;
            }),
            ("Show", a =>
            {
                if (_iframes.TryGetValue(a.Int(1), out var p))
                    Video.ShowStill(p);
                return 0;
            }),
            ("ShowPredefined", a =>
            {
                // The engine's background table: launching, insert disc, black.
                string? name = a.Int(1) switch
                {
                    0 => "launching.m2v",
                    1 => "insert_disc.m2v",
                    3 => "game_wave.m2v",
                    _ => null,
                };
                var data = name is null ? null : EngineResources.Get(name);
                var pic = data is null ? null : DecodeStill(data);
                if (pic is null)
                    Video.Clear();
                else
                    Video.ShowStill(pic);
                return 0;
            }),
            ("Clear", a =>
            {
                Video.Clear();
                return 0;
            }));

        L.RegisterModule("movie",
            ("Load", a =>
            {
                string path = a.Str(1);
                var f = Disc.Find(path);
                if (f is null)
                    Emit($"Could not open file : {path} from DVD");
                return a.Return(Video.Movie.Load(f) ? 0 : -1);
            }),
            ("SetLoop", a =>
            {
                Video.Movie.Loop = a.Int(1) != 0;
                return 0;
            }),
            ("Play", a =>
            {
                Video.StartMovie();
                return 0;
            }),
            ("Stop", a =>
            {
                // Stop(1) pauses on the current picture so Resume can continue; Stop(0) ends it.
                if (a.OptInt(1, 0) != 0)
                    Video.Movie.Pause();
                else
                    Video.Movie.Stop(false);
                return 0;
            }),
            ("Resume", a =>
            {
                Video.Movie.Resume();
                return 0;
            }),
            ("GetState", a =>
            {
                Poll();
                // Keep the movie's end-of-stream bookkeeping moving even with no display attached.
                Video.Movie.WithCurrentPicture(_ => { });
                return a.Return(Video.Movie.State);
            }));

        L.RegisterModule("audio",
            ("Load", a =>
            {
                string name = a.Str(2);
                var f = FindInResource(a.OptInt(1, 0), name);
                if (f is null)
                {
                    Emit($"Error loading sound [{name}]");
                    return a.Return(-1);
                }
                try
                {
                    int id = NewId();
                    _sounds[id] = Sound.FromZwf(f.ReadAll(), name);
                    return a.Return(id);
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    Emit($"Error loading sound [{name}]: {e.Message}");
                    return a.Return(-1);
                }
            }),
            ("Unload", a =>
            {
                if (_sounds.Remove(a.Int(1), out var s))
                    Audio.StopSound(s);
                return 0;
            }),
            ("Play", a =>
            {
                if (_sounds.TryGetValue(a.Int(1), out var s))
                    Audio.Play(s, a.OptInt(2, 0) != 0);
                return 0;
            }));
    }

    static Picture? DecodeStill(byte[] data)
    {
        var dec = new Mpeg2VideoDecoder();
        dec.Feed(data);
        dec.Flush();
        Picture? last = null;
        while (dec.TryGetPicture(out var p))
            last = p;
        return last;
    }

    // ------------------------------------------------------------------ input

    void RegisterInput(LuaState L)
    {
        L.RegisterModule("input",
            ("ClearKeyQueue", a =>
            {
                Input.Clear();
                return 0;
            }),
            ("GetKey", a =>
            {
                Poll();
                if (Input.TryTake(out var e, Clock.Now))
                    return a.Return(e.Key, e.Remote, (int)e.Timestamp);
                return a.Return(InputQueue.NoKey, InputQueue.NoRemote, (int)Clock.Now);
            }),
            ("WaitForKey", a =>
            {
                while (true)
                {
                    CheckStop();
                    WaitWhilePaused();
                    if (Input.TryTake(out var e, Clock.Now))
                        return a.Return(e.Key, e.Remote, (int)e.Timestamp);
                    Input.WaitForAny(20);
                }
            }),
            ("EnableRemotes", a =>
            {
                Input.RemotesEnabled = true;
                return 0;
            }),
            ("DisableRemotes", a =>
            {
                Input.RemotesEnabled = false;
                return 0;
            }),
            ("SetMode", a =>
            {
                Input.Mode = a.Int(1);
                return 0;
            }),
            ("GetMode", a => a.Return(Input.Mode)),
            ("SetQueueSize", a =>
            {
                Input.Capacity = a.Int(1);
                return 0;
            }),
            ("SetRandomKeysTable", a =>
            {
                var t = a.OptTable(1);
                var keys = new List<int>();
                if (t is not null)
                    for (int i = 1; t.Get(i).TryToNumber(out int k); i++)
                        keys.Add(k);
                Input.SetRandomKeys(keys.ToArray());
                return 0;
            }));
    }

    // ------------------------------------------------------------------ time, maths, log, engine

    void RegisterSystem(LuaState L)
    {
        L.RegisterModule("time",
            ("GetRealTime", a =>
            {
                Poll();
                return a.Return((int)Clock.Now);
            }),
            ("Sleep", a =>
            {
                Sleep(a.Int(1));
                return 0;
            }));

        L.RegisterModule("zmath",
            ("Mod", a =>
            {
                int d = a.Int(2);
                return a.Return(d == 0 ? 0 : a.Int(1) % d);
            }),
            ("Rand", a =>
            {
                // rand() % (b - a) + a: the upper bound is exclusive.
                int lo = a.Int(1), hi = a.Int(2);
                int r = NextRand();
                int range = hi - lo;
                if (range != 0)
                    r = (int)((uint)r % (uint)range);
                return a.Return(r + lo);
            }),
            ("RandSeed", a =>
            {
                _randSeed = a.Int(1);
                return 0;
            }));

        L.RegisterModule("bit",
            ("band", a => a.Return(a.Int(1) & a.Int(2))),
            ("bor", a => a.Return(a.Int(1) | a.Int(2))),
            ("bxor", a => a.Return(a.Int(1) ^ a.Int(2))),
            ("bnot", a => a.Return(~a.Int(1))),
            ("lshift", a => a.Return(a.Int(1) << (a.Int(2) & 31))),
            ("rshift", a => a.Return((int)((uint)a.Int(1) >> (a.Int(2) & 31)))),
            ("arshift", a => a.Return(a.Int(1) >> (a.Int(2) & 31))),
            ("mod", a =>
            {
                int d = a.Int(2);
                return a.Return(d == 0 ? 0 : a.Int(1) % d);
            }));

        L.RegisterModule("log",
            ("Log", a =>
            {
                int level = a.OptInt(1, 3);
                if (level <= LogLevel)
                    Emit(a.OptStr(2, "")!.TrimEnd('\n'));
                return 0;
            }),
            ("SetLevel", a => 0),
            ("SetModule", a => 0),
            ("DebugSetState", a => 0),
            ("PrintMemStats", a => 0),
            ("PrintRaw", a =>
            {
                if (LogLevel >= 3)
                    Emit(a.OptStr(1, "")!.TrimEnd('\n'));
                return 0;
            }),
            ("PrintLine", a =>
            {
                if (LogLevel >= 5)
                    Emit(a.OptStr(1, "")!.TrimEnd('\n'));
                return 0;
            }));

        L.RegisterModule("engine",
            ("ZMM_SetLeakDebugMode", a => 0),
            ("ZMM_SetCheckPoint", a => 0),
            ("ZMM_VerifyCheckPoint", a => 0),
            ("ZMM_GetTotalAllocMemory", a => a.Return(6 * 1024 * 1024)),
            ("ZMM_GetTotalFreeMemory", a => a.Return(10 * 1024 * 1024)),
            ("ZMM_GetMaxFreeMemory", a => a.Return(8 * 1024 * 1024)),
            ("DR_SetEnable", a => 0),
            ("DR_SetDebug", a => 0),
            ("DR_SetAbsoluteCapping", a => 0),
            ("DR_ClearPairCappings", a => 0),
            ("DR_AddPairCapping", a => 0),
            ("OpenTray", a => Eject(a)),
            ("EjectTray", a => Eject(a)),
            ("CloseTray", a =>
            {
                _trayState = 0;
                return 0;
            }),
            ("GetTrayState", a => a.Return(_trayState)),
            ("SoftwareReset", a =>
            {
                RequestSoftwareReset();
                return 0;
            }),
            ("WaitForDisk", a => 0),
            ("GetNumOverlays", a => a.Return(Osd.OverlayCount)),
            ("GetNumTextures", a => a.Return(Osd.TextureCount)),
            ("Version", a => a.Return(string.IsNullOrEmpty(Info.EngineVersion) ? "1.00" : Info.EngineVersion)),
            ("SetLineTraceMode", a => 0),
            ("GetLineTraceMode", a => a.Return(0)));

        L.RegisterModule("spi", ("Init", a => 0), ("Open", a => 0), ("Close", a => 0), ("Write", a => 0), ("Read", a => a.Return(0)));
        L.RegisterModule("uart", ("Open", a => 0), ("Close", a => 0), ("Write", a => 0), ("Read", a => a.Return(0)), ("TxD", a => 0), ("RxD", a => a.Return(0)));
        L.RegisterModule("exp_int", ("Configure", a => 0), ("Close", a => 0), ("Test", a => a.Return(0)));
    }

    int NextRand()
    {
        // The C library's rand(): a linear congruential generator returning 15 bits.
        _randSeed = unchecked(_randSeed * 1103515245 + 12345);
        return (_randSeed >> 16) & 0x7FFF;
    }

    /// <summary>
    /// engine.OpenTray: the game is finished with this disc (the two-disc Rewind sets ask for
    /// the other one this way). The console shows its "insert disc" screen and the game stops;
    /// the host then closes the tray on the same disc or puts in another.
    /// </summary>
    int Eject(LuaArgs a)
    {
        _trayState = 1;
        OpenTray();
        return 0;
    }

    // ------------------------------------------------------------------ resources, files, saves

    void RegisterData(LuaState L)
    {
        L.RegisterModule("rm",
            ("OpenResource", a =>
            {
                int id = NewId();
                _resources[id] = DiscPath.Normalize(a.Str(1));
                return a.Return(id);
            }),
            ("CloseResource", a =>
            {
                _resources.Remove(a.Int(1));
                return 0;
            }),
            ("LoadFile", a => a.Return(-1)),
            ("UnloadFile", a => 0));

        L.RegisterModule("zfile",
            ("OpenFile", a =>
            {
                string name = a.Str(1);
                var f = a.IsNoneOrNil(3) ? Disc.Find(name) : FindInResource(a.Int(3), name);
                if (f is null)
                {
                    Emit($"Could not open file : {name} from DVD");
                    return a.Return(-1);
                }
                int id = NewId();
                _files[id] = (f, f.Open());
                return a.Return(id);
            }),
            ("ReadBytes", a =>
            {
                if (!_files.TryGetValue(a.Int(1), out var f))
                    return a.Return(Nil);
                int offset = a.Int(2), count = a.Int(3);
                f.Stream.Position = Math.Clamp(offset, 0, f.Stream.Length);
                var buf = new byte[Math.Max(0, count)];
                int n = f.Stream.ReadAtLeast(buf, buf.Length, false);
                if (n < buf.Length)
                    Array.Resize(ref buf, n);
                // A pointer buffer, read back with the pointer module.
                return a.Return(LuaValue.Userdata(new LuaUserdata(buf)));
            }),
            ("ReadLine", a =>
            {
                if (!_files.TryGetValue(a.Int(1), out var f))
                    return a.Return(Nil);
                var sb = new StringBuilder();
                int c;
                while ((c = f.Stream.ReadByte()) >= 0)
                {
                    if (c == '\n')
                        break;
                    if (c != '\r')
                        sb.Append((char)c);
                }
                if (c < 0 && sb.Length == 0)
                    return a.Return(Nil);
                return a.Return(sb.ToString());
            }),
            ("SetHeadPosition", a =>
            {
                if (_files.TryGetValue(a.Int(1), out var f))
                    f.Stream.Position = Math.Clamp(a.Int(2), 0, f.Stream.Length);
                return 0;
            }),
            ("GetFileSize", a => a.Return(_files.TryGetValue(a.Int(1), out var f) ? (int)f.File.Length : 0)),
            ("CloseFile", a =>
            {
                if (_files.Remove(a.Int(1), out var f))
                    f.Stream.Dispose();
                return 0;
            }));

        L.RegisterModule("dict",
            ("Load", a =>
            {
                string name = a.Str(2);
                var f = FindInResource(a.OptInt(1, 0), name);
                if (f is null)
                {
                    Emit($"Cannot open dictionary : {name}");
                    return a.Return(-1);
                }
                int id = NewId();
                _dictionaries[id] = WordList.Load(f.ReadAll());
                return a.Return(id);
            }),
            ("Unload", a =>
            {
                _dictionaries.Remove(a.Int(1));
                return 0;
            }),
            ("Lookup", a =>
            {
                if (!_dictionaries.TryGetValue(a.Int(1), out var d))
                    return a.Return(false);
                return a.Return(d.Contains(a.Str(2)));
            }));

        L.RegisterModule("pointer",
            ("CreateUserData", a => a.Return(LuaValue.Userdata(new LuaUserdata(new byte[Math.Max(0, a.Int(1))])))),
            ("DestroyUserData", a => 0),
            ("ToString", a =>
            {
                var b = Bytes(a, 1);
                int end = Array.IndexOf(b, (byte)0);
                return a.Return(Encoding.Latin1.GetString(b, 0, end < 0 ? b.Length : end));
            }),
            ("ToStringRange", a =>
            {
                var b = Bytes(a, 1);
                // From an offset up to, not including, an end offset.
                int start = Math.Clamp(a.Int(2), 0, b.Length);
                int end = Math.Clamp(a.Int(3), start, b.Length);
                return a.Return(Encoding.Latin1.GetString(b, start, end - start));
            }),
            ("FromString", a => a.Return(LuaValue.Userdata(new LuaUserdata(Encoding.Latin1.GetBytes(a.Str(1)))))),
            ("GetIndex", a =>
            {
                var b = Bytes(a, 1);
                int i = a.Int(2);
                return a.Return(i >= 0 && i < b.Length ? b[i] : 0);
            }),
            ("GetU32MSB", a => a.Return(ReadU32(a, true))),
            ("GetU32LSB", a => a.Return(ReadU32(a, false))),
            ("SetU32MSB", a => WriteU32(a, true)),
            ("SetU32LSB", a => WriteU32(a, false)),
            ("ByteToAscii", a => a.Return(((char)(a.Int(1) & 0xFF)).ToString())),
            ("AsciiToByte", a =>
            {
                string s = a.Str(1);
                return a.Return(s.Length > 0 ? s[0] & 0xFF : 0);
            }));

        L.RegisterModule("eeprom",
            ("EnumerateGameSavesByName", a =>
            {
                _enumeratedSaves = Saves.Enumerate(a.Str(1));
                return a.Return(_enumeratedSaves.Count, Nil);
            }),
            ("EnumerateGameSavesByID", a =>
            {
                int id = a.Int(1);
                _enumeratedSaves = Saves.Slots.Where(s => s.Id == id).ToList();
                return a.Return(_enumeratedSaves.Count, Nil);
            }),
            ("GetSaveNameByID", a =>
            {
                int i = a.Int(1);
                if (i < 0 || i >= _enumeratedSaves.Count)
                    return a.Return(Nil, Nil);
                return a.Return(_enumeratedSaves[i].GameName, _enumeratedSaves[i].SlotName);
            }),
            ("LoadSaveByID", a =>
            {
                int i = a.Int(1);
                if (i < 0 || i >= _enumeratedSaves.Count)
                    return a.Return(Nil, true);
                var copy = (byte[])_enumeratedSaves[i].Data.Clone();
                return a.Return(LuaValue.Userdata(new LuaUserdata(copy)), Nil);
            }),
            ("UnloadData", a => 0),
            ("SaveGameToExistingSlot", a =>
            {
                int i = a.Int(1);
                if (i < 0 || i >= _enumeratedSaves.Count)
                    return a.Return(true);
                Saves.Update(_enumeratedSaves[i], Bytes(a, 2));
                return a.Return(Nil);
            }),
            ("SaveGameToNewSlot", a =>
            {
                int id = a.Int(1);
                int size = a.Int(2);
                string game = a.Str(3);
                string slot = a.Str(4);
                var data = Bytes(a, 5);
                var copy = new byte[Math.Max(0, size)];
                Array.Copy(data, copy, Math.Min(copy.Length, data.Length));
                Saves.Add(id, game, slot, copy);
                return a.Return(Nil);
            }),
            ("Format", a =>
            {
                Saves.Clear();
                return a.Return(Nil);
            }),
            ("CorruptFlash", a => 0),
            ("CheckFlashIntegrity", a => a.Return(Nil)));
    }

    static byte[] Bytes(LuaArgs a, int n)
    {
        if (a[n].AsUserdata?.Payload is byte[] b)
            return b;
        if (a[n].IsString)
            return Encoding.Latin1.GetBytes(a[n].AsString!);
        throw a.ArgError(n, "userdata expected");
    }

    static int ReadU32(LuaArgs a, bool bigEndian)
    {
        var b = Bytes(a, 1);
        int o = a.Int(2);
        if (o < 0 || o + 4 > b.Length)
            return 0;
        return bigEndian ? BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(o)) : BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
    }

    static int WriteU32(LuaArgs a, bool bigEndian)
    {
        var b = Bytes(a, 1);
        int o = a.Int(2);
        int v = a.Int(3);
        if (o >= 0 && o + 4 <= b.Length)
        {
            if (bigEndian)
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(o), v);
            else
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        }
        return 0;
    }
}
