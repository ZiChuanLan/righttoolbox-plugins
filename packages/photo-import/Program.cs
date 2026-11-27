using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using RightToolbox.Images;
using RightToolbox.PluginSdk;

namespace PhotoImport;

public sealed record ImportPlanItem(string Source, string Target, bool Reencode, DateTime TakenAt, string Action);

public static class Importer
{
    private static readonly HashSet<string> ConvertibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".webp", ".tif", ".tiff", ".gif", ".bmp"
    };

    public static DateTime ResolveTakenAt(string path) => ImageSupport.ReadTakenAt(path) ?? File.GetLastWriteTime(path);

    public static bool NeedsConversion(string path) => ConvertibleExtensions.Contains(Path.GetExtension(path));

    public static string ArchiveDirectory(DateTime taken, string archive) => archive switch
    {
        "按年/月归档 (2026/01)" => Path.Combine(taken.ToString("yyyy", CultureInfo.InvariantCulture), taken.ToString("MM", CultureInfo.InvariantCulture)),
        "按年归档 (2026)" => taken.ToString("yyyy", CultureInfo.InvariantCulture),
        "不归档" => "",
        _ => throw new InvalidDataException("归档方式无效。")
    };

    public static string BuildFileName(string source, DateTime taken, string naming, bool toJpeg)
    {
        var original = Path.GetFileNameWithoutExtension(source);
        var stem = naming switch
        {
            "按拍摄日期命名 (20260101_143000)" => taken.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture),
            "日期前缀 + 原名" => taken.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + original,
            "保留原名" => original,
            _ => throw new InvalidDataException("命名方式无效。")
        };
        var extension = toJpeg ? ".jpg" : Path.GetExtension(source).ToLowerInvariant();
        return stem + extension;
    }

    public static List<ImportPlanItem> Plan(
        IReadOnlyList<string> roots,
        bool recursive,
        string naming,
        string archive,
        bool toJpeg,
        int shrinkKb,
        string outputRoot,
        CancellationToken token)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(root))
            {
                if (ImageSupport.IsSupported(root)) files.Add(root);
                continue;
            }
            if (!Directory.Exists(root)) continue;
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            files.AddRange(Directory.EnumerateFiles(root, "*", option).Where(ImageSupport.IsSupported));
        }
        if (files.Count == 0) throw new InvalidDataException("选区内没有可处理的图片。");
        if (files.Count > 2000) throw new InvalidDataException("单次最多处理 2000 张图片，请分批导入。");

        var items = new List<ImportPlanItem>(files.Count);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var taken = ResolveTakenAt(file);
            var convert = toJpeg && NeedsConversion(file);
            var targetName = BuildFileName(file, taken, naming, convert);
            var baseDirectory = string.IsNullOrWhiteSpace(outputRoot) ? Path.GetDirectoryName(file) ?? Environment.CurrentDirectory : Path.GetFullPath(outputRoot);
            var archiveDirectory = ArchiveDirectory(taken, archive);
            var target = Path.Combine(baseDirectory, archiveDirectory, targetName);

            var reencode = convert || shrinkKb > 0;
            var parts = new List<string>();
            if (convert) parts.Add("转 JPG");
            if (shrinkKb > 0) parts.Add($"压到 {shrinkKb} KB");
            if (!string.Equals(Path.GetFileName(file), targetName, StringComparison.Ordinal)) parts.Add("改名");
            if (archiveDirectory.Length > 0) parts.Add("归档");
            if (parts.Count == 0) parts.Add("保持原名");

            items.Add(new ImportPlanItem(file, target, reencode, taken, string.Join(" · ", parts)));
        }
        return items;
    }

    public static string UniqueTarget(string target)
    {
        if (!File.Exists(target)) return target;
        var directory = Path.GetDirectoryName(target) ?? Environment.CurrentDirectory;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("目标目录里同名文件过多，请先清理。");
    }
}

public static class ImportExecutor
{
    public static (ResultRow[] Rows, int Moved, int Converted) Run(IReadOnlyList<ImportPlanItem> items, int shrinkKb, CancellationToken token)
    {
        var rows = new List<ResultRow>();
        var moved = 0;
        var converted = 0;

        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(item.Source))
                {
                    rows.Add(new ResultRow(Path.GetFileName(item.Source), item.Target, "已跳过", "源文件已不存在"));
                    continue;
                }

                var target = Importer.UniqueTarget(item.Target);
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                if (string.Equals(Path.GetFullPath(item.Source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(new ResultRow(Path.GetFileName(item.Source), target, "无需改动", item.Action));
                    continue;
                }

                if (!item.Reencode)
                {
                    File.Move(item.Source, target);
                    moved++;
                    rows.Add(new ResultRow(Path.GetFileName(item.Source), target, "已移动", $"{item.TakenAt:yyyy-MM-dd HH:mm} · {item.Action}"));
                    continue;
                }

                var frame = ImageSupport.Flatten(ImageSupport.Load(item.Source));
                var bytes = shrinkKb > 0 ? Squeeze(frame, shrinkKb * 1024L) : ImageSupport.EncodeJpeg(frame, 92);
                ImageSupport.WriteBytes(target, bytes);
                File.Delete(item.Source);
                converted++;
                rows.Add(new ResultRow(
                    Path.GetFileName(item.Source),
                    target,
                    "已转换",
                    $"{item.TakenAt:yyyy-MM-dd HH:mm} · {item.Action} · {ImageSupport.FormatBytes(bytes.LongLength)}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException)
            {
                rows.Add(new ResultRow(Path.GetFileName(item.Source), item.Target, "失败", ex.Message));
            }
        }

        return (rows.ToArray(), moved, converted);
    }

    public static byte[] Squeeze(BitmapSource source, long targetBytes)
    {
        foreach (var scale in new[] { 1.0, 0.8, 0.6, 0.45 })
        {
            var frame = ImageSupport.Resize(
                source,
                Math.Max(1, (int)Math.Round(source.PixelWidth * scale)),
                Math.Max(1, (int)Math.Round(source.PixelHeight * scale)));
            foreach (var quality in new[] { 90, 82, 74, 66, 58, 50, 42, 35 })
            {
                var bytes = ImageSupport.EncodeJpeg(frame, quality);
                if (bytes.LongLength <= targetBytes) return bytes;
            }
        }
        return ImageSupport.EncodeJpeg(source, 30);
    }
}

public static class Program
{
    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx) =>
        Task.FromResult(ImageSupport.RunSta(() => Handle(ctx)));

    public static int ParseShrinkKb(string value) => value switch
    {
        "不压缩" => 0,
        "≤200 KB" => 200,
        "≤500 KB" => 500,
        "≤1 MB" => 1024,
        "≤2 MB" => 2048,
        _ => throw new InvalidDataException("压缩选项无效。")
    };

    private static WorkerEvent Handle(PluginContext ctx)
    {
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中要整理的照片或文件夹。");

        var naming = ctx.Param("naming", "按拍摄日期命名 (20260101_143000)");
        var archive = ctx.Param("archive", "按年/月归档 (2026/01)");
        var toJpeg = ctx.BoolParam("toJpeg", true);
        var shrinkKb = ParseShrinkKb(ctx.Param("shrink", "不压缩"));
        var outputRoot = ctx.Param("outputRoot", "");
        var recursive = ctx.BoolParam("includeSubfolders", true);

        if (ctx.Phase == "preview")
        {
            var items = Importer.Plan(ctx.Paths, recursive, naming, archive, toJpeg, shrinkKb, outputRoot, ctx.Cancellation);
            var rows = items.Take(500).Select(item => new ResultRow(
                Path.GetFileName(item.Source),
                item.Target,
                item.Reencode ? "转换并移动" : "移动",
                $"{item.TakenAt:yyyy-MM-dd HH:mm} · {item.Action}")).ToArray();
            var convertCount = items.Count(item => item.Reencode);
            var message = $"将处理 {items.Count} 张照片：{convertCount} 张需要转换/压缩，其余为改名归档；确认后原件会被移动到新位置。";
            return PluginContext.Preview(message, new { kind = "photo-import", items }, rows);
        }

        if (ctx.Plan is not { } plan || !plan.TryGetProperty("items", out var planned) || planned.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("请先预览并确认整理方案。");

        var targets = new List<ImportPlanItem>();
        foreach (var entry in planned.EnumerateArray())
        {
            var source = entry.TryGetProperty("source", out var sourceValue) ? sourceValue.GetString() ?? "" : "";
            var target = entry.TryGetProperty("target", out var targetValue) ? targetValue.GetString() ?? "" : "";
            var reencode = entry.TryGetProperty("reencode", out var reencodeValue) && reencodeValue.ValueKind == JsonValueKind.True;
            var action = entry.TryGetProperty("action", out var actionValue) ? actionValue.GetString() ?? "" : "";
            var taken = entry.TryGetProperty("takenAt", out var takenValue) && takenValue.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(takenValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : File.GetLastWriteTime(source);
            if (source.Length == 0 || target.Length == 0) continue;
            targets.Add(new ImportPlanItem(source, target, reencode, taken, action));
        }

        var (resultRows, moved, converted) = ImportExecutor.Run(targets, shrinkKb, ctx.Cancellation);
        var failed = resultRows.Count(row => row.Status == "失败");
        var tail = failed > 0 ? $"，{failed} 张失败" : "";
        return PluginContext.Result($"已整理 {moved + converted} 张照片（{converted} 张转换/压缩，{moved} 张移动）{tail}。", resultRows);
    }
}
