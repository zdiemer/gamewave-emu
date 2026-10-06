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
Windows x64 is tested locally; CI publishes and smoke-tests Windows x64 and Linux x64.
Other targets require validation on their respective systems.

Tagged releases package the Windows core and its info file as
`gamewave-libretro-windows-x86_64.zip`.

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
Rewind 2005 (USA) (Disc A).iso
Rewind 2005 (USA) (Disc B).iso
```

When a game asks for its other disc, use the frontend's **Disk Control** menu:
eject if needed, select the other disc index, and insert it. Inserting the same
disc boots it again. The core also supports adding and replacing disc entries
through that menu. Selecting the index after the last entry leaves the drive empty.

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

## Saves and limits

High scores and settings persist automatically in
`<frontend save directory>/gamewave/gamewave.saves`. This file stores every
game's named flash slots, including both discs of a set. It uses the standalone
emulator's save format. Back up this file to preserve saves. When the frontend
does not supply a save directory, the core uses the user's local application data
directory under `gamewave-libretro`.

Libretro save states, rewind, runahead, cheats and exposed RAM are unsupported.
The engine's standalone quicksave is an in-memory object snapshot and cannot be
written as a libretro state file. Game-script or loading errors appear as frontend
messages.

Native AOT [does not support unloading its runtime](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries).
The core retains the native module in memory until the frontend exits. Unloading
content or deinitializing stops the console and releases disc resources; loading
the core again in the same process is supported. Replacing the DLL after a rebuild
requires closing the frontend first.

## Verification

Windows x64 validation includes the native ABI smoke test and a 3600-frame run
of Zap 21 in RetroArch 1.22.2 with its SDL audio driver. The retail-content probe
also verifies video, audio and SEL input through the native library for one minute.

```sh
dotnet test -c Release
```

`tests/libretro/smoke.c` uses the vendored upstream libretro header and loads the
published native library through the OS loader. It creates original synthetic
disc content and checks all required exports, failed loads, frame/audio callbacks,
six remotes, number chords, keyboard input, reset, save-file creation, disc swapping,
and unload/reload. No retail disc is needed for this test.

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
seconds, and writes BMP screenshots every 600 frames. The default is 3600 frames
(one minute). Retail disc images remain outside the repository.
