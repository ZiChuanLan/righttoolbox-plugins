using System.IO;
using System.Text;
using TextReplace;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

var root = Path.Combine(Path.GetTempPath(), "rt-textrep-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var notes = Path.Combine(root, "notes.txt");
    File.WriteAllText(notes, "hello world\nsecond world\n", new UTF8Encoding(false));

    var binary = Path.Combine(root, "blob.txt");
    File.WriteAllBytes(binary, [0x77, 0x00, 0x6F]);

    var loaded = Replacer.Load(notes, "自动检测");
    Assert(loaded is not null && loaded.Text.Contains("world"), "utf8 file should load");
    Assert(Replacer.Load(binary, "自动检测") is null, "binary file must be skipped");

    var regex = Replacer.Build("world", false, false);
    Assert(regex.Matches(loaded!.Text).Count == 2, "literal match count wrong");

    var replaced = regex.Replace(loaded.Text, "RightToolbox");
    Assert(replaced == "hello RightToolbox\nsecond RightToolbox\n", "replacement result wrong");
    Assert(Encoding.UTF8.GetString(Replacer.Save(loaded, replaced)) == replaced, "save round-trip wrong");

    var capture = Replacer.Build(@"(hello|second)\s+world", true, false);
    Assert(capture.Replace(loaded.Text, "W") == "W\nW\n", "regex replacement wrong");
    var withGroup = Replacer.Build(@"((\w+)) world", true, false);
    Assert(withGroup.Replace("hello world", "$1 there") == "hello there", "capture group reference wrong");

    var insensitive = Replacer.Build("WORLD", false, true);
    Assert(insensitive.Matches(loaded.Text).Count == 2, "ignoreCase should match both");

    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var gbk = Encoding.GetEncoding(936);
    var gbkPath = Path.Combine(root, "gbk.txt");
    File.WriteAllBytes(gbkPath, gbk.GetBytes("第一行 world 中文"));
    var gbkLoaded = Replacer.Load(gbkPath, "自动检测");
    Assert(gbkLoaded is not null && gbkLoaded.Text.Contains("第一行"), "GBK file should decode");
    Assert(gbkLoaded!.Encoding.CodePage == 936, $"GBK file should stay GBK, got {gbkLoaded.Encoding.CodePage}");
    var gbkOut = Replacer.Save(gbkLoaded, gbkLoaded.Text.Replace("world", "已替换"));
    Assert(gbk.GetString(gbkOut).Contains("已替换"), "GBK content should survive the write-back");

    var bomPath = Path.Combine(root, "bom.txt");
    File.WriteAllBytes(bomPath, [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes("world")]);
    var bomLoaded = Replacer.Load(bomPath, "自动检测");
    Assert(bomLoaded is not null && bomLoaded.HasBom, "BOM should be detected");
    Assert(Replacer.Save(bomLoaded!, "x")[0] == 0xEF, "BOM should be preserved");

    Directory.CreateDirectory(Path.Combine(root, "sub"));
    File.WriteAllText(Path.Combine(root, "sub", "a.txt"), "world", new UTF8Encoding(false));
    Assert(Replacer.Collect([root], true, CancellationToken.None).Count == 5, "recursive collect count wrong");
    Assert(Replacer.Collect([root], false, CancellationToken.None).Count == 4, "non-recursive collect count wrong");

    try { Replacer.Build("", false, false); throw new Exception("empty pattern should throw"); }
    catch (InvalidDataException) { }
    try { Replacer.Build("([", true, false); throw new Exception("invalid regex should throw"); }
    catch (InvalidDataException) { }
    try { Replacer.Load(notes, "bogus"); throw new Exception("bogus encoding should throw"); }
    catch (InvalidDataException) { }

    Console.WriteLine("PASS text-replace unit tests");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}
