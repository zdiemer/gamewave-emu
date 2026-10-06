"""Two RetroArch processes, delayed loopback netplay and wire-level CRC checks.

Requires RetroArch built with SDL2 audio and network gamepad support. Uses dummy
audio and null video/input drivers; does not publish a lobby or contact peers.
"""
import argparse
import asyncio
from collections import Counter
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import time


def free_port(kind=socket.SOCK_STREAM):
    with socket.socket(socket.AF_INET, kind) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


async def probe(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    host_port, proxy_port = free_port(), free_port()
    while proxy_port == host_port:
        proxy_port = free_port()
    remote_ports = [free_port(socket.SOCK_DGRAM) for _ in range(2)]
    stats = {"host": Counter(), "client": Counter()}
    nonzero_inputs = Counter()
    frames = {"host": [], "client": []}
    connected = asyncio.Event()
    state_requests = []
    proxy_errors = []
    proxy_tasks = set()

    async def relay(reader, writer, direction):
        # The protocol starts with six network-endian words, then framed commands.
        header = await reader.readexactly(24)
        assert header[:4] == b"RANP", header.hex()
        writer.write(header)
        await writer.drain()
        queue = asyncio.Queue(maxsize=64)

        async def send():
            while True:
                when, message = await queue.get()
                if message is None:
                    break
                await asyncio.sleep(max(0, when - time.monotonic()))
                writer.write(message)
                await writer.drain()

        sender = asyncio.create_task(send())
        try:
            while True:
                header = await reader.readexactly(8)
                command, size = struct.unpack("!II", header)
                assert size <= 64 * 1024 * 1024, (command, size)
                payload = await reader.readexactly(size)
                stats[direction][f"0x{command:04x}"] += 1
                if command == 0x41:
                    state_requests.append({"direction": direction, "inputs_sent": stats[direction]["0x0003"],
                                           "crc_checks_sent": stats["host"]["0x0040"]})
                if command == 3 and size >= 12:
                    frame = struct.unpack_from("!I", payload)[0]
                    frames[direction].append(frame)
                    if any(payload[8:]):
                        nonzero_inputs[direction] += 1
                await queue.put((time.monotonic() + args.delay_ms / 1000, header + payload))
        except asyncio.IncompleteReadError:
            pass
        finally:
            # A disconnected peer can leave a full queue after its writer fails.
            # Cancel the sender so teardown cannot wait forever on that queue.
            sender.cancel()
            await asyncio.gather(sender, return_exceptions=True)

    async def connection(client_reader, client_writer):
        task = asyncio.current_task()
        proxy_tasks.add(task)
        host_writer = None
        try:
            host_reader, host_writer = await asyncio.open_connection("127.0.0.1", host_port)
            connected.set()
            await asyncio.gather(relay(client_reader, host_writer, "client"), relay(host_reader, client_writer, "host"))
        except (ConnectionError, OSError, asyncio.IncompleteReadError):
            pass
        except Exception as error:
            proxy_errors.append(repr(error))
        finally:
            client_writer.close()
            if host_writer:
                host_writer.close()
            proxy_tasks.discard(task)

    server = await asyncio.start_server(connection, "127.0.0.1", proxy_port)
    env = os.environ.copy()
    env["SDL_AUDIODRIVER"] = "dummy"
    processes = []
    log_files = []
    def launch(index, role):
        directory = output / role
        directory.mkdir(exist_ok=True)
        for name in ("saves", "states"):
            (directory / name).mkdir(exist_ok=True)
        options = {
            "video_driver": "null", "audio_driver": "sdl2", "input_driver": "null", "input_joypad_driver": "null",
            "video_vsync": "false", "audio_sync": "true", "config_save_on_exit": "false",
            "core_info_cache_enable": "false", "netplay_public_announce": "false", "netplay_use_mitm_server": "false",
            "netplay_nat_traversal": "false", "netplay_check_frames": "30", "netplay_start_as_spectator": "false",
            "netplay_input_latency_frames_min": "0", "netplay_input_latency_frames_range": "0",
            "run_ahead_enabled": "false", "rewind_enable": "false", "input_max_users": "6",
            "network_remote_enable": "true", "network_remote_base_port": str(remote_ports[index]),
            "network_remote_enable_user_p1": "true", "input_remote_enable": "true",
            "input_remote_base_port": str(remote_ports[index]), "input_remote_enable_user_p1": "true",
            "savefile_directory": (directory / "saves").as_posix(), "savestate_directory": (directory / "states").as_posix(),
            "libretro_info_path": args.core.resolve().parent.as_posix(), "core_options_path": (directory / "options.cfg").as_posix(),
            "content_history_path": (directory / "history.lpl").as_posix(),
            "content_favorites_path": (directory / "favorites.lpl").as_posix(),
        }
        config = directory / "retroarch.cfg"
        config.write_text("\n".join(f'{key} = "{value}"' for key, value in options.items()) + "\n", encoding="utf-8")
        logfile = directory / "retroarch.log"
        log_files.append(logfile)
        command = [str(args.retroarch.resolve()), "--verbose", "--config", str(config), "--log-file", str(logfile),
                   "--nick", "GamewaveTest" + role, "--check-frames", "30", "-L", str(args.core.resolve()),
                   "--port", str(host_port if role == "host" else proxy_port)]
        command += ["--host"] if role == "host" else ["--connect", "127.0.0.1"]
        command += [str(args.content.resolve())]
        process = subprocess.Popen(command, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                   creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        processes.append(process)
    try:
        for index, role in enumerate(("host", "client")):
            launch(index, role)
            if role == "host":
                await asyncio.sleep(1.5)
        await asyncio.wait_for(connected.wait(), timeout=15)
        # UDP network gamepad input travels through RetroArch's local input poll,
        # so both peers see the same synchronized input after the delayed TCP hop.
        start = time.monotonic()
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as pad:
            while time.monotonic() - start < args.seconds:
                assert all(process.poll() is None for process in processes), "RetroArch exited early"
                state = int((time.monotonic() - start) % 1.2 < .6)
                packet = struct.pack("<iiiihxx", 0, 1, 0, 8, state)  # port, JOYPAD, index, A, state
                for port in remote_ports:
                    pad.sendto(packet, ("127.0.0.1", port))
                await asyncio.sleep(.1)
        results = {"delay_ms_each_direction": args.delay_ms, "seconds": args.seconds,
                   "commands": {role: dict(value) for role, value in stats.items()}, "nonzero_inputs": dict(nonzero_inputs),
                   "state_requests": state_requests,
                   "frame_ranges": {role: [min(value), max(value)] if value else [] for role, value in frames.items()}}
        (output / "wire.json").write_text(json.dumps(results, indent=2) + "\n")
        print(json.dumps(results, indent=2), flush=True)
        assert not proxy_errors, proxy_errors
        assert stats["host"]["0x0023"] >= 1, "No SRAM handshake"
        assert stats["host"]["0x0042"] >= 1, "No initial state synchronization"
        assert stats["host"]["0x0040"] >= 10, "Too few frame CRC checks"
        # RetroArch's achievement-enabled builds request a state when joining,
        # even with achievements disabled. Later requests indicate CRC failure.
        assert len(state_requests) <= 1 and all(request["direction"] == "client" and request["crc_checks_sent"] == 0
                                              for request in state_requests), "Client requested state resynchronization during play"
        assert stats["host"]["0x0001"] == stats["client"]["0x0001"] == 0, "Protocol rejection"
        assert nonzero_inputs["host"] > 0 and nonzero_inputs["client"] > 0, "Network gamepad input did not reach both peers"
        for logfile in log_files:
            log = logfile.read_text(encoding="utf-8", errors="replace")
            assert "[ERROR]" not in log and "crcs mismatch" not in log.lower(), logfile
        print("PASS: two RetroArch peers, changing input, delayed traffic, SRAM/state sync and frame CRCs", flush=True)
    finally:
        for process in processes:
            if process.poll() is None:
                process.terminate()
        for process in processes:
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        server.close()
        await server.wait_closed()
        for task in list(proxy_tasks):
            task.cancel()
        if proxy_tasks:
            await asyncio.gather(*proxy_tasks, return_exceptions=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--retroarch", type=Path, required=True)
    parser.add_argument("--core", type=Path, required=True)
    parser.add_argument("--content", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seconds", type=float, default=20)
    parser.add_argument("--delay-ms", type=float, default=80)
    args = parser.parse_args()
    if args.seconds < 10 or args.delay_ms < 0:
        parser.error("--seconds must be at least 10; --delay-ms cannot be negative")
    asyncio.run(probe(args))
