using System.Globalization;
using System.IO;
using System.Text;
using RightToolbox.PluginSdk;

namespace GbkFix;

public sealed record Choice(string Label, Encoding Encoding);

public sealed record RenameItem(string Source, string Target, string Reason);

public static class Fixer
{
    private static readonly char[] Forbidden = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];
    private const int MaxDepth = 32;

    static Fixer() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding Strict(int codePage) =>
        Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    public static IReadOnlyList<Choice> Sources(string value) => value switch
    {
        "自动检测" => [new("cp437", Strict(437)), new("latin-1", Encoding.Latin1)],
        "cp437（ZIP 解压最常见）" => [new("cp437", Strict(437))],
        "latin-1 / ISO-8859-1" => [new("latin-1", Encoding.Latin1)],
        _ => throw new InvalidDataException("乱码来源选项无效。")
    };

    public static IReadOnlyList<Choice> Targets(string value) => value switch
    {
        "自动（GBK 优先）" => [new("gbk", Strict(936)), new("gb18030", Strict(54936)), new("utf-8", Strict(65001))],
        "GBK" => [new("gbk", Strict(936))],
        "GB18030" => [new("gb18030", Strict(54936))],
        "UTF-8" => [new("utf-8", Strict(65001))],
        _ => throw new InvalidDataException("还原编码选项无效。")
    };

    public static bool LooksClean(string text)
    {
        if (text.Length == 0 || text.Length > 200) return false;
        foreach (var character in text)
        {
            if (Forbidden.Contains(character)) return false;
            var category = char.GetUnicodeCategory(character);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                return false;
        }
        return true;
    }

    /// <summary>把「按 cp437/latin-1 显示的 GBK 字节」还原成正确的中文名；识别不出时返回 null。</summary>
    public static RenameItem? TryDecode(string fileName, IReadOnlyList<Choice> sources, IReadOnlyList<Choice> targets)
    {
        foreach (var source in sources)
        {
            byte[] raw;
            try { raw = source.Encoding.GetBytes(fileName); }
            catch (EncoderFallbackException) { continue; }

            foreach (var target in targets)
            {
                string decoded;
                try { decoded = target.Encoding.GetString(raw); }
                catch (DecoderFallbackException) { continue; }
                catch (ArgumentException) { continue; }

                if (string.Equals(decoded, fileName, StringComparison.Ordinal)) continue;
                if (!LooksClean(decoded)) continue;
                if (Encoding.UTF8.GetByteCount(decoded) > 240) continue;
                return new RenameItem("", "", $"{source.Label} → {target.Label}");
            }
        }
        return null;
    }

    public static List<string> Collect(IReadOnlyList<string> roots, bool recursive, bool includeFolders, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(root))
            {
                seen.Add(Path.GetFullPath(root));
                continue;
            }
            if (!Directory.Exists(root)) continue;
            var full = Path.GetFullPath(root);
            seen.Add(full);
            if (!recursive) continue;
            Walk(full, 0);
        }

        void Walk(string directory, int depth)
        {
            if (depth > MaxDepth) return;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (includeFolders) seen.Add(entry);
                    Walk(entry, depth + 1);
                }
                else
                {
                    seen.Add(entry);
                }
            }
        }

        // 先深后浅：父目录改名不会让已经排好的子项路径失效。
        var ordered = seen.ToList();
        ordered.Sort((left, right) =>
        {
            var byDepth = right.Count(character => character == Path.DirectorySeparatorChar)
                .CompareTo(left.Count(character => character == Path.DirectorySeparatorChar));
            return byDepth != 0 ? byDepth : right.Length.CompareTo(left.Length);
        });
        return ordered;
    }

    public static List<RenameItem> Plan(IReadOnlyList<string> roots, bool recursive, bool includeFolders, IReadOnlyList<Choice> sources, IReadOnlyList<Choice> targets, CancellationToken token)
    {
        var items = new List<RenameItem>();
        foreach (var path in Collect(roots, recursive, includeFolders, token))
        {
            var name = Path.GetFileName(path);
            if (name.Length == 0) continue;
            var found = TryDecode(name, sources, targets);
            if (found is null) continue;
            var target = Path.Combine(Path.GetDirectoryName(path) ?? "", DecodeName(name, sources, targets));
            items.Add(new RenameItem(path, target, found.Reason));
        }
        return items;
    }

    private static string DecodeName(string fileName, IReadOnlyList<Choice> sources, IReadOnlyList<Choice> targets)
    {
        foreach (var source in sources)
        {
            byte[] raw;
            try { raw = source.Encoding.GetBytes(fileName); }
            catch (EncoderFallbackException) { continue; }

            foreach (var target in targets)
            {
                string decoded;
                try { decoded = target.Encoding.GetString(raw); }
                catch (DecoderFallbackException) { continue; }
                catch (ArgumentException) { continue; }

                if (string.Equals(decoded, fileName, StringComparison.Ordinal)) continue;
                if (!LooksClean(decoded)) continue;
                if (Encoding.UTF8.GetByteCount(decoded) > 240) continue;
                return decoded;
            }
        }
        return fileName;
    }
}

public static class Program
{
    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx)
    {
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中要修复的文件或文件夹。");

        var sources = Fixer.Sources(ctx.Param("sourceEncoding", "自动检测"));
        var targets = Fixer.Targets(ctx.Param("targetEncoding", "自动（GBK 优先）"));
        var recursive = ctx.BoolParam("recursive", true);
        var includeFolders = ctx.BoolParam("includeFolders", true);

        if (ctx.Phase == "preview")
        {
            var items = Fixer.Plan(ctx.Paths, recursive, includeFolders, sources, targets, ctx.Cancellation);
            var rows = items.Take(500).Select(item =>
            {
                var conflict = File.Exists(item.Target) || Directory.Exists(item.Target);
                return new ResultRow(
                    Path.GetFileName(item.Source),
                    Path.GetFileName(item.Target),
                    conflict ? "冲突，将跳过" : "待改名",
                    item.Reason);
            }).ToArray();
            var message = items.Count == 0
                ? "没有检测到乱码文件名，可能本身已经是正确的名字。"
                : $"共 {items.Count} 个名称可修复（确认后才会真正改名）。";
            return Task.FromResult(PluginContext.Preview(message, new { kind = "gbk-fix", items }, rows));
        }

        if (ctx.Plan is not { } plan || !plan.TryGetProperty("items", out var planned) || planned.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidDataException("请先预览并确认要修复的名称。");

        var rows2 = new List<ResultRow>();
        var renamed = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var entry in planned.EnumerateArray())
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var sourcePath = entry.TryGetProperty("source", out var sourceValue) ? sourceValue.GetString() ?? "" : "";
            var targetPath = entry.TryGetProperty("target", out var targetValue) ? targetValue.GetString() ?? "" : "";
            var reason = entry.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() ?? "" : "";
            if (sourcePath.Length == 0 || targetPath.Length == 0) continue;
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase)) continue;

            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                skipped++;
                rows2.Add(new ResultRow(Path.GetFileName(sourcePath), Path.GetFileName(targetPath), "已跳过", "源文件已不存在"));
                continue;
            }
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                skipped++;
                rows2.Add(new ResultRow(Path.GetFileName(sourcePath), Path.GetFileName(targetPath), "已跳过", "目标名称已被占用"));
                continue;
            }

            try
            {
                if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, targetPath);
                else File.Move(sourcePath, targetPath);
                renamed++;
                rows2.Add(new ResultRow(Path.GetFileName(sourcePath), Path.GetFileName(targetPath), "已改名", reason));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                rows2.Add(new ResultRow(Path.GetFileName(sourcePath), Path.GetFileName(targetPath), "失败", ex.Message));
            }
        }

        var summary = $"已改名 {renamed} 项，跳过 {skipped} 项，失败 {failed} 项";
        return Task.FromResult(PluginContext.Result(summary, rows2.ToArray()));
    }
}
