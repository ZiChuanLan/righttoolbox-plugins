import os
import sys
import unicodedata

SDK_LOCAL = os.path.join(os.path.dirname(__file__), "sdk")
SDK_REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "sdk", "python"))
sys.path.insert(0, SDK_LOCAL if os.path.isdir(SDK_LOCAL) else SDK_REPO)

from righttoolbox_plugin import PluginContext, ResultRow, run  # noqa: E402

FORBIDDEN = set('/\\:*?"<>|')

SOURCE_ENCODINGS = {
    "自动检测": ["cp437", "latin-1"],
    "cp437（ZIP 解压最常见）": ["cp437"],
    "latin-1 / ISO-8859-1": ["latin-1"],
}

TARGET_ENCODINGS = {
    "自动（GBK 优先）": ["gbk", "gb18030", "utf-8"],
    "GBK": ["gbk"],
    "GB18030": ["gb18030"],
    "UTF-8": ["utf-8"],
}


def looks_clean(text: str) -> bool:
    if not text or len(text) > 200:
        return False
    for char in text:
        if char in FORBIDDEN:
            return False
        if unicodedata.category(char) in ("Cc", "Cf", "Cs", "Co", "Cn"):
            return False
    return True


def decode_candidate(name, source_encodings, target_encodings):
    for source in source_encodings:
        try:
            raw = name.encode(source)
        except (UnicodeEncodeError, LookupError):
            continue
        for target in target_encodings:
            try:
                decoded = raw.decode(target)
            except (UnicodeDecodeError, LookupError):
                continue
            if decoded == name or not looks_clean(decoded):
                continue
            try:
                if len(decoded.encode("utf-8")) > 240:
                    continue
            except UnicodeEncodeError:
                continue
            return decoded, source, target
    return None


def collect(paths, recursive, include_folders):
    seen = set()
    for path in paths:
        path = os.path.abspath(path)
        if os.path.isdir(path):
            seen.add(path)
            if recursive:
                for root, dirs, files in os.walk(path):
                    for name in files:
                        seen.add(os.path.join(root, name))
                    if include_folders:
                        for name in dirs:
                            seen.add(os.path.join(root, name))
        elif os.path.exists(path):
            seen.add(path)
    # 先深后浅：父目录改名不会让已经排好的子项路径失效。
    return sorted(seen, key=lambda p: (p.count(os.sep), len(p)), reverse=True)


def handle(ctx: PluginContext) -> dict:
    if not ctx.paths:
        raise RuntimeError("请先选中要修复的文件或文件夹。")

    source_encodings = SOURCE_ENCODINGS.get(ctx.param("sourceEncoding", "自动检测"))
    target_encodings = TARGET_ENCODINGS.get(ctx.param("targetEncoding", "自动（GBK 优先）"))
    if not source_encodings or not target_encodings:
        raise RuntimeError("编码选项无效。")

    recursive = ctx.bool_param("recursive", True)
    include_folders = ctx.bool_param("includeFolders", True)

    if ctx.phase == "preview":
        items = []
        rows = []
        for path in collect(ctx.paths, recursive, include_folders):
            found = decode_candidate(os.path.basename(path), source_encodings, target_encodings)
            if not found:
                continue
            decoded, source, target = found
            new_path = os.path.join(os.path.dirname(path), decoded)
            conflict = os.path.exists(new_path) and os.path.normcase(new_path) != os.path.normcase(path)
            items.append({
                "source": path,
                "target": new_path,
                "reason": "%s → %s" % (source, target),
            })
            rows.append(ResultRow(
                os.path.basename(path),
                decoded,
                "冲突，将跳过" if conflict else "待改名",
                items[-1]["reason"],
            ))

        message = ("共 %d 个名称可修复（确认后才会真正改名）" % len(items)) if items else \
            "没有检测到乱码文件名，可能本身已经是正确的名字。"
        return ctx.preview(message, {"kind": "gbk-fix", "items": items}, rows)

    plan = ctx.plan or {}
    items = plan.get("items")
    if not isinstance(items, list):
        raise RuntimeError("请先预览并确认要修复的名称。")

    renamed = 0
    skipped = 0
    failed = 0
    rows = []
    for item in items:
        source_path = item.get("source") or ""
        target_path = item.get("target") or ""
        if not source_path or not target_path:
            continue
        if os.path.normcase(source_path) == os.path.normcase(target_path):
            continue
        if not os.path.exists(source_path):
            skipped += 1
            rows.append(ResultRow(os.path.basename(source_path), os.path.basename(target_path), "已跳过", "源文件已不存在"))
            continue
        if os.path.exists(target_path):
            skipped += 1
            rows.append(ResultRow(os.path.basename(source_path), os.path.basename(target_path), "已跳过", "目标名称已被占用"))
            continue
        try:
            os.rename(source_path, target_path)
            renamed += 1
            rows.append(ResultRow(os.path.basename(source_path), os.path.basename(target_path), "已改名", item.get("reason", "")))
        except OSError as error:
            failed += 1
            rows.append(ResultRow(os.path.basename(source_path), os.path.basename(target_path), "失败", str(error)))

    return ctx.result(
        "已改名 %d 项，跳过 %d 项，失败 %d 项" % (renamed, skipped, failed),
        rows=rows,
    )


if __name__ == "__main__":
    raise SystemExit(run(handle))
