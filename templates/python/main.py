import os
import sys

SDK_LOCAL = os.path.join(os.path.dirname(__file__), "sdk")
SDK_REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "sdk", "python"))
sys.path.insert(0, SDK_LOCAL if os.path.isdir(SDK_LOCAL) else SDK_REPO)

from righttoolbox_plugin import PluginContext, ResultRow, run  # noqa: E402


def handle(ctx: PluginContext) -> dict:
    rows = []
    total = len(ctx.paths)
    for idx, path in enumerate(ctx.paths, start=1):
        if ctx.cancelled.is_set():
            raise RuntimeError("操作已取消。")
        detail = "文件夹" if os.path.isdir(path) else f"{os.path.getsize(path):,} B"
        rows.append(ResultRow(os.path.basename(path), path, "已处理", detail))
        ctx.report_progress(idx, total, os.path.basename(path))

    return ctx.result(f"已处理 {len(rows)} 项", rows=rows)


if __name__ == "__main__":
    raise SystemExit(run(handle))
