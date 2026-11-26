import codecs
import os
import re
import sys

SDK_LOCAL = os.path.join(os.path.dirname(__file__), "sdk")
SDK_REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "sdk", "python"))
sys.path.insert(0, SDK_LOCAL if os.path.isdir(SDK_LOCAL) else SDK_REPO)

from righttoolbox_plugin import PluginContext, ResultRow, run  # noqa: E402

ENCODING_CHOICES = ("自动检测", "UTF-8", "GBK")


def load_text(path, preference):
    """返回 (text, encoding, has_bom)；二进制或无法解码时返回 None。"""
    try:
        with open(path, "rb") as handle:
            raw = handle.read()
    except OSError:
        return None
    if not raw or 0 in raw:
        return None

    has_bom = raw.startswith(codecs.BOM_UTF8)
    body = raw[len(codecs.BOM_UTF8):] if has_bom else raw

    if preference == "UTF-8":
        candidates = ["utf-8"]
    elif preference == "GBK":
        candidates = ["gbk"]
    else:
        candidates = ["utf-8", "gbk", "gb18030"]

    for encoding in candidates:
        try:
            return body.decode(encoding), encoding, has_bom
        except UnicodeDecodeError:
            continue
    return None


def save_text(path, text, encoding, has_bom):
    data = text.encode(encoding, errors="strict")
    if has_bom:
        data = codecs.BOM_UTF8 + data
    with open(path, "wb") as handle:
        handle.write(data)


def collect(paths, include_subfolders):
    seen = set()
    for path in paths:
        path = os.path.abspath(path)
        if os.path.isdir(path):
            if include_subfolders:
                for root, _, files in os.walk(path):
                    for name in files:
                        seen.add(os.path.join(root, name))
            else:
                for name in os.listdir(path):
                    full = os.path.join(path, name)
                    if os.path.isfile(full):
                        seen.add(full)
        elif os.path.isfile(path):
            seen.add(path)
    return sorted(seen)


def handle(ctx: PluginContext) -> dict:
    pattern_text = ctx.param("pattern", "")
    if not pattern_text:
        raise RuntimeError("请填写要查找的内容。")

    replacement = ctx.param("replacement", "")
    use_regex = ctx.bool_param("useRegex", False)
    ignore_case = ctx.bool_param("ignoreCase", False)
    backup = ctx.bool_param("backup", True)
    include_subfolders = ctx.bool_param("includeSubfolders", True)
    preference = ctx.param("encoding", "自动检测")
    if preference not in ENCODING_CHOICES:
        raise RuntimeError("文件编码选项无效。")

    try:
        pattern = re.compile(pattern_text if use_regex else re.escape(pattern_text),
                             re.IGNORECASE if ignore_case else 0)
    except re.error as error:
        raise RuntimeError("正则表达式无效：%s" % error)

    if not ctx.paths:
        raise RuntimeError("请先选中要处理的文件或文件夹。")

    files = collect(ctx.paths, include_subfolders)
    if not files:
        raise RuntimeError("没有找到可处理的文件。")

    if ctx.phase == "preview":
        items = []
        rows = []
        total_hits = 0
        total = len(files)
        for index, path in enumerate(files, start=1):
            loaded = load_text(path, preference)
            if loaded is None:
                continue
            text, encoding, _ = loaded
            hits = len(pattern.findall(text))
            if hits == 0:
                continue
            total_hits += hits
            items.append({"path": path, "hits": hits, "encoding": encoding})
            rows.append(ResultRow(os.path.basename(path), "", "%d 处匹配" % hits, encoding))
            ctx.report_progress(index, total, os.path.basename(path))

        if items:
            message = "共 %d 个文件、%d 处匹配（确认后才会写回%s）" % (
                len(items), total_hits, "，并保留 .bak" if backup else "")
        else:
            message = "没有匹配到任何内容。"
        return ctx.preview(message, {"kind": "text-replace", "items": items}, rows)

    plan = ctx.plan or {}
    items = plan.get("items")
    if not isinstance(items, list):
        raise RuntimeError("请先预览并确认要替换的文件。")

    changed_files = 0
    changed_hits = 0
    skipped = 0
    rows = []

    for item in items:
        path = item.get("path") or ""
        if not path or not os.path.isfile(path):
            skipped += 1
            continue
        if ctx.cancelled.is_set():
            raise RuntimeError("操作已取消。")

        loaded = load_text(path, preference)
        if loaded is None:
            skipped += 1
            rows.append(ResultRow(os.path.basename(path), "", "已跳过", "文件已不可读或不再是文本"))
            continue
        text, encoding, has_bom = loaded

        try:
            new_text, count = pattern.subn(replacement, text)
        except re.error as error:
            skipped += 1
            rows.append(ResultRow(os.path.basename(path), "", "失败", "替换表达式无效：%s" % error))
            continue

        if count == 0 or new_text == text:
            skipped += 1
            rows.append(ResultRow(os.path.basename(path), "", "无需改动", "内容已是最新"))
            continue

        try:
            if backup:
                backup_path = path + ".bak"
                if not os.path.exists(backup_path):
                    with open(backup_path, "wb") as handle_out:
                        handle_out.write((codecs.BOM_UTF8 if has_bom else b"") + text.encode(encoding, errors="strict"))
            save_text(path, new_text, encoding, has_bom)
            changed_files += 1
            changed_hits += count
            rows.append(ResultRow(os.path.basename(path), path, "已替换", "%d 处（%s）" % (count, encoding)))
        except (OSError, UnicodeEncodeError) as error:
            skipped += 1
            rows.append(ResultRow(os.path.basename(path), "", "失败", str(error)))

    tail = "，跳过 %d 个" % skipped if skipped else ""
    return ctx.result("已修改 %d 个文件、%d 处匹配%s" % (changed_files, changed_hits, tail), rows=rows)


if __name__ == "__main__":
    raise SystemExit(run(handle))
