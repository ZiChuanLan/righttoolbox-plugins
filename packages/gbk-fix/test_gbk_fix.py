"""gbk-fix 插件单元测试：真实启动插件进程，走完整 JSON Lines 协议。"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

PLUGIN = Path(__file__).with_name("main.py")
FORBIDDEN = set('/\\:*?"<>|')

CANDIDATES = ["中文文件名.txt", "测试文档.txt", "报告2026.txt", "说明文档.txt"]


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


def mojibake(name):
    """把中文名按 GBK 编码后再按 cp437 解码，模拟旧 ZIP 的乱码文件名。"""
    return name.encode("gbk").decode("cp437")


def pick_safe_mojibake():
    for candidate in CANDIDATES:
        try:
            broken = mojibake(candidate)
        except (UnicodeEncodeError, UnicodeDecodeError):
            continue
        if not any(char in FORBIDDEN for char in broken):
            return candidate, broken
    return None, None


class GbkFixTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="rt-gbkfix-")
        self.original, self.broken = pick_safe_mojibake()
        if self.broken is None:
            self.skipTest("当前平台无法构造安全的 cp437 乱码文件名")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def request(self, phase, plan=None):
        invocation = {
            "protocolVersion": 1,
            "requestId": "test-" + phase,
            "commandId": "community.gbk-fix.repair",
            "phase": phase,
            "paths": [self.root],
            "parameters": {
                "sourceEncoding": "自动检测",
                "targetEncoding": "自动（GBK 优先）",
                "recursive": "true",
                "includeFolders": "true",
            },
        }
        if plan is not None:
            invocation["plan"] = plan
        return invocation

    def test_preview_then_rename(self):
        broken_path = os.path.join(self.root, self.broken)
        Path(broken_path).write_text("hello", encoding="utf-8")

        completed, events = invoke(self.request("preview"))
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        self.assertEqual(len(events), 1)
        preview = events[0]
        self.assertEqual(preview["type"], "preview")
        items = preview["plan"]["items"]
        self.assertEqual(len(items), 1)
        self.assertEqual(os.path.basename(items[0]["target"]), self.original)
        self.assertEqual(len(preview["rows"]), 1)

        completed, events = invoke(self.request("execute", preview["plan"]))
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        result = events[-1]
        self.assertEqual(result["type"], "result")
        self.assertIn("1", result["message"])
        self.assertTrue(os.path.exists(os.path.join(self.root, self.original)))
        self.assertFalse(os.path.exists(broken_path))

    def test_conflict_is_skipped(self):
        broken_path = os.path.join(self.root, self.broken)
        Path(broken_path).write_text("old", encoding="utf-8")
        Path(os.path.join(self.root, self.original)).write_text("existing", encoding="utf-8")

        completed, events = invoke(self.request("preview"))
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        preview = events[0]
        self.assertEqual(preview["rows"][0]["status"], "冲突，将跳过")

        completed, events = invoke(self.request("execute", preview["plan"]))
        self.assertEqual(completed.returncode, 0, completed.stderr.decode("utf-8", "replace"))
        result = events[-1]
        self.assertEqual(result["type"], "result")
        self.assertTrue(os.path.exists(broken_path))
        self.assertEqual(Path(os.path.join(self.root, self.original)).read_text(encoding="utf-8"), "existing")

    def test_execute_without_plan_fails(self):
        Path(os.path.join(self.root, self.broken)).write_text("x", encoding="utf-8")
        completed, events = invoke(self.request("execute"))
        self.assertEqual(completed.returncode, 1)
        self.assertEqual(events[-1]["type"], "error")
        self.assertIn("预览", events[-1]["message"])


if __name__ == "__main__":
    unittest.main()
