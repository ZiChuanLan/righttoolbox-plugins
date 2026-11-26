using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using RightToolbox.PluginSdk;

namespace DirTree;

public sealed record ManifestRecord(string RelativePath, string Name, string Extension, long SizeBytes, string ModifiedLocal, string Sha256);

public static class TreeBuilder
{
    private static readonly HashSet<string> NoiseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__", ".venv", "venv"
    };

    public static string BuildTree(
        string folderPath,
        string style,
        int maxDepth,
        bool includeFiles,
        bool ignoreNoise,
        out int dirCount,
        out int fileCount,
        CancellationToken cancellation = default)
    {
        var root = new DirectoryInfo(folderPath);
        if (!root.Exists) throw new DirectoryNotFoundException("找不到目标文件夹。");

        var sb = new StringBuilder();
        var dirs = 0;
        var files = 0;
        var isMarkdown = style == "Markdown 缩进列表";
        var isAscii = style == "ASCII 树状图";

        if (isMarkdown)
            sb.AppendLine($"- **{root.Name}/**");
        else
            sb.AppendLine($"{root.Name}/");

        void Walk(DirectoryInfo current, string prefix, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > maxDepth) return;

            var children = new List<FileSystemInfo>();
            try
            {
                foreach (var entry in current.EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var isDir = (entry.Attributes & FileAttributes.Directory) != 0;
                    if (!isDir && !includeFiles) continue;
                    if (ignoreNoise && NoiseNames.Contains(entry.Name)) continue;
                    children.Add(entry);
                }
            }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }

            var ordered = children
                .OrderByDescending(c => (c.Attributes & FileAttributes.Directory) != 0)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            for (var i = 0; i < ordered.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var entry = ordered[i];
                var isLast = i == ordered.Length - 1;
                var isDir = (entry.Attributes & FileAttributes.Directory) != 0;

                if (isDir) dirs++;
                else files++;

                if (isMarkdown)
                {
                    var indent = new string(' ', depth * 2);
                    sb.AppendLine(isDir ? $"{indent}- **{entry.Name}/**" : $"{indent}- `{entry.Name}`");
                    if (isDir && depth < maxDepth)
                        Walk((DirectoryInfo)entry, "", depth + 1);
                }
                else
                {
                    var branch = isLast ? (isAscii ? "\\-- " : "└── ") : (isAscii ? "|-- " : "├── ");
                    var nextPrefix = prefix + (isLast ? "    " : (isAscii ? "|   " : "│   "));
                    sb.AppendLine($"{prefix}{branch}{entry.Name}{(isDir ? "/" : "")}");
                    if (isDir && depth < maxDepth)
                        Walk((DirectoryInfo)entry, nextPrefix, depth + 1);
                }
            }
        }

        Walk(root, "", 1);
        dirCount = dirs;
        fileCount = files;
        return sb.ToString().TrimEnd();
    }

    public static List<ManifestRecord> CollectManifest(
        IEnumerable<string> roots,
        bool includeHash,
        CancellationToken cancellation = default)
    {
        var records = new List<ManifestRecord>();
        foreach (var raw in roots)
        {
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(raw))
            {
                var info = new FileInfo(raw);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                records.Add(MakeRecord(info.Name, info, includeHash));
            }
            else if (Directory.Exists(raw))
            {
                var baseDir = Path.GetFullPath(raw);
                var stack = new Stack<DirectoryInfo>();
                stack.Push(new DirectoryInfo(baseDir));
                while (stack.TryPop(out var dir))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    try
                    {
                        foreach (var sub in dir.EnumerateDirectories().OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(sub);
                        }
                        foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            var rel = Path.GetRelativePath(baseDir, file.FullName);
                            records.Add(MakeRecord(Path.Combine(Path.GetFileName(baseDir), rel), file, includeHash));
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
        }
        return records;
    }

    private static ManifestRecord MakeRecord(string relPath, FileInfo info, bool includeHash)
    {
        var hash = "";
        if (includeHash)
        {
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        return new ManifestRecord(
            relPath,
            info.Name,
            info.Extension,
            info.Length,
            info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            hash);
    }

    public static string FormatCsv(IReadOnlyList<ManifestRecord> records, bool includeHash)
    {
        static string Cell(string v) =>
            v.Contains('"') || v.Contains(',') || v.Contains('\n') || v.Contains('\r')
                ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : v;

        var sb = new StringBuilder();
        sb.AppendLine(includeHash ? "relativePath,name,extension,sizeBytes,modifiedLocal,sha256" : "relativePath,name,extension,sizeBytes,modifiedLocal");
        foreach (var r in records)
        {
            sb.Append(Cell(r.RelativePath)).Append(',')
              .Append(Cell(r.Name)).Append(',')
              .Append(Cell(r.Extension)).Append(',')
              .Append(r.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Cell(r.ModifiedLocal));
            if (includeHash) sb.Append(',').Append(r.Sha256);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string FormatJson(IReadOnlyList<ManifestRecord> records) =>
        JsonSerializer.Serialize(records, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n";
}

internal static class Program
{
    private static Task<int> Main() => PluginHost.RunAsync(async ctx =>
    {
        await Task.CompletedTask;
        return ctx.CommandId switch
        {
            "community.dir-tree.tree" => RunTree(ctx),
            "community.dir-tree.manifest" => RunManifest(ctx),
            _ => throw new InvalidDataException($"未知命令：{ctx.CommandId}")
        };
    });

    private static WorkerEvent RunTree(PluginContext ctx)
    {
        var style = ctx.Param("style", "Unicode 树状图");
        var maxDepth = Math.Clamp(ctx.IntParam("maxDepth", 4), 1, 12);
        var includeFiles = ctx.BoolParam("includeFiles", true);
        var ignoreNoise = ctx.BoolParam("ignoreNoise", true);
        var saveFile = ctx.BoolParam("saveFile", false);

        var combined = new StringBuilder();
        var rows = new List<ResultRow>();
        var outputs = new List<string>();
        var totalDirs = 0;
        var totalFiles = 0;

        for (var i = 0; i < ctx.Paths.Length; i++)
        {
            var dir = ctx.Paths[i];
            var tree = TreeBuilder.BuildTree(dir, style, maxDepth, includeFiles, ignoreNoise, out var d, out var f, ctx.Cancellation);
            totalDirs += d;
            totalFiles += f;
            if (combined.Length > 0) combined.AppendLine().AppendLine();
            combined.AppendLine(style == "Markdown 缩进列表" ? tree : "```text\n" + tree + "\n```");

            var targetLabel = "已复制到剪贴板";
            if (saveFile)
            {
                var outPath = PluginContext.UniqueOutput(dir.TrimEnd('\\', '/'), "_tree", ".md");
                File.WriteAllText(outPath, style == "Markdown 缩进列表" ? tree + "\n" : "```text\n" + tree + "\n```\n", new UTF8Encoding(false));
                outputs.Add(outPath);
                targetLabel = outPath;
            }

            rows.Add(new ResultRow(Path.GetFileName(dir.TrimEnd('\\', '/')), targetLabel, "已生成", $"{d} 目录 · {f} 文件"));
            ctx.ReportProgress(i + 1, ctx.Paths.Length, Path.GetFileName(dir));
        }

        var clipboard = combined.ToString().TrimEnd();
        return PluginContext.Result(
            $"已生成目录树（{totalDirs} 个目录 · {totalFiles} 个文件）并写入剪贴板",
            rows.ToArray(),
            outputs.ToArray(),
            clipboardText: clipboard);
    }

    private static WorkerEvent RunManifest(PluginContext ctx)
    {
        var format = ctx.Param("format", "CSV (UTF-8 BOM)");
        var includeHash = ctx.BoolParam("includeHash", false);
        var outputFolder = ctx.Param("outputFolder", "");

        var records = TreeBuilder.CollectManifest(ctx.Paths, includeHash, ctx.Cancellation);
        var anchor = ctx.Paths[0].TrimEnd('\\', '/');
        var isJson = format == "JSON";
        var outPath = PluginContext.UniqueOutput(anchor, "_manifest", isJson ? ".json" : ".csv", outputFolder);
        var content = isJson ? TreeBuilder.FormatJson(records) : TreeBuilder.FormatCsv(records, includeHash);
        File.WriteAllText(outPath, content, new UTF8Encoding(!isJson));

        var totalBytes = records.Sum(r => r.SizeBytes);
        return PluginContext.Result(
            $"已导出 {records.Count} 条文件清单（{totalBytes:N0} B）",
            [new ResultRow($"{ctx.Paths.Length} 个选区项", outPath, "已导出", $"{records.Count} 个文件")],
            [outPath]);
    }
}
