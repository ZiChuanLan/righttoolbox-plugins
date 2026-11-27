using System.IO;
using System.Text.Json;
using RightToolbox.Images;
using RightToolbox.PluginSdk;

namespace PhotoDedupe;

public sealed record PhotoEntry(string Path, ulong Hash, long Bytes, int Width, int Height)
{
    public long Pixels => (long)Width * Height;
}

public sealed record PhotoGroup(string Keep, List<string> Duplicates);

public static class PhotoScanner
{
    public const int MaxPhotos = 4000;
    private const int ThumbnailWidth = 64;

    public static List<PhotoEntry> Scan(IReadOnlyList<string> roots, bool recursive, CancellationToken token)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(root))
            {
                if (ImageSupport.IsSupported(root) && seen.Add(root)) files.Add(root);
                continue;
            }
            if (!Directory.Exists(root)) continue;
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(root, "*", option))
            {
                if (!ImageSupport.IsSupported(file)) continue;
                if (seen.Add(file)) files.Add(file);
            }
            if (files.Count > MaxPhotos) throw new InvalidDataException($"图片超过 {MaxPhotos} 张，请缩小选区或分批处理。");
        }

        if (files.Count > MaxPhotos) throw new InvalidDataException($"图片超过 {MaxPhotos} 张，请缩小选区或分批处理。");

        var entries = new List<PhotoEntry>(files.Count);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var thumbnail = ImageSupport.LoadThumbnail(file, ThumbnailWidth);
                entries.Add(new PhotoEntry(file, ImageSupport.DifferenceHash(thumbnail), new FileInfo(file).Length, thumbnail.PixelWidth, thumbnail.PixelHeight));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException)
            {
                // 无法解码的文件直接跳过，不让一张坏图毁掉整批检测。
            }
        }
        return entries;
    }

    public static List<List<PhotoEntry>> Group(IReadOnlyList<PhotoEntry> entries, int threshold)
    {
        threshold = Math.Clamp(threshold, 0, 20);
        var remaining = new List<PhotoEntry>(entries);
        var groups = new List<List<PhotoEntry>>();
        while (remaining.Count > 0)
        {
            var seed = remaining[0];
            remaining.RemoveAt(0);
            var group = new List<PhotoEntry> { seed };
            for (var index = remaining.Count - 1; index >= 0; index--)
            {
                if (ImageSupport.HammingDistance(seed.Hash, remaining[index].Hash) > threshold) continue;
                group.Add(remaining[index]);
                remaining.RemoveAt(index);
            }
            if (group.Count > 1) groups.Add(group);
        }
        return groups;
    }

    /// <summary>Keeps the highest-resolution copy (largest bytes as tie-breaker) and marks the rest as duplicates.</summary>
    public static List<PhotoGroup> Plan(IReadOnlyList<List<PhotoEntry>> groups) =>
        groups.Select(group =>
        {
            var ordered = group
                .OrderByDescending(entry => entry.Pixels)
                .ThenByDescending(entry => entry.Bytes)
                .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new PhotoGroup(ordered[0].Path, ordered.Skip(1).Select(entry => entry.Path).ToList());
        }).ToList();
}

public static class DedupeExecutor
{
    public static (ResultRow[] Rows, int Moved, int Skipped) Move(IReadOnlyList<PhotoGroup> plan, CancellationToken token)
    {
        var rows = new List<ResultRow>();
        var moved = 0;
        var skipped = 0;

        foreach (var group in plan)
        {
            token.ThrowIfCancellationRequested();
            var directory = Path.Combine(Path.GetDirectoryName(group.Keep) ?? Environment.CurrentDirectory, "_duplicates");
            Directory.CreateDirectory(directory);

            foreach (var duplicate in group.Duplicates)
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(duplicate))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    var target = UniqueTarget(directory, Path.GetFileName(duplicate));
                    File.Move(duplicate, target);
                    moved++;
                    rows.Add(new ResultRow(Path.GetFileName(duplicate), target, "已移出", $"保留 {Path.GetFileName(group.Keep)}"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    rows.Add(new ResultRow(Path.GetFileName(duplicate), duplicate, "失败", ex.Message));
                }
            }
        }

        return (rows.ToArray(), moved, skipped);
    }

    public static string UniqueTarget(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 0; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, stem + (index == 0 ? "" : $" ({index})") + extension);
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("_duplicates 目录里同名文件过多，请先清理。");
    }
}

public static class Program
{
    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx) =>
        Task.FromResult(ImageSupport.RunSta(() => Handle(ctx)));

    private static WorkerEvent Handle(PluginContext ctx)
    {
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中文件夹或图片。");

        var threshold = ctx.IntParam("threshold", 8);
        var recursive = ctx.BoolParam("includeSubfolders", true);
        var action = ctx.Param("action", "只分组检测");

        if (ctx.Phase == "preview")
        {
            var entries = PhotoScanner.Scan(ctx.Paths, recursive, ctx.Cancellation);
            if (entries.Count == 0) throw new InvalidDataException("选区内没有可解析的图片。");
            var plan = PhotoScanner.Plan(PhotoScanner.Group(entries, threshold));
            var duplicateCount = plan.Sum(group => group.Duplicates.Count);
            var rows = new List<ResultRow>();
            for (var index = 0; index < plan.Count; index++)
            {
                var group = plan[index];
                rows.Add(new ResultRow(
                    $"第 {index + 1} 组（{group.Duplicates.Count + 1} 张）",
                    group.Keep,
                    "保留这张",
                    $"{ImageSupport.FormatBytes(new FileInfo(group.Keep).Length)} · 其余 {group.Duplicates.Count} 张{(action == "只分组检测" ? "仅供参考" : "将移入 _duplicates")}"));
            }
            var message = plan.Count == 0
                ? $"检查了 {entries.Count} 张图片，没有发现相似内容。"
                : $"检查了 {entries.Count} 张图片，发现 {plan.Count} 组相似照片，共 {duplicateCount} 张重复。";
            return PluginContext.Preview(message, new { kind = "photo-dedupe", action, groups = plan }, rows.ToArray());
        }

        if (ctx.Plan is not { } planElement || !planElement.TryGetProperty("groups", out var groupsElement) || groupsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("请先预览并确认分组结果。");

        var planned = new List<PhotoGroup>();
        foreach (var groupElement in groupsElement.EnumerateArray())
        {
            var keep = groupElement.TryGetProperty("keep", out var keepValue) ? keepValue.GetString() ?? "" : "";
            var duplicates = new List<string>();
            if (groupElement.TryGetProperty("duplicates", out var duplicatesElement) && duplicatesElement.ValueKind == JsonValueKind.Array)
                foreach (var item in duplicatesElement.EnumerateArray())
                    if (item.GetString() is { Length: > 0 } duplicate) duplicates.Add(duplicate);
            if (keep.Length > 0 && duplicates.Count > 0) planned.Add(new PhotoGroup(keep, duplicates));
        }

        if (planned.Count == 0) return PluginContext.Result("没有需要处理的重复照片。");

        if (action == "只分组检测")
        {
            var rows = planned.Select(group => new ResultRow(
                Path.GetFileName(group.Keep),
                group.Keep,
                "保留",
                $"{group.Duplicates.Count} 张重复（未移动）")).ToArray();
            return PluginContext.Result($"检测到 {planned.Count} 组相似照片，未移动任何文件。", rows);
        }

        var (rowsMoved, moved, skipped) = DedupeExecutor.Move(planned, ctx.Cancellation);
        var tail = skipped > 0 ? $"，跳过 {skipped} 张" : "";
        return PluginContext.Result($"已把 {moved} 张重复照片移到 _duplicates 文件夹{tail}。", rowsMoved);
    }
}
