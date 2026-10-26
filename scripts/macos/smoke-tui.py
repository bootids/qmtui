#!/usr/bin/env python3
"""Exercise the published TUI in a real PTY on a fresh CI runner.

The test uses the runner's own home directory, refuses existing application
profiles, and never supplies account credentials. Run it before tests that
write qmtui user data. It opens the normal login screen, waits for background
startup work, then exits with Escape, Q, and Enter.
"""

import errno
import fcntl
import os
from pathlib import Path
import pty
import re
import select
import signal
import struct
import sys
import termios
import time


ANSI = re.compile(
    rb"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|"
    rb"\x1bP[^\x1b]*(?:\x1b\\)|"
    rb"\x1b\[[0-?]*[ -/]*[@-~]|\x1b[()][0-2A-Z]|\x1b[=>78]"
)
FAILURE = re.compile(
    r"unhandled exception|unhandled AppDomain|unobserved task exception|"
    r"segmentation fault|abort trap|fatal error|\[FATAL\]|GStreamer-CRITICAL",
    re.IGNORECASE,
)
LOG_PATH = Path("/tmp/qqmusic_debug.log")


def plain(data):
    return ANSI.sub(b"", data).decode("utf-8", errors="replace")


def read_new_log(offset, inode):
    try:
        stat = LOG_PATH.stat()
        with LOG_PATH.open("rb") as stream:
            if stat.st_ino == inode and stat.st_size >= offset:
                stream.seek(offset)
            return stream.read().decode("utf-8", errors="replace")
    except FileNotFoundError:
        return ""


def main():
    if len(sys.argv) != 2:
        raise RuntimeError("Usage: smoke-tui.py /absolute/path/to/qmtui")
    binary = Path(sys.argv[1]).resolve(strict=True)
    if not os.access(binary, os.X_OK):
        raise RuntimeError(f"Binary is not executable: {binary}")

    # A real home directory is needed by .NET. Refuse existing profiles instead
    # of overriding HOME or exposing a developer's saved login to the smoke test.
    profile_root = Path.home()
    for relative in (".config/qmtui", ".local/share/qmtui"):
        directory = profile_root / relative
        if directory.exists() and any(directory.iterdir()):
            raise RuntimeError(f"Use a fresh CI runner; existing profile: {directory}")

    try:
        prior_log = LOG_PATH.stat()
        log_offset, log_inode = prior_log.st_size, prior_log.st_ino
    except FileNotFoundError:
        log_offset, log_inode = 0, None

    task_env = os.environ.copy()
    task_env.update({
        "TERM": "xterm-256color",
        "COLORTERM": "truecolor",
        "GST_AUDIO_SINK": "fakesink sync=true",
        "QQMUSIC_DISABLE_IMAGE": "1",
        "QQMUSIC_DEBUG": "1",
    })
    task_env.pop("QQMUSIC_WEB", None)
    task_env.pop("QQMUSIC_NO_AUDIO", None)
    size = struct.pack("HHHH", 40, 120, 800, 1200)
    pid, master = pty.fork()
    if pid == 0:
        try:
            fcntl.ioctl(0, termios.TIOCSWINSZ, size)
            os.execve(str(binary), [str(binary), "--debug", "--no-notify"], task_env)
        except BaseException as exc:
            print(f"Cannot start qmtui: {exc}", file=sys.stderr, flush=True)
            os._exit(127)

    fcntl.ioctl(master, termios.TIOCSWINSZ, size)
    transcript = bytearray()
    query_buffer = b""
    started = time.monotonic()
    phase = "starting"
    phase_time = started
    status = None
    saw_login = False
    saw_confirmation = False
    error = None
    terminal_replies = {
        b"\x1b[6n": b"\x1b[1;1R",
        b"\x1b[c": b"\x1b[?1;2c",
        b"\x1b[0c": b"\x1b[?1;2c",
        b"\x1b[>c": b"\x1b[>0;0;0c",
        b"\x1b[>0c": b"\x1b[>0;0;0c",
        b"\x1b[18t": b"\x1b[8;40;120t",
        b"\x1b[14t": b"\x1b[4;800;1200t",
    }

    try:
        while time.monotonic() - started < 30:
            ready, _, _ = select.select([master], [], [], 0.1)
            if ready:
                try:
                    chunk = os.read(master, 65536)
                except OSError as exc:
                    if exc.errno != errno.EIO:
                        raise
                    chunk = b""
                if chunk:
                    transcript.extend(chunk)
                    del transcript[:-1_048_576]
                    query_buffer += chunk
                    for query, reply in terminal_replies.items():
                        if query in query_buffer:
                            os.write(master, reply)
                            query_buffer = query_buffer.replace(query, b"")
                    query_buffer = query_buffer[-64:]
                    screen = "".join(plain(transcript).split())
                    saw_login |= "账号扫码登录" in screen or "网页登录" in screen
                    saw_confirmation |= "退出确认" in screen or "确定要退出播放器吗" in screen

            exited, child_status = os.waitpid(pid, os.WNOHANG)
            if exited:
                status = child_status
                if phase != "confirmed":
                    code = os.waitstatus_to_exitcode(status)
                    raise RuntimeError(f"qmtui exited early with status {code} (phase={phase})")
                break

            now = time.monotonic()
            if phase == "starting" and now - started >= 5 and saw_login:
                os.write(master, b"\x1b")
                phase, phase_time = "login_closed", now
            elif phase == "login_closed" and now - phase_time >= 0.75:
                os.write(master, b"q")
                phase, phase_time = "quit_requested", now
            elif phase == "quit_requested" and saw_confirmation:
                os.write(master, b"\r")
                phase, phase_time = "confirmed", now

        if status is None:
            raise RuntimeError(f"TUI timed out after 30 seconds (phase={phase})")
        exit_code = os.waitstatus_to_exitcode(status)
        if exit_code != 0:
            raise RuntimeError(f"TUI exited with status {exit_code}")
        new_log = read_new_log(log_offset, log_inode)
        if FAILURE.search(plain(transcript) + "\n" + new_log):
            raise RuntimeError("TUI emitted a fatal or unhandled native/managed error")
        if not saw_login or not saw_confirmation:
            raise RuntimeError("Login UI and interactive exit confirmation were not both rendered")
        if "GStreamer playbin pipeline created" not in new_log:
            raise RuntimeError("Native GStreamer playback was not initialized")
        if "Cocoa accessory application initialized on the native main thread." not in new_log:
            raise RuntimeError("The production TUI did not initialize Cocoa on the native main thread")
        if "macOS Now Playing media session started" not in new_log:
            raise RuntimeError("The production TUI did not start its macOS media session")
        if not re.search(r"Cocoa main loop serviced [1-9][0-9]* active TUI ticks\.", new_log):
            raise RuntimeError("The native event pump did not run during the active TUI session")
        if re.search(r"\[ERROR\] \[(?:MacMainLoop|MacMediaSession|MediaSession)\]", new_log):
            raise RuntimeError("The production TUI logged a native media-session error")
        if "qmtui Session ended." not in new_log:
            raise RuntimeError("The application did not log a clean shutdown")
        print("TUI smoke passed: native audio and macOS media session initialized, login rendered, alive >=5s, Q/Enter exit 0.")
    except BaseException as exc:
        error = exc
    finally:
        if status is None:
            try:
                os.killpg(pid, signal.SIGTERM)
                grace = time.monotonic() + 1
                while time.monotonic() < grace:
                    exited, status_value = os.waitpid(pid, os.WNOHANG)
                    if exited:
                        status = status_value
                        break
                    time.sleep(0.05)
                if status is None:
                    os.killpg(pid, signal.SIGKILL)
                    os.waitpid(pid, 0)
            except ProcessLookupError:
                pass
        os.close(master)

    if error is not None:
        print(f"TUI smoke failed: {error}", file=sys.stderr)
        print("--- Recent terminal output ---", file=sys.stderr)
        print(plain(transcript)[-6000:], file=sys.stderr)
        print("--- New application log ---", file=sys.stderr)
        print(read_new_log(log_offset, log_inode)[-6000:], file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        print(f"TUI smoke failed: {exc}", file=sys.stderr)
        sys.exit(1)
