import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const sdkLocal = path.join(here, "sdk", "righttoolbox-plugin.mjs");
const sdkRepo = path.resolve(here, "..", "..", "sdk", "node", "righttoolbox-plugin.mjs");
const { row, runPlugin } = await import(fs.existsSync(sdkLocal) ? `file:///${sdkLocal.replace(/\\/g, "/")}` : `file:///${sdkRepo.replace(/\\/g, "/")}`);

await runPlugin(async (ctx) => {
  const rows = [];
  for (let i = 0; i < ctx.paths.length; i++) {
    if (ctx.signal.aborted) throw new Error("操作已取消。");
    const target = ctx.paths[i];
    const stat = fs.statSync(target);
    rows.push(row(path.basename(target), target, "已处理", stat.isDirectory() ? "文件夹" : `${stat.size} B`));
    ctx.reportProgress(i + 1, ctx.paths.length, path.basename(target));
  }
  return ctx.result(`已处理 ${rows.length} 项`, { rows });
});
