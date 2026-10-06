#!/usr/bin/env bash
set -euo pipefail
rid="${1:?Supply the native runtime identifier}"
case "$rid" in
  linux-x64) platform=linux-x86_64; sdl=libSDL2-2.0.so; core=gamewave_libretro.so ;;
  linux-arm64) platform=linux-arm64; sdl=libSDL2-2.0.so; core=gamewave_libretro.so ;;
  osx-x64) platform=macos-x86_64; sdl=libSDL2-2.0.dylib; core=gamewave_libretro.dylib ;;
  osx-arm64) platform=macos-arm64; sdl=libSDL2-2.0.dylib; core=gamewave_libretro.dylib ;;
  *) echo "Unsupported runtime: $rid" >&2; exit 1 ;;
esac

mkdir -p dist
core_out="artifacts/libretro/$rid"
dotnet publish src/GameWave.Libretro -c Release -r "$rid" -p:DebugType=none -o "$core_out"
test -f "$core_out/$core"
if [[ "$rid" == osx-* ]]; then
  codesign --force --sign - "$core_out/$core"
  codesign --verify --verbose "$core_out/$core"
fi

# These original fixtures exercise ABI and deterministic replay without a retail
# disc or a frontend. They do not establish desktop/RetroArch compatibility.
compile_flags=(-std=c11 -Wall -Wextra -Werror -O2)
if [[ "$rid" == linux-* ]]; then compile_flags+=(-ldl); fi
for probe in smoke determinism features; do
  cc "${compile_flags[@]}" "tests/libretro/$probe.c" -o "artifacts/libretro/$probe"
  "artifacts/libretro/$probe" "$PWD/$core_out/$core" "$PWD/artifacts/libretro/$probe-data"
done

core_name="gamewave-libretro-$platform-experimental"
mkdir -p "staging/$core_name"
cp "$core_out/$core" "$core_out/gamewave_libretro.info" docs/libretro.md LICENSE "staging/$core_name/"
cp docs/experimental-builds.txt "staging/$core_name/EXPERIMENTAL.txt"
tar -czf "dist/$core_name.tar.gz" -C staging "$core_name"

player_out="publish/$rid"
dotnet publish src/GameWave -c Release -r "$rid" --self-contained -p:PublishSingleFile=true -p:DebugType=none -o "$player_out"
test -f "$player_out/gamewave"
test -f "$player_out/$sdl"
chmod 755 "$player_out/gamewave"
if [[ "$rid" == osx-* ]]; then
  codesign --verify "$player_out/gamewave" 2>/dev/null || codesign --force --sign - "$player_out/gamewave"
  codesign --force --sign - "$player_out/$sdl"
  codesign --verify --verbose "$player_out/gamewave" "$player_out/$sdl"
fi
actual_version="$("$player_out/gamewave" version)"
release_tag="${TAG:?Supply the release tag}"
expected_version="gamewave ${release_tag#v}"
test "$actual_version" = "$expected_version"
echo "built $rid: $actual_version"

player_name="gamewave-$platform-experimental"
mkdir -p "staging/$player_name"
cp "$player_out/gamewave" "$player_out/$sdl" README.md LICENSE "staging/$player_name/"
cp docs/experimental-builds.txt "staging/$player_name/EXPERIMENTAL.txt"
chmod 755 "staging/$player_name/gamewave"
chmod 644 "staging/$player_name/$sdl" "staging/$player_name/README.md" "staging/$player_name/LICENSE" "staging/$player_name/EXPERIMENTAL.txt"
tar -czf "dist/$player_name.tar.gz" -C staging "$player_name"
