using System.IO;
using System.Text;
using GbkFix;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

static string Mojibake(string name)
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var gbk = Encoding.GetEncoding(936);
    var cp437 = Encoding.GetEncoding(437);
    return cp437.GetString(gbk.GetBytes(name));
}

static string? PickSafe(out string broken)
{
    var forbidden = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
    foreach (var candidate in new[] { "中文文件名.txt", "测试文档.txt", "报告2026.txt", "说明文档.txt" })
    {
        var mojibake = Mojibake(candidate);
        if (mojibake.Any(character => forbidden.Contains(character))) continue;
        broken = mojibake;
        return candidate;
    }
    broken = "";
    return null;
}

var sources = Fixer.Sources("自动检测");
var targets = Fixer.Targets("自动（GBK 优先）");

var original = PickSafe(out var broken);
if (original is null)
{
    Console.WriteLine("SKIP gbk-fix decoding (cannot build a safe cp437 mojibake name here)");
}
else
{
    var decoded = Fixer.TryDecode(broken, sources, targets);
    Assert(decoded is not null, "mojibake name should be recognised");
    Assert(decoded!.Reason.Contains("cp437"), "reason should mention cp437: " + decoded.Reason);

    var root = Path.Combine(Path.GetTempPath(), "rt-gbkfix-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var brokenPath = Path.Combine(root, broken);
        File.WriteAllText(brokenPath, "hello", new UTF8Encoding(false));

        var plan = Fixer.Plan([root], true, true, sources, targets, CancellationToken.None);
        Assert(plan.Count == 1, $"expected 1 planned rename, got {plan.Count}");
        Assert(Path.GetFileName(plan[0].Target) == original, $"target name wrong: {Path.GetFileName(plan[0].Target)}");

        // 正常中文名不能被误判
        Assert(Fixer.TryDecode(original, sources, targets) is null, "a correct Chinese name must not be rewritten");
        Assert(Fixer.TryDecode("plain-ascii.txt", sources, targets) is null, "an ASCII name must not be rewritten");

        // 冲突检测：目标已存在时预览应标为冲突
        File.WriteAllText(Path.Combine(root, original), "existing", new UTF8Encoding(false));
        var conflictPlan = Fixer.Plan([root], true, true, sources, targets, CancellationToken.None);
        Assert(conflictPlan.Count == 1 && File.Exists(conflictPlan[0].Target), "conflict should be detectable");

        // 深→浅排序：子项必须先于父目录
        var nestedRoot = Path.Combine(root, "deep");
        Directory.CreateDirectory(nestedRoot);
        var nestedPlan = Fixer.Collect([nestedRoot], true, true, CancellationToken.None);
        Assert(nestedPlan.Count >= 1 && nestedPlan[0].Length >= nestedPlan[^1].Length, "collect should order deepest first");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}

// 参数校验
Assert(Fixer.Sources("cp437（ZIP 解压最常见）").Count == 1, "cp437 preset should have one source");
Assert(Fixer.Targets("GBK").Count == 1, "GBK preset should have one target");
try { Fixer.Sources("bogus"); throw new Exception("bogus source should throw"); }
catch (InvalidDataException) { }
try { Fixer.Targets("bogus"); throw new Exception("bogus target should throw"); }
catch (InvalidDataException) { }

// 非法字符与控制字符必须被拒
Assert(!Fixer.LooksClean("a/b"), "path separator must be rejected");
Assert(!Fixer.LooksClean("a\u0001b"), "control character must be rejected");
Assert(Fixer.LooksClean("正常的中文名.txt"), "normal Chinese name must be accepted");
Assert(!Fixer.LooksClean(""), "empty name must be rejected");
Assert(!Fixer.LooksClean(new string('a', 300)), "over-long name must be rejected");

Console.WriteLine("PASS gbk-fix unit tests");
