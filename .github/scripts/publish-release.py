"""Verify release archives, upload them to a draft, then publish the release."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tarfile
import time
import zipfile


API_VERSION = "X-GitHub-Api-Version: 2026-03-10"
PLATFORMS = ("linux-x86_64", "linux-arm64", "macos-arm64", "macos-x86_64")


def verify_archive(path):
    if path.suffix == ".zip":
        required = {"LICENSE", "libretro.md", "gamewave_libretro.dll", "gamewave_libretro.info"} if "libretro" in path.name else {"LICENSE", "README.md", "gamewave.exe", "SDL2.dll"}
        with zipfile.ZipFile(path) as archive:
            if archive.testzip() is not None or set(archive.namelist()) != required:
                raise RuntimeError("Invalid Windows archive contents: " + path.name)
    else:
        root = path.name.removesuffix(".tar.gz")
        core = "libretro" in root
        mac = "macos" in root
        required = {"LICENSE", "EXPERIMENTAL.txt"}
        required |= {"libretro.md", "gamewave_libretro.info", "gamewave_libretro." + ("dylib" if mac else "so")} if core else {"README.md", "gamewave", "libSDL2-2.0." + ("dylib" if mac else "so")}
        with tarfile.open(path, "r:gz") as archive:
            members = archive.getmembers()
            if any(not (member.isfile() or member.isdir()) for member in members):
                raise RuntimeError("Unexpected archive member type: " + path.name)
            if {member.name for member in members if member.isfile()} != {root + "/" + name for name in required}:
                raise RuntimeError("Invalid experimental archive contents: " + path.name)
            warning = archive.extractfile(root + "/EXPERIMENTAL.txt").read().decode()
            if "EXPERIMENTAL PLATFORM BUILD" not in warning or "untested" not in warning:
                raise RuntimeError("Missing experimental warning: " + path.name)
            if not core and archive.getmember(root + "/gamewave").mode & 0o111:
                raise RuntimeError("Standalone executable permissions missing: " + path.name)


def api(endpoint, method="GET", data=None):
    command = ["gh", "api", endpoint, "--method", method, "-H", API_VERSION]
    if data is not None:
        command += ["--input", "-"]
    for attempt in range(3):
        result = subprocess.run(command, input=json.dumps(data) if data is not None else None,
                                text=True, capture_output=True)
        if result.returncode == 0:
            return json.loads(result.stdout)
        if attempt == 2 or not any(f"HTTP {code}" in result.stderr for code in (500, 502, 503, 504)):
            raise RuntimeError(result.stderr.strip())
        print(f"GitHub returned a server error; retrying {method} {endpoint}", flush=True)
        time.sleep(2 ** (attempt + 1))


def prepare(directory, tag):
    expected = {"gamewave-windows-x86_64.zip", "gamewave-libretro-windows-x86_64.zip"}
    for platform in PLATFORMS:
        expected.update(f"{prefix}-{platform}-experimental.tar.gz" for prefix in ("gamewave", "gamewave-libretro"))
    archives = sorted(path for path in directory.iterdir() if path.name.endswith((".zip", ".tar.gz")))
    if {path.name for path in archives} != expected:
        raise RuntimeError("Missing or unexpected release archives: " + str(expected ^ {path.name for path in archives}))
    digests = {}
    for path in archives:
        verify_archive(path)
        with path.open("rb") as stream:
            digests[path.name] = hashlib.file_digest(stream, "sha256").hexdigest()
    checksums = directory / "SHA256SUMS"
    checksums.write_bytes("".join(f"{digest}  {name}\n" for name, digest in digests.items()).encode())
    body = """This release adds experimental Linux and macOS downloads for both the standalone emulator and the libretro core. Windows remains the validated release platform.

**Experimental / untested:** macOS Intel, macOS Apple silicon, and Linux ARM64 have automated build and smoke checks only. They have not been manually validated with games, desktop GUI/audio/controllers, or RetroArch.

**Linux x64:** headless tests were run in Ubuntu 24.04 under WSL2, including the managed suite, standalone retail playback, and native libretro ABI, frontend-interface, and deterministic replay probes. Native desktop GUI/audio/controllers and real RetroArch/netplay remain untested. WSL results do not establish compatibility with every Linux distribution.

Every experimental archive is named `*-experimental.tar.gz` and includes `EXPERIMENTAL.txt`. macOS binaries are signed ad hoc, not notarized. Both packages run without an installed .NET runtime; supply your own disc images.

## Downloads

"""
    base = f"https://github.com/{os.environ['GH_REPO']}/releases/download/{tag}/"
    for path in archives:
        status = "Experimental / untested desktop and frontend" if "experimental" in path.name else "Windows x64"
        body += f"- [{path.name}]({base}{path.name}) — {status}\n"
    body += f"\n[SHA256SUMS]({base}SHA256SUMS) covers all ten archives.\n"
    digests[checksums.name] = hashlib.sha256(checksums.read_bytes()).hexdigest()
    return archives + [checksums], digests, body


def publish(directory, tag):
    files, digests, body = prepare(directory, tag)
    repo = os.environ["GH_REPO"]
    releases = api(f"repos/{repo}/releases?per_page=100")
    release = next((item for item in releases if item["tag_name"] == tag), None)
    if release is None:
        release = api(f"repos/{repo}/releases", "POST", {
            "tag_name": tag, "name": f"gamewave {tag.removeprefix('v')} (experimental Linux/macOS builds)",
            "body": body, "draft": True, "prerelease": False,
        })
    subprocess.run(["gh", "release", "upload", tag, "--repo", repo, "--clobber", *map(str, files)], check=True)
    uploaded = api(f"repos/{repo}/releases/{release['id']}")
    if {asset["name"] for asset in uploaded["assets"]} != set(digests):
        raise RuntimeError("Published assets do not match the expected package set")
    for asset in uploaded["assets"]:
        if asset.get("digest") != "sha256:" + digests[asset["name"]]:
            raise RuntimeError("Upload checksum mismatch: " + asset["name"])
    if uploaded["draft"]:
        uploaded = api(f"repos/{repo}/releases/{release['id']}", "PATCH", {"draft": False, "make_latest": "true"})
    print(uploaded["html_url"], flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, default=Path("dist"))
    parser.add_argument("--prepare-only", action="store_true")
    args = parser.parse_args()
    tag = os.environ["TAG"]
    if args.prepare_only:
        files, _, body = prepare(args.directory, tag)
        print(body)
        print(f"Verified package set: {len(files) - 1} archives plus SHA256SUMS")
    else:
        publish(args.directory, tag)
