// Zero-dependency Node.js / Bun ESM SDK for RightToolbox v1 JSON Lines plugins.

import fs from "node:fs";
import path from "node:path";
import readline from "node:readline";

export const PROTOCOL_VERSION = 1;

export function row(source, target, status = "就绪", detail = "") {
  return { source, target, status, detail };
}

export function uniqueOutput(sourcePath, suffix, extension, outputFolder = "") {
  const directory = outputFolder && outputFolder.trim()
    ? path.resolve(outputFolder)
    : path.dirname(path.resolve(sourcePath));
  fs.mkdirSync(directory, { recursive: true });
  const parsed = path.parse(sourcePath);
  const stem = parsed.name + suffix;
  const ext = !extension ? "" : (extension.startsWith(".") ? extension : `.${extension}`);
  for (let i = 0; i < 10000; i++) {
    const name = `${stem}${i === 0 ? "" : ` (${i})`}${ext}`;
    const candidate = path.join(directory, name);
    if (!fs.existsSync(candidate)) return candidate;
  }
  throw new Error("同名输出过多，请清理目标目录或更换输出文件夹。");
}

function emit(event) {
  process.stdout.write(JSON.stringify(event) + "\n");
}

export async function runPlugin(handler) {
  process.stdin.setEncoding("utf8");
  const controller = new AbortController();
  const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
  const iterator = rl[Symbol.asyncIterator]();

  try {
    const first = await iterator.next();
    if (first.done || !first.value.trim()) {
      throw new Error("未收到插件调用消息。");
    }

    const request = JSON.parse(first.value);
    if (request.protocolVersion !== PROTOCOL_VERSION) {
      throw new Error(`不支持的协议版本：${request.protocolVersion}`);
    }

    // Continue watching stdin in the background for cooperative CANCEL
    (async () => {
      try {
        while (true) {
          const next = await iterator.next();
          if (next.done) break;
          if (next.value.trim() === "CANCEL") {
            controller.abort();
            break;
          }
        }
      } catch {
        // ignore closed stdin
      }
    })();

    const ctx = {
      request,
      commandId: request.commandId ?? "",
      phase: request.phase ?? "execute",
      paths: Array.isArray(request.paths) ? request.paths : [],
      parameters: request.parameters ?? {},
      plan: request.plan ?? null,
      signal: controller.signal,
      param(key, fallback = "") {
        return Object.prototype.hasOwnProperty.call(this.parameters, key)
          ? String(this.parameters[key])
          : fallback;
      },
      boolParam(key, fallback = false) {
        if (!Object.prototype.hasOwnProperty.call(this.parameters, key)) return fallback;
        return String(this.parameters[key]).trim().toLowerCase() === "true";
      },
      intParam(key, fallback = 0) {
        const parsed = Number.parseInt(this.param(key, String(fallback)), 10);
        return Number.isFinite(parsed) ? parsed : fallback;
      },
      reportProgress(current, total, message) {
        emit({ type: "progress", current, total, message });
      },
      result(message, { rows = [], outputs = [], clipboardText = undefined, undoId = undefined } = {}) {
        const evt = { type: "result", message, rows, outputs };
        if (clipboardText !== undefined) evt.clipboardText = clipboardText;
        if (undoId !== undefined) evt.undoId = undoId;
        return evt;
      },
      preview(message, plan, rows = []) {
        return { type: "preview", message, plan, rows };
      },
      uniqueOutput,
    };

    const terminal = await handler(ctx);
    if (!terminal || !["result", "preview", "error"].includes(terminal.type)) {
      throw new Error("插件必须返回 result / preview / error 事件。");
    }

    emit(terminal);
    rl.close();
    process.exitCode = terminal.type === "error" ? 1 : 0;
  } catch (err) {
    emit({ type: "error", message: err instanceof Error ? err.message : String(err) });
    rl.close();
    process.exitCode = 1;
  }
}
