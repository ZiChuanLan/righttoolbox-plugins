using System.Globalization;
using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using RightToolbox.PluginSdk;

namespace PdfMerge;

public static class PageSelection
{
    /// <summary>Parses "1-3,5" into 1-based page numbers, clamped to the document.</summary>
    public static List<int> Parse(string spec, int pageCount)
    {
        if (pageCount <= 0) return [];
        if (string.IsNullOrWhiteSpace(spec)) return Enumerable.Range(1, pageCount).ToList();

        var pages = new SortedSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            try
            {
                if (dash > 0)
                {
                    var from = int.Parse(part[..dash].Trim(), CultureInfo.InvariantCulture);
                    var to = int.Parse(part[(dash + 1)..].Trim(), CultureInfo.InvariantCulture);
                    if (from > to) (from, to) = (to, from);
                    for (var page = Math.Max(1, from); page <= Math.Min(pageCount, to); page++) pages.Add(page);
                }
                else
                {
                    var page = int.Parse(part, CultureInfo.InvariantCulture);
                    if (page >= 1 && page <= pageCount) pages.Add(page);
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new InvalidDataException($"页码「{part}」无法解析，示例：1-3,5");
            }
        }

        if (pages.Count == 0) throw new InvalidDataException($"指定页码超出了范围（该 PDF 共 {pageCount} 页）。");
        return pages.ToList();
    }
}

public static class PdfTools
{
    public static void Merge(IReadOnlyList<string> inputs, string output)
    {
        using var target = new PdfDocument();
        foreach (var input in inputs)
        {
            using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            for (var index = 0; index < source.PageCount; index++) target.AddPage(source.Pages[index]);
        }
        if (target.PageCount == 0) throw new InvalidDataException("这些 PDF 里没有任何页面。");
        target.Save(output);
    }

    public static List<(string Output, int Pages)> Split(string input, string directory, string stemSuffix)
    {
        var results = new List<(string, int)>();
        using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        var stem = Path.GetFileNameWithoutExtension(input);
        var extension = Path.GetExtension(input);
        for (var index = 0; index < source.PageCount; index++)
        {
            using var single = new PdfDocument();
            single.AddPage(source.Pages[index]);
            var output = PluginContext.UniqueFilePath(Path.Combine(directory, $"{stem}{stemSuffix}{index + 1:D2}{extension}"));
            single.Save(output);
            results.Add((output, 1));
        }
        return results;
    }

    public static (string Output, int Pages) Extract(string input, string directory, string pages)
    {
        using var source = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        var selection = PageSelection.Parse(pages, source.PageCount);
        using var target = new PdfDocument();
        foreach (var page in selection) target.AddPage(source.Pages[page - 1]);
        var stem = Path.GetFileNameWithoutExtension(input);
        var extension = Path.GetExtension(input);
        var output = PluginContext.UniqueFilePath(Path.Combine(directory, $"{stem}_extract{extension}"));
        var extracted = selection.Count;
        target.Save(output);
        return (output, extracted);
    }

    public static int CountPages(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }
}

public static class Program
{
    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx)
    {
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中 PDF 文件。");
        foreach (var path in ctx.Paths)
            if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"不是 PDF 文件：{Path.GetFileName(path)}");

        var action = ctx.Param("action", "合并为一个 PDF");
        var pages = ctx.Param("pages", "");
        var outputFolder = ctx.Param("outputFolder", "");
        var rows = new List<ResultRow>();
        var outputs = new List<string>();

        switch (action)
        {
            case "合并为一个 PDF":
            {
                if (ctx.Paths.Length < 2) throw new InvalidDataException("合并至少需要选中两个 PDF。");
                var directory = string.IsNullOrWhiteSpace(outputFolder)
                    ? Path.GetDirectoryName(ctx.Paths[0]) ?? Environment.CurrentDirectory
                    : Path.GetFullPath(outputFolder);
                Directory.CreateDirectory(directory);
                var output = PluginContext.UniqueFilePath(Path.Combine(directory, Path.GetFileNameWithoutExtension(ctx.Paths[0]) + "_merged.pdf"));
                PdfTools.Merge(ctx.Paths, output);
                var total = PdfTools.CountPages(output);
                rows.Add(new ResultRow($"{ctx.Paths.Length} 个 PDF", output, "已合并", $"共 {total} 页 · {new FileInfo(output).Length / 1024.0:F0} KB"));
                outputs.Add(output);
                return Task.FromResult(PluginContext.Result($"已合并 {ctx.Paths.Length} 个 PDF 为 {total} 页。", rows.ToArray(), outputs.ToArray()));
            }
            case "每页拆成单独文件":
            {
                foreach (var path in ctx.Paths)
                {
                    ctx.Cancellation.ThrowIfCancellationRequested();
                    var directory = string.IsNullOrWhiteSpace(outputFolder) ? Path.GetDirectoryName(path) ?? Environment.CurrentDirectory : Path.GetFullPath(outputFolder);
                    Directory.CreateDirectory(directory);
                    var produced = PdfTools.Split(path, directory, "_page");
                    foreach (var (output, count) in produced)
                    {
                        rows.Add(new ResultRow(Path.GetFileName(path), output, "已拆出", $"{count} 页"));
                        outputs.Add(output);
                    }
                    ctx.ReportProgress(outputs.Count, ctx.Paths.Length, Path.GetFileName(path));
                }
                return Task.FromResult(PluginContext.Result($"已拆出 {outputs.Count} 个单页 PDF。", rows.ToArray(), outputs.ToArray()));
            }
            case "提取指定页码":
            {
                if (string.IsNullOrWhiteSpace(pages)) throw new InvalidDataException("请填写要提取的页码，例如 1-3,5。");
                foreach (var path in ctx.Paths)
                {
                    ctx.Cancellation.ThrowIfCancellationRequested();
                    var directory = string.IsNullOrWhiteSpace(outputFolder) ? Path.GetDirectoryName(path) ?? Environment.CurrentDirectory : Path.GetFullPath(outputFolder);
                    Directory.CreateDirectory(directory);
                    var (output, count) = PdfTools.Extract(path, directory, pages);
                    rows.Add(new ResultRow(Path.GetFileName(path), output, "已提取", $"{count} 页（{pages}）"));
                    outputs.Add(output);
                }
                return Task.FromResult(PluginContext.Result($"已提取 {rows.Count} 个 PDF 的指定页面。", rows.ToArray(), outputs.ToArray()));
            }
            default:
                throw new InvalidDataException("处理方式无效。");
        }
    }
}
