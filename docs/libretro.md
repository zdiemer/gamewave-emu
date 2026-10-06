# Libretro core

`GameWave.Libretro` publishes the emulator as a native libretro v1 shared library.
It includes the engine and audio decoder and needs neither SDL2 nor an installed
.NET runtime. No BIOS is required.

## Build and install

Install .NET SDK 10 and the [Native AOT build prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#prerequisites).
On Windows this means Visual Studio's **Desktop development with C++** workload.
On Ubuntu install `clang` and `zlib1g-dev`; on macOS install Xcode command line tools.
Publish on the target operating system:

```sh
dotnet publish src/GameWave.Libretro -c Release -r win-x64 -o artifacts/libretro/win-x64
```

Use `linux-x64`, `linux-arm64`, `osx-x64` or `osx-arm64` for those targets.
The output library is `gamewave_libretro.dll` on Windows,
`gamewave_libretro.so` on Linux, or `gamewave_libretro.dylib` on macOS.
Windows x64 is tested locally. CI builds and runs the managed suite and native
ABI, determinism, and frontend-interface probes on all five targets. Linux x64
also has headless validation in Ubuntu 24.04 under WSL2. Real RetroArch, desktop
audio/controllers, and netplay remain untested on Linux and macOS; macOS and
Linux ARM64 have no manual game validation.

Tagged releases package the Windows core and its info file as
`gamewave-libretro-windows-x86_64.zip`.
Experimental Linux and macOS downloads are named
`gamewave-libretro-PLATFORM-experimental.tar.gz`, with `PLATFORM` set to
`linux-x86_64`, `linux-arm64`, `macos-x86_64`, or `macos-arm64`.
Each includes an `EXPERIMENTAL.txt` warning; macOS libraries are signed ad hoc,
not notarized. These packages need validation on their respective systems.

Copy the library to your frontend's cores directory and
`gamewave_libretro.info` to its core info directory. In RetroArch these locations
are shown under **Settings > Directory > Cores / Core Info**.
Choose **Load Core**, then **Load Content**, or launch directly:

```sh
retroarch -L artifacts/libretro/win-x64/gamewave_libretro.dll "Some Disc.iso"
```

The core follows the [libretro API](https://docs.libretro.com/development/cores/developing-cores/):
the frontend owns video, audio, controller assignment, pausing, and frame pacing.
Output is 720 × 480 XRGB8888, presented at 60 Hz with a 4:3 display aspect,
and 44.1 kHz signed 16-bit stereo audio. MPEG movies retain their source frame rate.

## Content and disc swapping

Supported content:

- `.iso`: a Game Wave UDF DVD image.
- `.zip`: an archive containing an ISO. The core handles extraction itself.
- `gamewave.diz`: select this file to load an already extracted disc directory.
- `.m3u`: a list of discs, one path per line, relative to the playlist. Empty
  lines and lines beginning with `#` are ignored. Entries can also be ZIPs or
  `gamewave.diz` files.

For example, save this as `Rewind 2005.m3u` beside the two images:

```text
#LABEL:Disc A
Rewind 2005 (USA) (Disc A).iso
#LABEL:Disc B
Rewind 2005 (USA) (Disc B).iso
```

When a game asks for its other disc, use the frontend's **Disk Control** menu:
eject if needed, select the other disc index, and insert it. Inserting the same
disc boots it again. The core also supports adding and replacing disc entries
through that menu. Selecting the index after the last entry leaves the drive empty.
Modern frontends can display disc labels and full paths and remember the initial
disc. Playlists accept `#LABEL:` and `#EXTINF:duration,label` before an entry;
unlabelled entries use their file name. Older frontends retain the basic interface.

ZIP images are unpacked synchronously during loading into
`<save directory>/gamewave/unpacked/`; allow time and disk space for a DVD image.
The cache keeps three images. Loading an ISO directly avoids unpacking.

## Remotes

Ports 1–6 are red, yellow, blue, green, purple and orange. Set each participating
port to **Game Wave remote** or a RetroPad; choose **None** to disconnect it.
Configure the physical devices in the frontend. Games usually ask each player
to press SEL to join.

| Remote key | RetroPad |
|---|---|
| Up / Down / Left / Right | D-pad |
| SEL | Start or R |
| A / B / C / D | A / B / X / Y |
| Game menu | Select |
| DVD menu | L |
| 1 / 2 / 3 / 4 | Hold R2 and press A / B / X / Y |
| 5 / 6 / 7 / 8 | Hold R2 and press Up / Right / Down / Left |
| 9 / 0 | Hold R2 and press L / R |

The labels above use libretro button names. Your physical controller's labels
may differ. Directions repeat after 350 ms, then every 100 ms of emulated time.
Other buttons produce one press until released.

The optional keyboard callback supports arrows, Enter/Space (SEL), A–D,
number row/keypad, G (game menu) and Home (DVD menu). The **Keyboard remote**
core option chooses a colour by its port number, 1–6. Ctrl/Alt/Meta shortcuts
are ignored. Disable RetroArch's game focus mode only when you want its hotkeys;
enable game focus when you want the frontend to send the full keyboard to the core.
Avoid binding a key both to the frontend's RetroPad and the core keyboard callback,
which can produce two presses.

The **Deinterlacing** option selects `blend` (default) or `off`.
Modern frontends group these options under **Video** and **Input**, with help text
and value labels. Core options v1 and legacy frontends use the same keys and defaults.

## Saves and limits

High scores and settings persist automatically in
`<frontend save directory>/gamewave/gamewave.saves`. This file stores every
game's named flash slots, including both discs of a set. It uses the standalone
emulator's save format. Back up this file to preserve saves. When the frontend
does not supply a save directory, the core uses the user's local application data
directory under `gamewave-libretro`.

The core also exposes a stable 4 MiB `RETRO_MEMORY_SAVE_RAM` image. RetroArch can
load and save it as the content's `.srm` file, autosave it, edit it through its
memory tools, and synchronize it when joining netplay. A frontend-loaded image
replaces the console's flash slots before the next frame; an all-zero image clears
them. Malformed images are rejected without changing the running game. Each image
contains all console flash slots, while the frontend names its file per content.
Back up both the `.srm` and `gamewave.saves` files when using both save mechanisms;
a frontend restore takes precedence over the console file.

The SRAM image begins with `GWSRAM1\0`, a little-endian 32-bit payload length,
and four reserved zero bytes. At offset 16 is the standalone `GWSAVE1\0` save-store
payload; the remaining buffer is zero padding. This is a flash container, not a
hardware CPU address space. The memory-map interface describes this SRAM region
with the `SRAM` address-space name and save-RAM flag.

Use the frontend's **Save State** and **Load State** commands for portable snapshots.
States restore Lua execution, graphics and animations, queued input and remote repeat
timing, sound effects, movie position, open files, and the console's flash slots.
They can be loaded after resetting the game or restarting the frontend. Loading a
state also restores `gamewave.saves`, including high scores and settings.

Keep the same disc content and core state-format version. For a playlist, insert
the saved disc at its original index before loading. Saving requires a running
game with the tray closed. Damaged, truncated, incompatible and wrong-disc
states are rejected before replacing the running game.

The serialization buffer stays at 16 MiB for a loaded session. The state contains
a compressed, checksummed payload and a zero-filled tail; enable frontend state
compression to keep files small. The uncompressed payload limit is 128 MiB.
New states capture the pending clock step, input random generator, MPEG decoder,
buffered pictures, and movie audio. Version 1 states remain readable for ordinary
loads. Game-script or loading errors appear as frontend messages.

## Rewind and runahead

The core advertises deterministic states. Install the updated `.info` file so
RetroArch exposes its rewind and runahead menus. Enable **Settings > Frame Throttle
> Rewind** and bind the rewind hotkey. A larger rewind buffer (for example, 200 MiB)
holds more history than the default buffer; increasing rewind granularity reduces
the cost of taking states. Enable **Settings > Latency > Run-Ahead** for runahead.
Start with one frame. Movie decoding and state capture add CPU and memory costs.

Speculative frames still run the game, decoder, audio mixer, and animations when
the frontend disables output. They cannot write the console's flash save file.
Rollback restores queued input, remote repeat timing, graphics, decoder state,
audio, and flash slots. The core sends a duplicate-frame notification when video
output is disabled, keeping frontend frame accounting active.

Protected calls, metamethods, sort comparisons, table iterators, generic-for
iterators, and string replacement callbacks retain their continuation in the Lua
stack, including callbacks that sleep or wait for input. Loading resumes the
callback without repeating earlier comparisons, table mutations, or output.
Lua object strings use saved deterministic identities across rollback and fresh
instances. Portable state version 4 also retains table tombstones so iterators
can continue after clearing fields. It still reads versions 1, 2 and 3. Rewind cannot
cross a host disc change. Save-state history belongs to the inserted disc.

## Cheats

Enable codes through the frontend's core cheat interface:

- `lua:global.field.1=123` sets an existing integer global or table entry, using
  dot-separated field names and positive numeric table indices. Values are signed
  decimal integers. For example, `lua:score=999` works if that game has a numeric
  `score` global; names depend on each game's program.
- `sram:HEX_OFFSET:8|16|32:HEX_VALUE` writes a little-endian value into the exposed
  flash image. For example, `sram:0020:8:FF` writes one byte at hexadecimal offset
  `20`. Find the intended slot data in that game's image first; container lengths
  and names mean that slot offsets vary. Offsets below `10` (hex) are protected.
  Changes that damage the save-store structure are rejected.

Cheats apply at emulation boundaries and are reapplied while enabled. Codes can
be replaced, disabled, or reset by index. Cheat configuration belongs to the
frontend and is not stored in save states. These formats do not emulate hardware
CPU patches or Game Genie codes.

## Frontend I/O and logging

With a complete VFS v3 interface, all disc, playlist, ZIP cache and console flash
I/O goes through the frontend's filesystem. If that interface is unavailable, the
core uses ordinary OS files. A path denied by an active VFS stays denied. ZIP cache
identity uses archive-entry CRC and length when frontend file timestamps are
unavailable, so replacing an archive invalidates stale extraction data.

The libretro log callback receives informational game output and error messages.
Worker output is queued and delivered on the frontend thread, with a fixed format
string. Errors also appear through the frontend message interface.

## Netplay

The core supports frontend rollback netplay with synchronized RetroPad input,
portable deterministic states, and initial SRAM synchronization. In RetroArch,
load the same core build and identical disc content on each peer, use matching
core options, then host or connect through the **Netplay** menu. Assign players
to the six RetroPad ports and press SEL to join as usual. Keep cheats disabled
and use RetroPad controls for multiplayer. Disc changes require coordinating all
peers and starting a new session.

Rollback frames keep disk saves and exposed SRAM at the last committed frame;
the next presented frame exports the corrected flash data. Netplay state buffers
and movie replay can use considerable memory and CPU. Windows x64 with RetroArch
1.22.2 has been tested with two peers and added network latency. Compatibility
between operating systems or CPU architectures still needs validation.

## Frontend features

| Feature | Status |
|---|---|
| Extended disk control | Disc labels, paths, and validated frontend-selected initial disc; legacy interface fallback. |
| Frontend-managed save RAM | Stable 4 MiB SRAM buffer; frontend loads, autosaves and netplay SRAM imports are applied before emulation. |
| Cheats and memory maps | SRAM memory descriptor plus flash-offset and named Lua-integer cheats. The high-level engine has no hardware CPU RAM map. |
| Core options v2 | Categorized English options with descriptions and labels; v1 and legacy fallback. |
| VFS and log interfaces | VFS v3 for discs, playlists, ZIP cache and flash files; OS fallback when unavailable. Queued structured logs on the frontend thread, with error-message fallback. |
| Netplay | RetroArch host/client SRAM and state synchronization, delayed controller input, rollback, and frame CRC checks validated on Windows x64. |

Rumble, analog controls, hardware rendering, and microphone input have no Game
Wave device counterpart in the current emulator.

Native AOT [does not support unloading its runtime](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries).
The core retains the native module in memory until the frontend exits. Unloading
content or deinitializing stops the console and releases disc resources; loading
the core again in the same process is supported. Replacing the DLL after a rebuild
requires closing the frontend first.

## Verification

Windows x64 validation includes the native ABI smoke test and a 3600-frame run
of Zap 21 in RetroArch 1.22.2 with its SDL audio driver. The retail-content probe
also verifies video, audio and SEL input through the native library for one minute.
Save-state validation includes movie rewind and restoration after core reload in
the retail probe, and RetroArch auto-save/auto-load across process restarts.
RetroArch 1.22.2 also restores rewind history through its rewind command and
completes runs with single- and secondary-instance runahead enabled. The native
determinism probe compares video, PCM, and subsequent
state bytes after replay and speculative rollback, walks saved history backwards,
and checks that speculative flash changes stay off disk. Zap 21 passes these
checks during its intro movies and menu. Managed tests use an original MPEG test
pattern to exercise resampling, pause, end of playback, and looping without a disc.
They also restore blocking Lua callbacks into both the original machine and a
fresh instance, preserving callback output, table traversal and state bytes.

```sh
dotnet test -c Release
```

`tests/libretro/smoke.c` uses the vendored upstream libretro header and loads the
published native library through the OS loader. It creates original synthetic
disc content and checks all required exports, failed loads, frame/audio callbacks,
six remotes, number chords, keyboard input, reset, save-file creation, disc swapping,
and unload/reload. State checks include saving before the first frame, independent
snapshots, corruption rejection, restoration after reset and core reload, and
loading a saved file in a separate process. No retail disc is needed for this test.

From an x64 Visual Studio developer prompt after publishing:

```bat
cl /nologo /std:c11 /W4 /WX /O2 tests\libretro\smoke.c /Fe:artifacts\libretro\smoke.exe /Fo:artifacts\libretro\smoke.obj
artifacts\libretro\smoke.exe "%CD%\artifacts\libretro\win-x64\gamewave_libretro.dll" "%CD%\artifacts\libretro\smoke-data"
```

On Linux after publishing:

```sh
cc -std=c11 -Wall -Wextra -Werror -O2 tests/libretro/smoke.c -ldl -o artifacts/libretro/smoke
artifacts/libretro/smoke "$PWD/artifacts/libretro/linux-x64/gamewave_libretro.so" "$PWD/artifacts/libretro/smoke-data"
```

`tests/libretro/content.c` is an optional probe for your own retail disc. Compile
it with the same compiler flags as `smoke.c`, then run
`content CORE OUTPUT_DIRECTORY DISC [FRAMES]` with absolute paths. It runs at
60 Hz, checks changing video and audible samples, presses SEL after 40 and 50
seconds, rewinds an intro movie, restores a later state after core deinitialization
and reload, and writes BMP screenshots every 600 frames. The default is 3600 frames
(one minute), followed by 120 restored frames. Retail disc images remain outside
the repository.

`tests/libretro/determinism.c` runs without wall-clock pacing. Compile it with the
same flags as `smoke.c`, then run `determinism CORE OUTPUT_DIRECTORY [DISC]`.
Omit `DISC` for its original fixture. CI and release builds run this probe in
addition to the ABI smoke test. Supply your own retail disc to verify its movies
and gameplay at the probe's checkpoints.

`tests/libretro/features.c` exercises extended disk controls, all three core option
interfaces, SRAM import/export, memory descriptors and cheat enable/disable/reset.
Its fake VFS exposes paths that OS I/O cannot open, limits reads and writes to
short chunks, and checks handle cleanup and save-file replacement. It also checks
structured logging and fallback to older frontends. Compile and run it with the
same flags and arguments as the ABI smoke test. CI runs it on Windows and Linux.

`tests/libretro/netplay.py` launches two private RetroArch processes through a
delayed loopback TCP proxy, sends changing network-gamepad input, and records the
SRAM/state handshake, input frames, CRC packets and state requests in `wire.json`.
It permits the initial join-state request and rejects later CRC resync requests.
RetroArch's SDL2 audio and network-gamepad support are required; the probe uses
dummy audio and null video/input drivers, without a public lobby. For example:

```sh
python tests/libretro/netplay.py --retroarch PATH/retroarch.exe --core artifacts/libretro/win-x64/gamewave_libretro.dll --content artifacts/libretro/determinism-data/disc/gamewave.diz --output artifacts/libretro/netplay-data --seconds 20 --delay-ms 80
```

Run the native determinism probe first to create that fixture, or supply your own
disc as `--content`. Tests with 80 and 160 ms of delay in each direction validate
SRAM synchronization and changing multiplayer input with frame CRC checks. A
160-second Zap 21 session reaches the menu with 80 CRC packets and no gameplay
resync requests. Other games and mixed-platform netplay require their own checks.
