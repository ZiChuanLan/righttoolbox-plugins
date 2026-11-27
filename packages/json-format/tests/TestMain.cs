using System.IO;
using System.Text;
using JsonFormat;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

var root = Path.Combine(Path.GetTempPath(), "rt-jsonfmt-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var pretty2 = Path.Combine(root, "a.json");
    File.WriteAllText(pretty2, "{\"b\":1,\"a\":[1,2],\"name\":\"中文\"}", new UTF8Encoding(false));
    var outcome = Formatter.Process(pretty2, Formatter.Pretty2);
    Assert(outcome.Ok && outcome.Changed, "pretty print should change the file");
    var text = Encoding.UTF8.GetString(outcome.Output);
    Assert(text.Contains("\n  \"b\": 1"), "2-space indentation missing");
    Assert(text.Contains("中文"), "Chinese must not be escaped");
    Assert(!text.Contains("\\u4e2d"), "Chinese was escaped");
    Assert(outcome.Original.Length > 0 && File.ReadAllText(pretty2).Contains("\"b\":1"), "original file must stay untouched");

    var four = Formatter.Process(pretty2, Formatter.Pretty4);
    Assert(Encoding.UTF8.GetString(four.Output).Contains("\n    \"b\": 1"), "4-space indentation missing");

    var minified = Formatter.Process(pretty2, Formatter.Minify);
    Assert(Encoding.UTF8.GetString(minified.Output).Trim() == "{\"b\":1,\"a\":[1,2],\"name\":\"中文\"}", "minify result wrong");

    var sorted = Formatter.Process(pretty2, Formatter.Sort);
    var sortedText = Encoding.UTF8.GetString(sorted.Output);
    Assert(sortedText.IndexOf("\"a\"", StringComparison.Ordinal) < sortedText.IndexOf("\"b\"", StringComparison.Ordinal), "keys not sorted: " + sortedText);
    Assert(sortedText.IndexOf("\"b\"", StringComparison.Ordinal) < sortedText.IndexOf("\"name\"", StringComparison.Ordinal), "keys not sorted (b/name)");

    // 排序必须递归
    var nested = Path.Combine(root, "nested.json");
    File.WriteAllText(nested, "{\"z\":{\"y\":1,\"b\":2},\"a\":[{\"d\":3,\"c\":4}]}", new UTF8Encoding(false));
    var nestedSorted = Encoding.UTF8.GetString(Formatter.Process(nested, Formatter.Sort).Output);
    Assert(nestedSorted.IndexOf("\"b\": 2", StringComparison.Ordinal) < nestedSorted.IndexOf("\"y\": 1", StringComparison.Ordinal), "nested object not sorted");
    Assert(nestedSorted.IndexOf("\"c\": 4", StringComparison.Ordinal) < nestedSorted.IndexOf("\"d\": 3", StringComparison.Ordinal), "array object not sorted");

    // 校验模式不产出写入内容
    var validate = Formatter.Process(pretty2, Formatter.Validate);
    Assert(validate.Ok && !validate.Changed && validate.Output.Length == 0, "validate must not produce output");

    // 语法错误与空文件
    var broken = Path.Combine(root, "broken.json");
    File.WriteAllText(broken, "{nope", new UTF8Encoding(false));
    var brokenOutcome = Formatter.Process(broken, Formatter.Pretty2);
    Assert(!brokenOutcome.Ok && brokenOutcome.Error.Contains("语法错误"), "broken json should report a syntax error");
    var empty = Path.Combine(root, "empty.json");
    File.WriteAllText(empty, "", new UTF8Encoding(false));
    Assert(!Formatter.Process(empty, Formatter.Pretty2).Ok, "empty file should fail");

    // 注释与尾逗号（.jsonc）必须支持
    var jsonc = Path.Combine(root, "c.jsonc");
    File.WriteAllText(jsonc, "{\n  // comment\n  \"a\": 1,\n}", new UTF8Encoding(false));
    Assert(Formatter.Process(jsonc, Formatter.Pretty2).Ok, "jsonc with comments and trailing commas should parse");

    // BOM 保留
    var bomPath = Path.Combine(root, "bom.json");
    File.WriteAllBytes(bomPath, [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes("{\"a\":1}")]);
    var bomOutcome = Formatter.Process(bomPath, Formatter.Pretty2);
    Assert(bomOutcome.Ok && bomOutcome.Output[0] == 0xEF && bomOutcome.Output[1] == 0xBB, "BOM must be preserved");

    // CRLF 保留
    var crlf = Path.Combine(root, "crlf.json");
    File.WriteAllText(crlf, "{\r\n  \"a\": 1\r\n}\r\n", new UTF8Encoding(false));
    Assert(Encoding.UTF8.GetString(Formatter.Process(crlf, Formatter.Pretty2).Output).Contains("\r\n"), "CRLF style must be preserved");

    Assert(Formatter.IsMode(Formatter.Sort) && !Formatter.IsMode("bogus"), "mode validation wrong");

    Console.WriteLine("PASS json-format unit tests");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}
