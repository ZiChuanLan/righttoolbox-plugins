using System.Text;
using RightToolbox.PluginSdk;

namespace TextOps;

public static class TextProcessor
{
    public const long MaxTextFileBytes = 50L * 1024 * 1024;

    static TextProcessor()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static (string Content, string EncodingLabel, string LineEnding) ReadTextAuto(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("找不到文件。", path);
        if (info.Length > MaxTextFileBytes) throw new IOException($"文件超过 50 MB 文本处理上限：{info.Name}");

        var bytes = File.ReadAllBytes(path);
        var (encoding, label, offset) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, offset, bytes.Length - offset);
        var lineEnding = DetectLineEnding(text);
        return (text, label, lineEnding);
    }

    public static (Encoding Encoding, string Label, int BomBytes) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true), "UTF-8 BOM", 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, "UTF-16 LE", 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, "UTF-16 BE", 2);

        try
        {
            var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            _ = strictUtf8.GetString(bytes);
            return (strictUtf8, "UTF-8", 0);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(936), "GBK", 0);
        }
    }

    public static string DetectLineEnding(string text)
    {
        var hasCrlf = text.Contains("\r\n", StringComparison.Ordinal);
        var stripped = text.Replace("\r\n", "", StringComparison.Ordinal);
        var hasLf = stripped.Contains('\n');
        return (hasCrlf, hasLf) switch
        {
            (true, true) => "混合 (CRLF/LF)",
            (true, false) => "CRLF",
            (false, true) => "LF",
            _ => "单行"
        };
    }

    public static (int Lines, int NonEmptyLines, int Chars, int Words, long Bytes, string EncodingLabel, string LineEnding) Analyze(string path)
    {
        var (text, encLabel, lineEnding) = ReadTextAuto(path);
        var bytes = new FileInfo(path).Length;
        if (text.Length == 0) return (0, 0, 0, 0, bytes, encLabel, "空文件");

        var lines = SplitLines(text);
        var nonEmpty = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        var chars = text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Length;
        var words = CountTokens(text);
        return (lines.Length, nonEmpty, chars, words, bytes, encLabel, lineEnding);
    }

    public static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    public static int CountTokens(string text)
    {
        var count = 0;
        var inAsciiWord = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inAsciiWord = false;
                continue;
            }
            if (ch >= 0x4E00 && ch <= 0x9FFF)
            {
                inAsciiWord = false;
                count++;
                continue;
            }
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                if (!inAsciiWord)
                {
                    count++;
                    inAsciiWord = true;
                }
            }
            else
            {
                inAsciiWord = false;
            }
        }
        return count;
    }

    public static string CleanupText(string text, bool dedupe, string sortMode, bool trimLines, bool removeBlank, out int beforeCount, out int afterCount)
    {
        var lines = SplitLines(text).ToList();
        beforeCount = lines.Count;
        if (trimLines) lines = lines.Select(l => l.Trim()).ToList();
        if (removeBlank) lines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (dedupe)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            lines = lines.Where(seen.Add).ToList();
        }
        lines = sortMode switch
        {
            "升序 (A→Z)" => lines.OrderBy(l => l, StringComparer.Ordinal).ToList(),
            "降序 (Z→A)" => lines.OrderByDescending(l => l, StringComparer.Ordinal).ToList(),
            "按长度升序" => lines.OrderBy(l => l.Length).ThenBy(l => l, StringComparer.Ordinal).ToList(),
            _ => lines
        };
        afterCount = lines.Count;
        return string.Join(Environment.NewLine, lines);
    }
}

internal static class Program
{
    private static Task<int> Main() => PluginHost.RunAsync(async ctx =>
    {
        await Task.CompletedTask;
        return ctx.CommandId switch
        {
            "community.text-ops.stats" => RunStats(ctx),
            "community.text-ops.cleanup" => RunCleanup(ctx),
            "community.text-ops.merge" => RunMerge(ctx),
            _ => throw new InvalidDataException($"未知命令：{ctx.CommandId}")
        };
    });

    private static WorkerEvent RunStats(PluginContext ctx)
    {
        var rows = new List<ResultRow>(ctx.Paths.Length + 1);
        long totalLines = 0, totalChars = 0, totalWords = 0;

        for (var i = 0; i < ctx.Paths.Length; i++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = ctx.Paths[i];
            var s = TextProcessor.Analyze(path);
            totalLines += s.Lines;
            totalChars += s.Chars;
            totalWords += s.Words;
            rows.Add(new ResultRow(
                Path.GetFileName(path),
                $"{s.Lines:N0} 行 ({s.NonEmptyLines:N0} 非空) · {s.Chars:N0} 字符 · {s.Words:N0} 词",
                s.EncodingLabel,
                $"{s.LineEnding} · {s.Bytes:N0} B"));
            ctx.ReportProgress(i + 1, ctx.Paths.Length, Path.GetFileName(path));
        }

        if (ctx.Paths.Length > 1)
        {
            rows.Add(new ResultRow("合计", $"{totalLines:N0} 行 · {totalChars:N0} 字符 · {totalWords:N0} 词", "汇总", $"共 {ctx.Paths.Length} 个文件"));
        }

        return PluginContext.Result($"已统计 {ctx.Paths.Length} 个文件（共 {totalLines:N0} 行 / {totalWords:N0} 词）", rows.ToArray());
    }

    private static WorkerEvent RunCleanup(PluginContext ctx)
    {
        var dedupe = ctx.BoolParam("dedupe", true);
        var sortMode = ctx.Param("sortMode", "保持原序");
        var trimLines = ctx.BoolParam("trimLines", false);
        var removeBlank = ctx.BoolParam("removeBlank", true);
        var outputFolder = ctx.Param("outputFolder", "");

        var rows = new List<ResultRow>(ctx.Paths.Length);
        var outputs = new List<string>(ctx.Paths.Length);

        for (var i = 0; i < ctx.Paths.Length; i++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = ctx.Paths[i];
            var (raw, _, _) = TextProcessor.ReadTextAuto(path);
            var cleaned = TextProcessor.CleanupText(raw, dedupe, sortMode, trimLines, removeBlank, out var before, out var after);
            var outPath = PluginContext.UniqueOutput(path, "_clean", Path.GetExtension(path), outputFolder);
            File.WriteAllText(outPath, cleaned, new UTF8Encoding(false));
            outputs.Add(outPath);
            rows.Add(new ResultRow(Path.GetFileName(path), outPath, "已完成", $"{before} → {after} 行"));
            ctx.ReportProgress(i + 1, ctx.Paths.Length, Path.GetFileName(path));
        }

        return PluginContext.Result($"已清理 {outputs.Count} 个文本文件（原件已保留）", rows.ToArray(), outputs.ToArray());
    }

    private static WorkerEvent RunMerge(PluginContext ctx)
    {
        if (ctx.Paths.Length < 2) throw new InvalidDataException("合并至少需要 2 个文件。");
        var separator = ctx.Param("separator", "文件名标题头");
        var customName = ctx.Param("fileName", "").Trim();
        var outputFolder = ctx.Param("outputFolder", "");

        var first = ctx.Paths[0];
        var ext = Path.GetExtension(first);
        if (string.IsNullOrEmpty(ext)) ext = ".txt";

        string outPath;
        if (!string.IsNullOrEmpty(customName))
        {
            var dir = string.IsNullOrWhiteSpace(outputFolder) ? Path.GetDirectoryName(first)! : Path.GetFullPath(outputFolder);
            var stem = Path.GetFileNameWithoutExtension(customName);
            var targetExt = Path.HasExtension(customName) ? Path.GetExtension(customName) : ext;
            outPath = PluginContext.UniqueOutput(Path.Combine(dir, stem + targetExt), "", targetExt, dir);
        }
        else
        {
            outPath = PluginContext.UniqueOutput(first, "_merged", ext, outputFolder);
        }

        var sb = new StringBuilder();
        var rows = new List<ResultRow>(ctx.Paths.Length);
        for (var i = 0; i < ctx.Paths.Length; i++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = ctx.Paths[i];
            var (content, enc, _) = TextProcessor.ReadTextAuto(path);
            if (i > 0)
            {
                if (separator == "文件名标题头") sb.AppendLine().AppendLine();
                else if (separator == "空行分隔") sb.AppendLine().AppendLine();
            }
            if (separator == "文件名标题头")
            {
                sb.AppendLine($"===== {Path.GetFileName(path)} =====");
            }
            sb.Append(content);
            rows.Add(new ResultRow(Path.GetFileName(path), outPath, "已合并", enc));
            ctx.ReportProgress(i + 1, ctx.Paths.Length, Path.GetFileName(path));
        }

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        return PluginContext.Result($"已将 {ctx.Paths.Length} 个文件合并为 {Path.GetFileName(outPath)}", rows.ToArray(), [outPath]);
    }
}
