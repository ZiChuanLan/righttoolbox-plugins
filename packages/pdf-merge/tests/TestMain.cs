using System.IO;
using PdfMerge;
using PdfSharp.Pdf;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

string CreatePdf(string directory, string name, int pages)
{
    var path = Path.Combine(directory, name);
    using var document = new PdfDocument();
    for (var index = 0; index < pages; index++) document.AddPage();
    document.Save(path);
    return path;
}

var root = Path.Combine(Path.GetTempPath(), "rt-pdf-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var first = CreatePdf(root, "a.pdf", 2);
    var second = CreatePdf(root, "b.pdf", 3);
    Assert(PdfTools.CountPages(first) == 2, "fixture a should have 2 pages");

    // 合并
    var merged = Path.Combine(root, "merged.pdf");
    PdfTools.Merge([first, second], merged);
    Assert(PdfTools.CountPages(merged) == 5, $"merged should have 5 pages, got {PdfTools.CountPages(merged)}");

    // 拆分
    var parts = PdfTools.Split(second, root, "_page");
    Assert(parts.Count == 3, $"expected 3 split files, got {parts.Count}");
    Assert(parts.All(part => File.Exists(part.Output) && part.Pages == 1), "every split file must exist with 1 page");
    Assert(parts.All(part => part.Output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)), $"split files must keep the .pdf extension: {parts[0].Output}");
    Assert(Path.GetExtension(merged).Equals(".pdf", StringComparison.OrdinalIgnoreCase), $"merged output must keep .pdf: {merged}");
    
    Assert(parts.All(part => PdfTools.CountPages(part.Output) == 1), "each split file must really contain a single page");
    Assert(Path.GetFileName(parts[1].Output).StartsWith("b_page02", StringComparison.Ordinal), $"unexpected split name {parts[1].Output}");

    // 提取页码
    var (extracted, count) = PdfTools.Extract(second, root, "1,3");
    Assert(Path.GetExtension(extracted).Equals(".pdf", StringComparison.OrdinalIgnoreCase), $"extracted output must keep .pdf: {extracted}");
    Assert(count == 2 && PdfTools.CountPages(extracted) == 2, $"extract should give 2 pages, got {count}");
    var (all, allCount) = PdfTools.Extract(second, root, "");
    Assert(allCount == 3, $"blank range should keep every page, got {allCount}");
    var (reversed, reversedCount) = PdfTools.Extract(second, root, "5-3");
    Assert(reversedCount == 1, $"5-3 on a 3-page PDF should normalise to page 3 only, got {reversedCount}");

    // 页码解析
    Assert(PageSelection.Parse("1-3,5", 10).SequenceEqual([1, 2, 3, 5]), "page parsing wrong");
    Assert(PageSelection.Parse("2,2,1", 5).SequenceEqual([1, 2]), "page parsing must dedupe and sort");
    Assert(PageSelection.Parse("8-20", 10).SequenceEqual([8, 9, 10]), "out-of-range pages must clamp");
    Assert(PageSelection.Parse("", 4).Count == 4, "blank spec means all pages");
    try { PageSelection.Parse("abc", 5); throw new Exception("bad spec should throw"); }
    catch (InvalidDataException) { }
    try { PageSelection.Parse("99", 5); throw new Exception("fully out-of-range should throw"); }
    catch (InvalidDataException) { }

    // 合并失败保护：非 PDF 内容不应产生输出
    var fake = Path.Combine(root, "fake.pdf");
    File.WriteAllText(fake, "not a pdf");
    var broken = Path.Combine(root, "broken.pdf");
    var threw = false;
    try { PdfTools.Merge([first, fake], broken); }
    catch (Exception) { threw = true; }
    Assert(threw, "merging a corrupt PDF should fail loudly");

    Console.WriteLine("PASS pdf-merge unit tests");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}
