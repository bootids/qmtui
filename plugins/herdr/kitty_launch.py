#!/usr/bin/env python3
"""Attach a Kitty viewer to qmtui on the local default Herdr server."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


def local_environment(environ):
    # A remote/named-session environment must never retarget these host shortcuts.
    env = {key: value for key, value in environ.items() if not key.startswith("HERDR_")}
    config = Path(env.get("XDG_CONFIG_HOME", str(Path.home() / ".config"))) / "herdr"
    state = Path(env.get("XDG_STATE_HOME", str(Path.home() / ".local/state"))) / "herdr"
    env.update({
        "HERDR_SOCKET_PATH": str(config / "herdr.sock"),
        "HERDR_CONFIG_PATH": str(config / "config.toml"),
        "HERDR_PLUGIN_ID": "qmtui",
        "HERDR_PLUGIN_CONFIG_DIR": str(config / "plugins/config/qmtui"),
        "HERDR_PLUGIN_STATE_DIR": str(state / "plugins/qmtui"),
    })
    return env


def main():
    env = local_environment(os.environ)
    binary = shutil.which("herdr", path=env.get("PATH"))
    if not binary:
        raise FileNotFoundError("找不到本机 herdr，请检查 Kitty 启动环境的 PATH。")
    env["HERDR_BIN_PATH"] = binary
    result = json.loads(subprocess.check_output(
        [binary, "plugin", "list", "--plugin", "qmtui", "--json"], env=env, text=True
    ))["result"]
    if not result["plugins"]:
        raise RuntimeError("请先在 Local 安装 qmtui Herdr 插件。")
    plugin = result["plugins"][0]
    if not plugin["enabled"]:
        raise RuntimeError("本机 qmtui 插件已禁用，请先启用。")
    env["HERDR_PLUGIN_ROOT"] = plugin["plugin_root"]
    script = Path(plugin["plugin_root"]) / "qmtui.py"
    os.execve(sys.executable, [sys.executable, str(script), "attach"], env)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.CalledProcessError) as error:
        sys.exit(f"qmtui-local: {error}")
