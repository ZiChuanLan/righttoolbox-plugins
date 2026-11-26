using DirTree;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

var temp = Path.Combine(Path.GetTempPath(), "rt-dirtree-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(temp, "src"));
Directory.CreateDirectory(Path.Combine(temp, "node_modules", "pkg"));
try
{
    File.WriteAllText(Path.Combine(temp, "README.md"), "# hi");
    File.WriteAllText(Path.Combine(temp, "src", "app.cs"), "class A {}");
    File.WriteAllText(Path.Combine(temp, "node_modules", "pkg", "ignored.js"), "x");

    var tree = TreeBuilder.BuildTree(temp, "Unicode 树状图", 4, true, true, out var dirs, out var files);
    Assert(dirs == 1 && files == 2, $"Expected 1 dir & 2 files, got {dirs}/{files}");
    Assert(!tree.Contains("node_modules"), "Should ignore node_modules");

    var records = TreeBuilder.CollectManifest([temp], includeHash: true);
    Assert(records.Count == 3 && records.All(r => r.Sha256.Length == 64), "Expected 3 hashed manifest records");

    Console.WriteLine("PASS dir-tree unit tests");
}
finally
{
    Directory.Delete(temp, true);
}
