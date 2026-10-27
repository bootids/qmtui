"""Behavior checks for background playback and session-local player reuse."""

import copy
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import qmtui


class ToggleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.panes = [self.pane("work", "terminal-work", "tab-work")]
        self.opens = 0
        self.focused = "work"
        self.addCleanup(patch.stopall)
        patch.dict(os.environ, {
            "HERDR_SOCKET_PATH": "/session-one/herdr.sock",
            "HERDR_PLUGIN_STATE_DIR": self.temp.name,
            "HERDR_PLUGIN_ID": "qmtui",
        }).start()
        self.api = patch.object(qmtui, "herdr", side_effect=self.herdr).start()
        patch.object(qmtui, "player_binary", return_value="/bin/qmtui").start()

    @staticmethod
    def pane(pane_id, terminal_id, tab_id):
        return {
            "pane_id": pane_id, "terminal_id": terminal_id,
            "tab_id": tab_id, "workspace_id": "workspace",
        }

    def invoke(self):
        os.environ["HERDR_PLUGIN_CONTEXT_JSON"] = json.dumps({
            "focused_pane_id": self.focused,
        })
        qmtui.toggle()

    def herdr(self, *args):
        if args == ("pane", "list"):
            return {"panes": copy.deepcopy(self.panes)}
        if args[:3] == ("plugin", "pane", "open"):
            self.opens += 1
            player = self.pane(f"music-{self.opens}", f"terminal-music-{self.opens}", f"tab-music-{self.opens}")
            self.panes.append(player)
            # Opening a plugin pane does not change an attached client's view.
            return {"plugin_pane": {"pane": player}}
        if args[:3] == ("plugin", "pane", "focus"):
            # Herdr 0.9.5 changes server focus here, not the client's visible tab.
            return {}
        if args[:2] == ("tab", "focus"):
            self.focused = next(p["pane_id"] for p in self.panes if p["tab_id"] == args[2])
            return {}
        self.fail(f"Unexpected Herdr command: {args}")

    def test_show_hide_show_keeps_one_player(self):
        self.invoke()
        self.assertEqual(self.focused, "music-1")
        self.invoke()
        self.assertEqual(self.focused, "work")
        self.invoke()
        self.assertEqual(self.focused, "music-1")
        self.assertEqual(self.opens, 1)
        self.assertFalse(any("close" in call.args for call in self.api.call_args_list))

    def test_moved_player_is_found_by_terminal_identity(self):
        self.invoke()
        self.panes[1]["pane_id"] = "moved-music"
        self.focused = "work"
        self.invoke()
        self.assertEqual(self.focused, "moved-music")
        self.assertEqual(self.opens, 1)

    def test_closed_player_does_not_focus_a_reused_pane_id(self):
        self.invoke()
        self.panes[1]["terminal_id"] = "unrelated-terminal"
        self.focused = "work"
        self.invoke()
        self.assertEqual(self.focused, "music-2")
        self.assertEqual(self.opens, 2)

    def test_closed_return_pane_reports_error_without_stopping_music(self):
        self.invoke()
        self.panes.pop(0)
        with self.assertRaisesRegex(RuntimeError, "原工作界面已关闭"):
            self.invoke()
        self.assertEqual(self.focused, "music-1")
        self.assertEqual(self.opens, 1)

    def test_server_error_does_not_start_another_player_or_overwrite_state(self):
        self.invoke()
        state = next(Path(self.temp.name).glob("*.json"))
        saved = state.read_text()
        self.api.side_effect = OSError("server disconnected")
        with self.assertRaisesRegex(OSError, "server disconnected"):
            self.invoke()
        self.assertEqual(self.opens, 1)
        self.assertEqual(state.read_text(), saved)

    def test_sessions_keep_separate_player_state(self):
        self.invoke()
        self.focused = "work"
        os.environ["HERDR_SOCKET_PATH"] = "/session-two/herdr.sock"
        self.invoke()
        self.assertEqual(self.opens, 2)
        self.assertEqual(len(list(Path(self.temp.name).glob("*.json"))), 2)


if __name__ == "__main__":
    unittest.main()
