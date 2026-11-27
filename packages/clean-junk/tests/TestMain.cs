using CleanJunk;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

var root = Path.Combine(Path.GetTempPath(), "rt-cleanjunk-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");
    File.WriteAllText(Path.Combine(root, "Thumbs.db"), "thumb");
    File.WriteAllText(Path.Combine(root, "~$report.docx"), "lock");
    File.WriteAllText(Path.Combine(root, "scratch.tmp"), "tmp");
    File.WriteAllText(Path.Combine(root, "empty.bin"), "");
    Directory.CreateDirectory(Path.Combine(root, "empty-dir"));
    Directory.CreateDirectory(Path.Combine(root, "nested", "deeper"));
    File.WriteAllText(Path.Combine(root, "nested", "keep.md"), "m");

    Assert(JunkScanner.IsNoise("Thumbs.db", true, false), "Thumbs.db should be noise");
    Assert(!JunkScanner.IsNoise("Thumbs.db", false, false), "Thumbs.db must not be noise when disabled");
    Assert(JunkScanner.IsNoise("~$a.docx", false, true), "~$ prefix should be temp");
    Assert(!JunkScanner.IsNoise("keep.txt", true, true), "keep.txt must survive");

    var items = JunkScanner.Scan([root], true, true, false, true, CancellationToken.None);
    var names = items.Where(i => !i.IsDirectory).Select(i => Path.GetFileName(i.Path)).ToArray();
    Assert(names.Length == 3, "expected 3 junk files, got " + string.Join(",", names));
    Assert(names.Contains("Thumbs.db") && names.Contains("scratch.tmp") && names.Contains("~$report.docx"), "unexpected junk files: " + string.Join(",", names));

    var directoryNames = items.Where(i => i.IsDirectory).Select(i => Path.GetFileName(i.Path)).ToArray();
    Assert(directoryNames.Contains("empty-dir") && directoryNames.Contains("deeper"), "empty directories missing: " + string.Join(",", directoryNames));

    var withEmpty = JunkScanner.Scan([root], true, true, true, false, CancellationToken.None);
    Assert(withEmpty.Any(i => Path.GetFileName(i.Path) == "empty.bin"), "empty.bin should be listed when enabled");

    var outcome = JunkCleaner.Delete(items, CancellationToken.None);
    Assert(outcome.Files == 3, $"expected 3 deleted files, got {outcome.Files}");
    Assert(outcome.Directories == 2, $"expected 2 deleted directories, got {outcome.Directories}");
    Assert(File.Exists(Path.Combine(root, "keep.txt")) && File.Exists(Path.Combine(root, "nested", "keep.md")), "user files must survive");
    Assert(File.Exists(Path.Combine(root, "empty.bin")), "0-byte file must survive when not selected");
    Assert(!Directory.Exists(Path.Combine(root, "empty-dir")), "empty-dir should be removed");

    Directory.CreateDirectory(Path.Combine(root, "guarded"));
    File.WriteAllText(Path.Combine(root, "guarded", "x.txt"), "x");
    var guardOutcome = JunkCleaner.Delete([new JunkItem(Path.Combine(root, "guarded"), true, 0)], CancellationToken.None);
    Assert(guardOutcome.Directories == 0 && guardOutcome.Skipped == 1, "non-empty directory must be skipped");
    Assert(Directory.Exists(Path.Combine(root, "guarded")), "guarded dir must survive");

    Console.WriteLine("PASS clean-junk unit tests");
}
finally
{
    Directory.Delete(root, true);
}
