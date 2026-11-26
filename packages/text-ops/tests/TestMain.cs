using System.Text;
using TextOps;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

var temp = Path.Combine(Path.GetTempPath(), "rt-textops-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var sample = Path.Combine(temp, "notes.txt");
    File.WriteAllText(sample, "banana\napple\n\nbanana\n你好 world\n", new UTF8Encoding(false));

    var stats = TextProcessor.Analyze(sample);
    Assert(stats.NonEmptyLines == 4, $"Expected 4 non-empty lines, got {stats.NonEmptyLines}");
    Assert(stats.Words >= 6, $"Expected >=6 tokens, got {stats.Words}");

    var cleaned = TextProcessor.CleanupText(File.ReadAllText(sample), true, "升序 (A→Z)", true, true, out var before, out var after);
    Assert(before == 6 && after == 3, $"Expected 6->3, got {before}->{after}");
    var lines = TextProcessor.SplitLines(cleaned);
    Assert(lines[0] == "apple" && lines[1] == "banana" && lines[2] == "你好 world", "Unexpected sort/dedupe order");

    Console.WriteLine("PASS text-ops unit tests");
}
finally
{
    Directory.Delete(temp, true);
}
