"""text-replace 插件单元测试：真实启动插件进程，走完整 JSON Lines 协议。"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

PLUGIN = Path(__file__).with_name("main.py")


def invoke(invocation):
    env = dict(os.environ)
    env["PYTHONUTF8"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    completed = subprocess.run(
        [sys.executable, "-u", str(PLUGIN)],
        input=(json.dumps(invocation, ensure_ascii=False) + "\n").encode("utf-8"),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
        timeout=90,
    )
    events = []
    for line in completed.stdout.decode("utf-8", "replace").splitlines():
        if line.strip():
            events.append(json.loads(line))
    return completed, events


class TextReplaceTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="rt-textrep-")
        self.text_file = os.path.join(self.root, "notes.txt")
        Path(self.text_file).write_text("hello world\nsecond world\n", encoding="utf-8")
        self.binary_file = os.path.join(self.root, "blob.txt")
        Path(self.binary_file).write_bytes(b"world\x00binary")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def request(self, phase, plan=None, **overrides):
        parameters = {
            "pattern": "world",
            "replacement": "RightToolbox",
            "useRegex": "false",
            "ignoreCase": "false",
            "encoding": "自动检测",
            "includeSubfolders": "true",
            "backup": "true",
        }
        parameters.update(overrides)
        invocation = {
            "protocolVersion": 1,
            "requestId": "test-" + phase,
            "commandId": "community.text-replace.apply",
            "phase": phase,
            "paths": [self.root],
            "parameters": parameters,
        }
        if plan is not None:
            invocation["plan"] = plan
        return invocation

    def test_preview_reports_hits_and_skips_binary(self):
        completed, events = invoke(self.request("preview"))
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        preview = events[-1]
        self.assertEqual(preview["type"], "preview")
        paths = [item["path"] for item in preview["plan"]["items"]]
        self.assertEqual(len(paths), 1)
        self.assertEqual(os.path.normcase(paths[0]), os.path.normcase(self.text_file))
        self.assertEqual(preview["plan"]["items"][0]["hits"], 2)
        self.assertIn("2", preview["message"])

    def test_execute_writes_backup_and_content(self):
        completed, events = invoke(self.request("preview"))
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        plan = events[-1]["plan"]

        completed, events = invoke(self.request("execute", plan))
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        result = events[-1]
        self.assertEqual(result["type"], "result")

        updated = Path(self.text_file).read_text(encoding="utf-8")
        self.assertEqual(updated, "hello RightToolbox\nsecond RightToolbox\n")
        backup = Path(self.text_file + ".bak")
        self.assertTrue(backup.exists())
        self.assertEqual(backup.read_text(encoding="utf-8"), "hello world\nsecond world\n")

    def test_regex_replacement(self):
        completed, events = invoke(self.request("preview", pattern=r"(hello|second)\s+world", replacement="W", useRegex="true"))
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        plan = events[-1]["plan"]
        self.assertEqual(plan["items"][0]["hits"], 2)

        completed, events = invoke(self.request("execute", plan, pattern=r"(hello|second)\s+world", replacement="W", useRegex="true"))
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        self.assertEqual(Path(self.text_file).read_text(encoding="utf-8"), "W\nW\n")

    def test_no_backup_when_disabled(self):
        completed, events = invoke(self.request("preview", backup="false"))
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        plan = events[-1]["plan"]
        completed, events = invoke(self.request("execute", plan, backup="false"))
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        self.assertFalse(Path(self.text_file + ".bak").exists())

    def test_empty_pattern_is_rejected(self):
        completed, events = invoke(self.request("preview", pattern=""))
        self.assertEqual(completed.returncode, 1)
        self.assertEqual(events[-1]["type"], "error")

    def test_execute_without_plan_fails(self):
        completed, events = invoke(self.request("execute"))
        self.assertEqual(completed.returncode, 1)
        self.assertEqual(events[-1]["type"], "error")
        self.assertIn("预览", events[-1]["message"])


if __name__ == "__main__":
    unittest.main()
