import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const sdkLocal = path.join(here, "sdk", "righttoolbox-plugin.mjs");
const sdkRepo = path.resolve(here, "..", "..", "sdk", "node", "righttoolbox-plugin.mjs");
const { row, runPlugin } = await import(fs.existsSync(sdkLocal) ? `file:///${sdkLocal.replace(/\\/g, "/")}` : `file:///${sdkRepo.replace(/\\/g, "/")}`);

const MODE_PRETTY_2 = "美化（2 空格）";
const MODE_PRETTY_4 = "美化（4 空格）";
const MODE_MINIFY = "压缩为单行";
const MODE_SORT = "按字母排序键后美化";
const MODE_VALIDATE = "仅校验，不写回";

function sortValue(value) {
  if (Array.isArray(value)) return value.map(sortValue);
  if (value && typeof value === "object") {
    const sorted = {};
    for (const key of Object.keys(value).sort()) sorted[key] = sortValue(value[key]);
    return sorted;
  }
  return value;
}

function analyze(target, mode) {
  if (!fs.existsSync(target)) return { path: target, ok: false, error: "文件已不存在" };
  if (!fs.statSync(target).isFile()) return { path: target, ok: false, error: "不是文件" };

  let text;
  try {
    text = fs.readFileSync(target, "utf8");
  } catch (error) {
    return { path: target, ok: false, error: `读取失败：${error.message}` };
  }

  const hasBom = text.charCodeAt(0) === 0xfeff;
  const body = hasBom ? text.slice(1) : text;
  if (!body.trim()) return { path: target, ok: false, error: "文件为空" };

  let parsed;
  try {
    parsed = JSON.parse(body);
  } catch (error) {
    return { path: target, ok: false, error: `JSON 语法错误：${error.message}` };
  }

  const bytesBefore = Buffer.byteLength(text);
  if (mode === MODE_VALIDATE) {
    return { path: target, ok: true, changed: false, bytesBefore, bytesAfter: bytesBefore, status: "仅校验", detail: "语法有效" };
  }

  const source = mode === MODE_SORT ? sortValue(parsed) : parsed;
  const indent = mode === MODE_PRETTY_4 ? 4 : mode === MODE_MINIFY ? 0 : 2;
  let output = JSON.stringify(source, null, indent);
  output += text.includes("\r\n") ? "\r\n" : "\n";
  if (hasBom) output = "\ufeff" + output;

  const bytesAfter = Buffer.byteLength(output);
  if (output === text) {
    return { path: target, ok: true, changed: false, bytesBefore, bytesAfter, output, text, status: "无需改动", detail: "已是目标格式" };
  }
  return { path: target, ok: true, changed: true, bytesBefore, bytesAfter, output, text, status: "待格式化", detail: mode };
}

await runPlugin(async (ctx) => {
  if (ctx.paths.length === 0) throw new Error("请先选中 JSON 文件。");

  const mode = ctx.param("mode", MODE_PRETTY_2);
  const backup = ctx.boolParam("backup", true);
  const known = [MODE_PRETTY_2, MODE_PRETTY_4, MODE_MINIFY, MODE_SORT, MODE_VALIDATE];
  if (!known.includes(mode)) throw new Error("处理方式无效。");

  if (ctx.phase === "preview") {
    const items = ctx.paths.map((target) => analyze(target, mode));
    const rows = items.map((item) => row(path.basename(item.path), "", item.status ?? "无法处理", item.error ?? item.detail ?? ""));
    const usable = items.filter((item) => item.ok && item.changed).length;
    const suffix = mode === MODE_VALIDATE ? "（仅校验，不写回）" : backup ? "（确认后写回，保留 .bak）" : "（确认后写回，不保留备份）";
    return ctx.preview(
      `可处理 ${usable} 个文件${suffix}`,
      { kind: "json-format", items: items.map((item) => ({ path: item.path, ok: item.ok === true, error: item.error ?? "" })) },
      rows
    );
  }

  const plan = ctx.plan ?? {};
  if (!Array.isArray(plan.items)) throw new Error("请先预览并确认要格式化的文件。");

  const rows = [];
  let changed = 0;
  let skipped = 0;

  for (const item of plan.items) {
    if (!item.ok) {
      skipped += 1;
      rows.push(row(path.basename(item.path), "", "已跳过", item.error || "预览时不可处理"));
      continue;
    }
    if (ctx.signal.aborted) throw new Error("操作已取消。");

    const result = analyze(item.path, mode);
    if (!result.ok) {
      skipped += 1;
      rows.push(row(path.basename(item.path), "", "失败", result.error));
      continue;
    }
    if (!result.changed) {
      rows.push(row(path.basename(item.path), "", "无需改动", result.detail ?? ""));
      continue;
    }

    try {
      if (backup) {
        const bak = item.path + ".bak";
        if (!fs.existsSync(bak)) fs.writeFileSync(bak, result.text, "utf8");
      }
      fs.writeFileSync(item.path, result.output, "utf8");
      changed += 1;
      rows.push(row(path.basename(item.path), item.path, "已写回", `${result.bytesBefore} → ${result.bytesAfter} 字节`));
    } catch (error) {
      skipped += 1;
      rows.push(row(path.basename(item.path), "", "失败", error.message));
    }
  }

  const summary = mode === MODE_VALIDATE
    ? `已校验 ${plan.items.length} 个文件，语法有效`
    : `已格式化 ${changed} 个文件${skipped > 0 ? `，跳过 ${skipped} 个` : ""}`;
  return ctx.result(summary, { rows });
});
