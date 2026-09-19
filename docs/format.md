# How a Game Wave disc works

This is what `gamewave` implements, worked out from the sixteen retail discs and from the
engine image every disc carries (`app_sdram_3.cat.bin`, big-endian MIPS code loaded at
`0x80600000`, built around a Cirrus Logic "Maui" DVD decoder). Where the emulator's
behaviour rests on reading that engine code rather than on the games' own use of a
function, this says so.

## The disc

A Game Wave disc is an ordinary DVD with a **UDF 1.02** file system (the retail discs were
mastered with Nero). There is no DVD-Video structure: the console's own firmware boots
whatever the disc describes in `gamewave.diz`, an INI file:

```ini
[global]
appname=Zap21
appfile=/data/game.zbc
version=1.05.5blalj

[platform]
board=3
engine=/data/app_sdram_3.cat.bin
version=1.01.3.5bpa1s
```

`appfile` is the game program. `engine` is the console software for board revision 3,
the retail console; some discs list a second `[platform]` for revision "a". Every disc
brings its own engine, so the engine version varies from disc to disc (see
[Engine versions](#engine-versions)).

File names on disc and in the programs disagree about case (`/Data/game.zbc` against a
`data` directory), so lookups ignore case, as the console's did.

The rest of a disc is:

| Kind | Files | What they are |
|------|-------|---------------|
| Movies | `*.mpg` | MPEG-2 program streams: 720 x 480 video at 29.97 frames a second, MPEG-1 Layer II sound at 44.1 kHz (one bumper at 48 kHz) |
| Stills | `*.m2v` | One MPEG-2 I-frame, shown as a background |
| Images | `*.zbm` | Bitmaps for the on-screen display |
| Sounds | `*.zwf` | Sound effects |
| Fonts | `*.dat` + `*.zbm` | Bitmap fonts: metrics and a glyph sheet |
| Data | anything | Read by the game itself: Sudoku's puzzle sets, Letter Zap's dictionaries |

## The game program

`.zbc` is **Lua 5.0 bytecode**, usually inside a zlib container:

```
0   4  1B 'Z' 'C' 'S'
4   4  0A 1A 00 00
8   4  uncompressed size (little endian)
12  4  compressed size
16  .. zlib data
```

Three discs store it uncompressed. The bytecode is stock Lua 5.0 with two changes to the
header: the signature reads `1B 'Z' 'B' 'C' 0A 1A` instead of `1B 'L' 'u' 'a'`, and three
bytes (`01 00 01`) sit between the version byte and the usual size fields. Those fields
declare 4-byte ints, sizes and instructions, the standard 6/8/9/9 instruction layout and
a **4-byte integer `lua_Number`**: the header's test number is 31415926, the integer
truncation of 3.14159265358979e7. All arithmetic is 32-bit integer arithmetic, division
truncates, and numbers print as integers.

The opcodes, their numbering and the function layout are unchanged, so the programs keep
their line information and local names. The main chunk, named `=(zapcompiler)`, is a list
of closures, one per source file (`@utils.zsc`, `@opening_screen.zsc`, ...), each called
once.

The Lua libraries are 5.0's `base` (with `coroutine`), `table`, `string` and `debug`.
There is no `math`, `io` or `os`, and no compiler: `loadstring` is present but unused.

## The engine's modules

Everything else a game does goes through these modules. The emulator reimplements each
function; the notes are about the behaviour the games rely on.

| Module | Functions |
|--------|-----------|
| `gl` | The on-screen display: textures, overlays, animations, scenes |
| `text`, `font` | Text rendered into overlays with bitmap fonts |
| `iframe` | Still backgrounds |
| `movie` | Movie playback |
| `audio` | Sound effects |
| `input` | The remotes' key queue |
| `time` | The millisecond clock and sleeping |
| `zmath`, `bit` | Integer maths the language lacks |
| `rm`, `zfile`, `dict` | Resource folders, raw files, dictionaries |
| `pointer`, `eeprom` | Byte buffers and the save memory |
| `engine`, `log` | Memory statistics, the disc tray, reset, logging |
| `spi`, `uart`, `exp_int` | Hardware interfaces no retail game uses |

### Screen

The picture is 720 x 480. Under the on-screen display (OSD) is a video plane showing a
movie, a still, or black.

**Overlays** are the OSD's sprites. `gl.LoadTexture` makes a texture from a `.zbm`,
`gl.CreateOverlayFromTexture` an overlay showing it, hidden at the origin.
`gl.SetParameters(oid, x, y, z, visible)` places it; higher `z` is in front. An overlay
can hold several textures and show one (`AddTextureToOverlay`, `SetTextureActiveFrame`).

Changes between `gl.BeginScene()` and `gl.EndScene()` appear together, so the display
keeps showing the last complete scene until the batch ends.

**Animations** run against the millisecond clock:

- `AddPositionAnimation(oid, x0, y0, x1, y1, start, duration)` moves in a straight line.
- `AddAlphaAnimation(oid, start, kind, duration)` fades in (1) or out (2). The opacity
  stays where the fade left it; the games follow a fade out with a 10 ms fade in when they
  want an overlay back.
- `AddVisibilityAnimation(oid, time, visible)` shows or hides at a time.
- `CreateTextureAnimation(oid, start, steps)` runs a frame script over the overlay's
  textures: `{1, frame, ms}` shows a frame for a time, `{3, index}` jumps to a step (0
  based), `{2}` ends.
- `HasAnimations(oid)` counts what is still running; games spin on it.

### Images

`.zbm` is a 48-byte little-endian header and zlib data:

```
0   1, 1
8   pixel format (4), bytes per pixel (2)
16  width, height
24  0, 0
32  frame count (1)
36  compressed size, raw size, 0
```

Pixels are **16-bit A4 Y6 Cb3 Cr3**, big endian, stored in 32-bit words that hold two
pixels with the second one first: pixel *i* is at byte `2 * (i ^ 1)`. Luma scales by 4 and
chroma by 32 around a centre of 4: font glyphs are y = 59, Cb = Cr = 4, which is studio
white (236, 128, 128). Alpha scales from 0..15. One font on *Click!* uses a 32-bit A Y Cb Cr
format (100, 4 bytes per pixel). `gl.SelectOSDMode("4633")` names the 16-bit mode.

### Text

A font is a `.dat` of metrics and a `.zbm` glyph sheet:

```
0x000 128  family name           0x180 128  style
0x080 128  PostScript name       0x200 128  glyph sheet file name
0x100 128  script
0x280  20  glyph count, first character, last character, cell height, kerning pairs
then 32 bytes a glyph: cell left, top, right, bottom in the sheet, advance,
     left bearing, ink width, right bearing
then 12 bytes a kerning pair: first character, second character, adjustment
```

A cell already includes its left bearing, so a glyph is drawn by copying its cell at the
pen and advancing by the advance plus kerning.

`text.Render(string, font, width, height, halign, valign, line, word, char, colour, Y, Cb,
Cr, flag)` renders into a new texture and overlay: `halign` 0 left, 1 centre, 2 right;
`valign` 0 top, 1 middle, 2 bottom. The three signed bytes that follow are extra pixels
between lines, added to each space, and between characters (from the engine's layout
code, which reads them at offsets 22, 21 and 20 of its parameter block). With `colour` set,
the glyphs take the Y Cb Cr colour; otherwise they keep the sheet's. `text.GetOverlayId`
gives the overlay, `text.Remove` frees both.

The built-in font (`font.GetBuiltinFontID`) is Arial 22, stored in the engine image.

### Stills and predefined backgrounds

`iframe.Load` decodes an `.m2v`; `iframe.Show` puts it on the video plane.
`iframe.ShowPredefined(n)` picks from the engine's background table: 0 "Launching game",
1 "Please insert disc", 2 plain black. The two pictures live in the engine image, in an
archive that also holds the built-in font:

```
0   8  12 34 56 78 87 65 43 21
8   4  entry count (big endian)
12  48 per entry: name[40], offset, size (big endian, from the archive start)
```

### Movies

`movie.Load(path)` returns 0 if the file is there and -1 if not, and clears the loop flag.
`movie.SetLoop(1)` makes the next `movie.Play()` loop. `movie.GetState()` is 1 while a
movie plays (or is paused) and 0 once it has ended or been stopped; games poll it in tight
loops. `movie.Stop(1)` pauses on the current picture and `movie.Resume()` continues;
`movie.Stop(0)` stops. The last picture stays on the video plane until something replaces
it.

The loop flag matters more than it looks: Zap 21 plays its looping menu movie, then its
remote activation movie, and waits for that one to end. If the flag survived `Load`, the
second movie would loop and the game would wait forever. The engine's `Load` zeroes the
same field `SetLoop` writes.

The movies are clean MPEG-2 main profile, frame pictures, interlaced (top field first)
apart from a few progressive intros. The emulator's decoder matches a reference decoder
to 64 dB or better on every disc; the difference is IDCT rounding.

### Sound

`.zwf` is a 20-byte header and zlib-compressed 16-bit big-endian PCM:

```
0   4  02 EE 90 7C
4   4  sample count
8   4  channels (1)
12  4  compressed size
16  4  a checksum
```

The rate is not stored. It is 44.1 kHz: Zap 21 plays its 102 496-sample "round over" and
holds the screen for `Sleep(2000)`, which fits 2.3 s and not 4.6 s.

`audio.Play(id, loop)` mixes over the movie soundtrack.

### Input

A Game Wave has up to six colour remotes, numbered red 1, yellow 2, blue 3, green 4,
purple 5, orange 6 (255 is no remote). Keys are 0-9, up 10, down 11, right 12, left 13,
select 14, DVD menu 15, A-D 16-19, game menu 20 (255 is no key).

`input.GetKey()` returns key, remote and timestamp without waiting; `input.WaitForKey()`
waits. The games poll `GetKey` in loops with no sleep, which the console's slow processor
made harmless. `input.SetMode(1)` with `input.SetRandomKeysTable` is an "auto" soak-test
mode that invents key presses.

### Time and maths

`time.GetRealTime()` is milliseconds; `time.Sleep(ms)` blocks. `zmath.Rand(a, b)` is
`a + rand() % (b - a)`, so the upper bound is exclusive (from the engine code); `zmath.Mod`
is C's `%`.

### Files

`rm.OpenResource(folder)` returns an id that later loads resolve names against.
`zfile.ReadBytes(file, offset, count)` returns a `pointer` buffer, not a string;
`pointer.ToStringRange(buffer, from, to)` takes an exclusive end, which is how Sudoku
slices `16,17,12,...` into numbers.

### Letter Zap's dictionaries

`.zdt` is a trie after an 8-byte header. A node is a child count and that many entries;
an entry is a byte with the letter in the low five bits (1 is 'a'), 0x20 when a word ends
there, and in the top two bits how many little-endian bytes of child offset follow. The
offset counts from the start of the node the entry is in. (From the engine's lookup
routine.)

### Save memory

The console keeps saves in flash, as named slots under a game name.
`eeprom.EnumerateGameSavesByName(game)` lists a game's slots for the calls that take a
slot index: `GetSaveNameByID`, `LoadSaveByID` (a `pointer` buffer),
`SaveGameToExistingSlot`. `SaveGameToNewSlot(id, size, game, slot, buffer)` makes one.
Every call's error result is nil on success. The emulator keeps all slots in one file.

### The disc tray

`engine.OpenTray()` is how a game says it is done with its disc; the two-disc *Rewind*
sets use it at the end of a game. The console shows "Please insert disc", and whatever
goes in is booted.

## Engine versions

Newer engines add functions older discs never call: `log.DebugSetState`, `log.PrintMemStats`
and the `bit` library (`band`, `bor`, `bxor`, `bnot`, `lshift`, `rshift`, `arshift`,
`mod`). *Gemz* and *VeggieTales* call `log.DebugSetState` before anything else, and
*Gemz* packs its saves with `bit`. The emulator offers the union of every version.
