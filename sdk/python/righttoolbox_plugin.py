"""Zero-dependency Python 3 SDK for RightToolbox v1 JSON Lines plugins."""

from __future__ import annotations

import json
import os
import sys
import threading
from dataclasses import dataclass
from typing import Any, Callable, Dict, List, Optional


PROTOCOL_VERSION = 1


@dataclass
class ResultRow:
    source: str
    target: str
    status: str = "就绪"
    detail: str = ""

    def to_dict(self) -> Dict[str, str]:
        return {
            "source": self.source,
            "target": self.target,
            "status": self.status,
            "detail": self.detail,
        }


class PluginContext:
    def __init__(self, request: Dict[str, Any], cancelled: threading.Event, emit: Callable[[Dict[str, Any]], None]):
        self.request = request
        self.command_id: str = request.get("commandId", "")
        self.phase: str = request.get("phase", "execute")
        self.paths: List[str] = list(request.get("paths", []))
        self.parameters: Dict[str, str] = dict(request.get("parameters", {}))
        self.plan: Any = request.get("plan")
        self.cancelled = cancelled
        self._emit = emit

    def param(self, key: str, default: str = "") -> str:
        return self.parameters.get(key, default)

    def bool_param(self, key: str, default: bool = False) -> bool:
        if key not in self.parameters:
            return default
        return self.parameters[key].strip().lower() == "true"

    def int_param(self, key: str, default: int = 0) -> int:
        try:
            return int(self.parameters.get(key, str(default)))
        except ValueError:
            return default

    def report_progress(self, current: int, total: int, message: str) -> None:
        self._emit({
            "type": "progress",
            "current": current,
            "total": total,
            "message": message,
        })

    @staticmethod
    def result(
        message: str,
        rows: Optional[List[ResultRow]] = None,
        outputs: Optional[List[str]] = None,
        clipboard_text: Optional[str] = None,
        undo_id: Optional[str] = None,
    ) -> Dict[str, Any]:
        payload: Dict[str, Any] = {
            "type": "result",
            "message": message,
            "rows": [r.to_dict() for r in (rows or [])],
            "outputs": list(outputs or []),
        }
        if clipboard_text is not None:
            payload["clipboardText"] = clipboard_text
        if undo_id is not None:
            payload["undoId"] = undo_id
        return payload

    @staticmethod
    def preview(message: str, plan: Any, rows: Optional[List[ResultRow]] = None) -> Dict[str, Any]:
        return {
            "type": "preview",
            "message": message,
            "plan": plan,
            "rows": [r.to_dict() for r in (rows or [])],
        }

    @staticmethod
    def unique_output(source_path: str, suffix: str, extension: str, output_folder: str = "") -> str:
        directory = os.path.abspath(output_folder) if output_folder.strip() else os.path.dirname(os.path.abspath(source_path))
        os.makedirs(directory, exist_ok=True)
        stem = os.path.splitext(os.path.basename(source_path))[0] + suffix
        ext = "" if not extension else (extension if extension.startswith(".") else "." + extension)
        for i in range(10000):
            name = f"{stem}{'' if i == 0 else f' ({i})'}{ext}"
            candidate = os.path.join(directory, name)
            if not os.path.exists(candidate):
                return candidate
        raise OSError("同名输出过多，请清理目标目录或更换输出文件夹。")


def run(handler: Callable[[PluginContext], Dict[str, Any]]) -> int:
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    emit_lock = threading.Lock()

    def emit(event: Dict[str, Any]) -> None:
        line = json.dumps(event, ensure_ascii=False, separators=(",", ":"))
        with emit_lock:
            sys.stdout.write(line + "\n")
            sys.stdout.flush()

    try:
        first_line = sys.stdin.readline()
        if not first_line or not first_line.strip():
            raise ValueError("未收到插件调用消息。")
        request = json.loads(first_line)
        if request.get("protocolVersion") != PROTOCOL_VERSION:
            raise ValueError(f"不支持的协议版本：{request.get('protocolVersion')}")

        cancelled = threading.Event()

        def watch_cancel() -> None:
            try:
                for line in sys.stdin:
                    if line.strip() == "CANCEL":
                        cancelled.set()
                        break
            except Exception:
                pass

        watcher = threading.Thread(target=watch_cancel, daemon=True)
        watcher.start()

        ctx = PluginContext(request, cancelled, emit)
        terminal = handler(ctx)
        if not isinstance(terminal, dict) or terminal.get("type") not in ("result", "preview", "error"):
            raise ValueError("插件必须返回 result / preview / error 事件。")

        emit(terminal)
        return 1 if terminal.get("type") == "error" else 0
    except Exception as exc:
        emit({"type": "error", "message": str(exc)})
        return 1
