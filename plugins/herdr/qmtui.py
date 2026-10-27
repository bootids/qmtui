#!/usr/bin/env python3
"""Keep one player per Herdr session and toggle back to the calling tab."""

import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


def herdr(*args):
    output = subprocess.check_output(
        [os.environ["HERDR_BIN_PATH"], *args], text=True
    )
    return json.loads(output)["result"]


def player_binary():
    config = Path(os.environ["HERDR_PLUGIN_CONFIG_DIR"]) / "qmtui-path"
    command = config.read_text().strip() if config.exists() else "qmtui"
    binary = shutil.which(command)
    if not binary:
        raise FileNotFoundError(
            f"找不到 qmtui：{command}。请安装 qmtui，或将可执行文件路径写入 {config}。"
        )
    return binary


def toggle():
    session = hashlib.sha256(os.environ["HERDR_SOCKET_PATH"].encode()).hexdigest()
    state_dir = Path(os.environ["HERDR_PLUGIN_STATE_DIR"])
    state_dir.mkdir(parents=True, exist_ok=True)
    state_path = state_dir / f"{session}.json"

    # Serialize repeated shortcuts so they cannot start duplicate players.
    with (state_dir / f"{session}.lock").open("w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        state = json.loads(state_path.read_text()) if state_path.exists() else {}
        panes = herdr("pane", "list")["panes"]
        by_terminal = {pane["terminal_id"]: pane for pane in panes}
        context = json.loads(os.environ["HERDR_PLUGIN_CONTEXT_JSON"])
        current = next(
            (pane for pane in panes if pane["pane_id"] == context["focused_pane_id"]),
            None,
        )
        if current is None:
            raise RuntimeError("请从 Herdr 工作界面调用 qmtui 插件。")

        player = by_terminal.get(state.get("player_terminal_id"))
        if player and player["pane_id"] == current["pane_id"]:
            previous = by_terminal.get(state.get("return_terminal_id"))
            if previous is None:
                raise RuntimeError("原工作界面已关闭，请通过 Herdr 标签栏切换。")
            herdr("tab", "focus", previous["tab_id"])
            return

        if player:
            herdr("plugin", "pane", "focus", player["pane_id"])
        else:
            player_binary()
            result = herdr(
                "plugin", "pane", "open",
                "--plugin", os.environ["HERDR_PLUGIN_ID"],
                "--entrypoint", "player",
                "--workspace", current["workspace_id"],
                "--no-focus",
            )
            player = result["plugin_pane"]["pane"]

        # Herdr 0.9.5 projects tab.focus to attached clients; plugin.pane.focus
        # only changes the server's default focus and can leave the UI unchanged.
        herdr("tab", "focus", player["tab_id"])
        state["player_terminal_id"] = player["terminal_id"]
        state["return_terminal_id"] = current["terminal_id"]
        state_path.write_text(json.dumps(state) + "\n")


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["toggle"]:
            toggle()
        elif sys.argv[1:] == ["run"]:
            binary = player_binary()
            os.execv(binary, [binary])
        else:
            sys.exit("Usage: qmtui.py {toggle|run}")
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.CalledProcessError) as error:
        sys.exit(f"qmtui: {error}")
