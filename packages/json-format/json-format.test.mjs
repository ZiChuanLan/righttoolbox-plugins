import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const entry = path.join(here, "main.mjs");

function invoke(invocation) {
  const result = spawnSync(process.execPath, [entry], {
    input: JSON.stringify(invocation) + "\n",
    encoding: "utf8",
    timeout: 60000,
  });
  const events = result.stdout
    .split(/\r?\n/)
    .filter((line) => line.trim().length > 0)
    .map((line) => JSON.parse(line));
  return { status: result.status, events, stderr: result.stderr };
}

function makeRequest(file, phase, parameters, plan) {
  const invocation = {
    protocolVersion: 1,
    requestId: `test-${phase}`,
    commandId: "community.json-format.apply",
    phase,
    paths: [file],
    parameters,
  };
  if (plan !== undefined) invocation.plan = plan;
  return invocation;
}

const pretty2 = { mode: "美化（2 空格）", backup: "true" };

test("preview then pretty-print keeps a .bak of the original", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "rt-jsonfmt-"));
  try {
    const file = path.join(root, "ugly.json");
    fs.writeFileSync(file, '{"b":1,"a":[1,2]}', "utf8");

    const preview = invoke(makeRequest(file, "preview", pretty2));
    assert.equal(preview.status, 0, preview.stderr);
    assert.equal(preview.events.length, 1);
    assert.equal(preview.events[0].type, "preview");
    assert.equal(preview.events[0].plan.items.length, 1);
    assert.equal(preview.events[0].plan.items[0].ok, true);

    const execute = invoke(makeRequest(file, "execute", pretty2, preview.events[0].plan));
    assert.equal(execute.status, 0, execute.stderr);
    assert.equal(execute.events.at(-1).type, "result");

    const text = fs.readFileSync(file, "utf8");
    assert.equal(text, '{\n  "b": 1,\n  "a": [\n    1,\n    2\n  ]\n}\n');
    assert.equal(fs.readFileSync(`${file}.bak`, "utf8"), '{"b":1,"a":[1,2]}');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("minify collapses to a single line", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "rt-jsonfmt-"));
  try {
    const file = path.join(root, "pretty.json");
    fs.writeFileSync(file, '{\n  "b": 1,\n  "a": 2\n}\n', "utf8");
    const parameters = { mode: "压缩为单行", backup: "false" };

    const preview = invoke(makeRequest(file, "preview", parameters));
    assert.equal(preview.status, 0, preview.stderr);
    const execute = invoke(makeRequest(file, "execute", parameters, preview.events[0].plan));
    assert.equal(execute.status, 0, execute.stderr);

    assert.equal(fs.readFileSync(file, "utf8"), '{"b":1,"a":2}\n');
    assert.equal(fs.existsSync(`${file}.bak`), false);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("sort mode orders keys alphabetically", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "rt-jsonfmt-"));
  try {
    const file = path.join(root, "keys.json");
    fs.writeFileSync(file, '{"z":1,"a":{"y":2,"b":3}}', "utf8");
    const parameters = { mode: "按字母排序键后美化", backup: "false" };

    const preview = invoke(makeRequest(file, "preview", parameters));
    assert.equal(preview.status, 0, preview.stderr);
    const execute = invoke(makeRequest(file, "execute", parameters, preview.events[0].plan));
    assert.equal(execute.status, 0, execute.stderr);

    const parsed = JSON.parse(fs.readFileSync(file, "utf8"));
    assert.deepEqual(Object.keys(parsed), ["a", "z"]);
    assert.deepEqual(Object.keys(parsed.a), ["b", "y"]);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("broken JSON is reported and left untouched", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "rt-jsonfmt-"));
  try {
    const file = path.join(root, "broken.json");
    fs.writeFileSync(file, "{nope", "utf8");

    const preview = invoke(makeRequest(file, "preview", pretty2));
    assert.equal(preview.status, 0, preview.stderr);
    assert.equal(preview.events[0].plan.items[0].ok, false);
    assert.equal(preview.events[0].rows[0].status, "无法处理");

    const execute = invoke(makeRequest(file, "execute", pretty2, preview.events[0].plan));
    assert.equal(execute.status, 0, execute.stderr);
    assert.equal(execute.events.at(-1).rows[0].status, "已跳过");
    assert.equal(fs.readFileSync(file, "utf8"), "{nope");
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("validate mode never writes back", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "rt-jsonfmt-"));
  try {
    const file = path.join(root, "keep.json");
    const original = '{"b":1,"a":2}';
    fs.writeFileSync(file, original, "utf8");
    const parameters = { mode: "仅校验，不写回", backup: "true" };

    const preview = invoke(makeRequest(file, "preview", parameters));
    assert.equal(preview.status, 0, preview.stderr);
    const execute = invoke(makeRequest(file, "execute", parameters, preview.events[0].plan));
    assert.equal(execute.status, 0, execute.stderr);

    assert.equal(fs.readFileSync(file, "utf8"), original);
    assert.equal(fs.existsSync(`${file}.bak`), false);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("execute without a plan is rejected", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "rt-jsonfmt-"));
  try {
    const file = path.join(root, "a.json");
    fs.writeFileSync(file, "{}", "utf8");
    const result = invoke(makeRequest(file, "execute", pretty2));
    assert.equal(result.status, 1);
    assert.equal(result.events.at(-1).type, "error");
    assert.match(result.events.at(-1).message, /预览/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
