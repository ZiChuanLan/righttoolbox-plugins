using System.Text.Json;
using RightToolbox.PluginSdk;

namespace CleanJunk;

public sealed record JunkItem(string Path, bool IsDirectory, long Bytes);

public static class JunkScanner
{
    private const int MaxDepth = 32;

    private static readonly HashSet<string> NoiseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db", "ehthumbs.db", ".DS_Store"
    };

    public static bool IsNoise(string fileName, bool includeSystemNoise, bool includeTempFiles)
    {
        if (includeSystemNoise && NoiseNames.Contains(fileName)) return true;
        if (!includeTempFiles) return false;
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".temp", StringComparison.OrdinalIgnoreCase)) return true;
        return fileName.StartsWith("~$", StringComparison.Ordinal);
    }

    public static List<JunkItem> Scan(
        IReadOnlyList<string> roots,
        bool includeSystemNoise,
        bool includeTempFiles,
        bool includeEmptyFiles,
        bool includeEmptyDirectories,
        CancellationToken token)
    {
        var items = new List<JunkItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(root))
            {
                if (IsNoise(Path.GetFileName(root), includeSystemNoise, includeTempFiles) && seen.Add(root))
                    items.Add(new JunkItem(root, false, new FileInfo(root).Length));
                continue;
            }
            if (Directory.Exists(root)) WalkFiles(root, 0);
        }

        void WalkFiles(string directory, int depth)
        {
            if (depth > MaxDepth) return;
            token.ThrowIfCancellationRequested();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    WalkFiles(entry, depth + 1);
                    continue;
                }

                long length;
                try { length = new FileInfo(entry).Length; }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

                var junk = IsNoise(Path.GetFileName(entry), includeSystemNoise, includeTempFiles) ||
                           (includeEmptyFiles && length == 0);
                if (junk && seen.Add(entry)) items.Add(new JunkItem(entry, false, length));
            }
        }

        if (!includeEmptyDirectories) return items;

        var directories = new List<JunkItem>();
        foreach (var root in roots)
        {
            if (Directory.Exists(root)) CollectEmptyDirectories(root, 0);
        }

        void CollectEmptyDirectories(string directory, int depth)
        {
            if (depth > MaxDepth) return;
            token.ThrowIfCancellationRequested();
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(directory); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }

            foreach (var child in children)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                CollectEmptyDirectories(child, depth + 1);
                bool empty;
                try { empty = !Directory.EnumerateFileSystemEntries(child).Any(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
                if (empty) directories.Add(new JunkItem(child, true, 0));
            }
        }

        // 先深后浅：子目录被删掉后父目录才可能变空。
        directories.Sort((left, right) => right.Path.Length.CompareTo(left.Path.Length));
        items.AddRange(directories);
        return items;
    }
}

public sealed record CleanOutcome(ResultRow[] Rows, int Files, int Directories, long Bytes, int Skipped)
{
    public string Summary => $"已删除 {Files} 个垃圾文件（释放 {JunkFormat.FormatBytes(Bytes)}）、{Directories} 个空文件夹" +
                             (Skipped > 0 ? $"，跳过 {Skipped} 项" : "");
}

public static class JunkCleaner
{
    public static CleanOutcome Delete(IReadOnlyList<JunkItem> items, CancellationToken token)
    {
        var rows = new List<ResultRow>();
        long bytes = 0;
        var files = 0;
        var directories = 0;
        var skipped = 0;

        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Path)) continue;

            try
            {
                if (item.IsDirectory)
                {
                    if (!Directory.Exists(item.Path) || Directory.EnumerateFileSystemEntries(item.Path).Any())
                    {
                        skipped++;
                        rows.Add(new ResultRow(Path.GetFileName(item.Path), item.Path, "已跳过", "不再是空文件夹"));
                        continue;
                    }
                    Directory.Delete(item.Path, false);
                    directories++;
                    rows.Add(new ResultRow(Path.GetFileName(item.Path), item.Path, "已删除", "空文件夹"));
                    continue;
                }

                if (!File.Exists(item.Path))
                {
                    skipped++;
                    continue;
                }

                var length = new FileInfo(item.Path).Length;
                File.Delete(item.Path);
                files++;
                bytes += length;
                rows.Add(new ResultRow(Path.GetFileName(item.Path), item.Path, "已删除", JunkFormat.FormatBytes(length)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                rows.Add(new ResultRow(Path.GetFileName(item.Path), item.Path, "失败", ex.Message));
            }
        }

        return new CleanOutcome(rows.ToArray(), files, directories, bytes, skipped);
    }
}

public static class JunkFormat
{
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}

public static class Program
{
    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx)
    {
        var includeSystemNoise = ctx.BoolParam("systemNoise", true);
        var includeTempFiles = ctx.BoolParam("tempFiles", true);
        var includeEmptyFiles = ctx.BoolParam("emptyFiles", false);
        var includeEmptyDirectories = ctx.BoolParam("emptyDirectories", true);

        if (!includeSystemNoise && !includeTempFiles && !includeEmptyFiles && !includeEmptyDirectories)
            throw new InvalidDataException("至少要勾选一种清理目标。");
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中要清理的文件夹。");

        if (ctx.Phase == "preview")
        {
            var items = JunkScanner.Scan(ctx.Paths, includeSystemNoise, includeTempFiles, includeEmptyFiles, includeEmptyDirectories, ctx.Cancellation);
            var fileCount = items.Count(item => !item.IsDirectory);
            var directoryCount = items.Count(item => item.IsDirectory);
            var bytes = items.Sum(item => item.Bytes);
            var rows = items.Take(500).Select(item => new ResultRow(
                Path.GetFileName(item.Path),
                item.Path,
                item.IsDirectory ? "空文件夹" : "垃圾文件",
                item.IsDirectory ? "" : JunkFormat.FormatBytes(item.Bytes))).ToArray();

            var message = items.Count == 0
                ? "没有发现可以清理的内容。"
                : $"发现 {fileCount} 个垃圾文件（共 {JunkFormat.FormatBytes(bytes)}）与 {directoryCount} 个空文件夹，确认后才会删除。";
            return Task.FromResult(PluginContext.Preview(message, new { kind = "clean-junk", items }, rows));
        }

        if (ctx.Plan is not { } plan || !plan.TryGetProperty("items", out var planned) || planned.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("请先预览并确认要清理的内容。");

        var targets = new List<JunkItem>();
        foreach (var entry in planned.EnumerateArray())
        {
            var path = entry.TryGetProperty("path", out var pathValue) ? pathValue.GetString() ?? "" : "";
            var isDirectory = entry.TryGetProperty("isDirectory", out var dirValue) && dirValue.ValueKind == JsonValueKind.True;
            if (!string.IsNullOrWhiteSpace(path)) targets.Add(new JunkItem(path, isDirectory, 0));
        }

        var outcome = JunkCleaner.Delete(targets, ctx.Cancellation);
        return Task.FromResult(PluginContext.Result(outcome.Summary, outcome.Rows));
    }
}
