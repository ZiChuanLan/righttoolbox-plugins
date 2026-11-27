using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RightToolbox.PluginSdk;

namespace TextReplace;

public sealed record Loaded(string Path, string Text, Encoding Encoding, bool HasBom);

public static class Replacer
{
    public static readonly string[] EncodingChoices = ["自动检测", "UTF-8", "GBK"];

    static Replacer() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding Strict(int codePage) =>
        Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    /// <summary>Binary or undecodable files return null so they are silently skipped.</summary>
    public static Loaded? Load(string path, string preference)
    {
        byte[] raw;
        try { raw = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        if (raw.Length == 0 || Array.IndexOf(raw, (byte)0) >= 0) return null;

        var hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        var body = hasBom ? raw[3..] : raw;

        var candidates = preference switch
        {
            "UTF-8" => new[] { 65001 },
            "GBK" => new[] { 936 },
            "自动检测" => new[] { 65001, 936, 54936 },
            _ => throw new InvalidDataException("文件编码选项无效。")
        };

        foreach (var codePage in candidates)
        {
            try
            {
                var encoding = Strict(codePage);
                return new Loaded(path, encoding.GetString(body), encoding, hasBom);
            }
            catch (DecoderFallbackException) { }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static byte[] Save(Loaded file, string text)
    {
        var body = file.Encoding.GetBytes(text);
        if (!file.HasBom) return body;
        return [0xEF, 0xBB, 0xBF, .. body];
    }

    public static Regex Build(string pattern, bool useRegex, bool ignoreCase)
    {
        if (pattern.Length == 0) throw new InvalidDataException("请填写要查找的内容。");
        var options = RegexOptions.None;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        try
        {
            return new Regex(useRegex ? pattern : Regex.Escape(pattern), options, TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("正则表达式无效：" + ex.Message);
        }
    }

    public static List<string> Collect(IReadOnlyList<string> roots, bool recursive, CancellationToken token)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(root))
            {
                if (seen.Add(Path.GetFullPath(root))) files.Add(Path.GetFullPath(root));
                continue;
            }
            if (!Directory.Exists(root)) continue;
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(root, "*", option))
                if (seen.Add(file)) files.Add(file);
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }
}

public static class Program
{
    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx)
    {
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中要处理的文件或文件夹。");

        var pattern = ctx.Param("pattern", "");
        var replacement = ctx.Param("replacement", "");
        var useRegex = ctx.BoolParam("useRegex", false);
        var ignoreCase = ctx.BoolParam("ignoreCase", false);
        var backup = ctx.BoolParam("backup", true);
        var recursive = ctx.BoolParam("includeSubfolders", true);
        var preference = ctx.Param("encoding", "自动检测");
        if (!Replacer.EncodingChoices.Contains(preference)) throw new InvalidDataException("文件编码选项无效。");

        var regex = Replacer.Build(pattern, useRegex, ignoreCase);
        var files = Replacer.Collect(ctx.Paths, recursive, ctx.Cancellation);
        if (files.Count == 0) throw new InvalidDataException("没有找到可处理的文件。");

        if (ctx.Phase == "preview")
        {
            var items = new List<object>();
            var rows = new List<ResultRow>();
            var totalHits = 0;
            for (var index = 0; index < files.Count; index++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                var path = files[index];
                var loaded = Replacer.Load(path, preference);
                ctx.ReportProgress(index + 1, files.Count, Path.GetFileName(path));
                if (loaded is null) continue;

                var hits = regex.Matches(loaded.Text).Count;
                if (hits == 0) continue;
                totalHits += hits;
                items.Add(new { path, hits, encoding = loaded.Encoding.WebName });
                rows.Add(new ResultRow(Path.GetFileName(path), "", $"{hits} 处匹配", loaded.Encoding.WebName));
            }

            var message = items.Count == 0
                ? "没有匹配到任何内容。"
                : $"共 {items.Count} 个文件、{totalHits} 处匹配（确认后才会写回{(backup ? "，并保留 .bak" : "")}）。";
            return Task.FromResult(PluginContext.Preview(message, new { kind = "text-replace", items }, rows.ToArray()));
        }

        if (ctx.Plan is not { } plan || !plan.TryGetProperty("items", out var planned) || planned.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidDataException("请先预览并确认要替换的文件。");

        var rows2 = new List<ResultRow>();
        var changedFiles = 0;
        var changedHits = 0;
        var skipped = 0;

        foreach (var entry in planned.EnumerateArray())
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = entry.TryGetProperty("path", out var pathValue) ? pathValue.GetString() ?? "" : "";
            if (path.Length == 0 || !File.Exists(path))
            {
                skipped++;
                continue;
            }

            var loaded = Replacer.Load(path, preference);
            if (loaded is null)
            {
                skipped++;
                rows2.Add(new ResultRow(Path.GetFileName(path), "", "已跳过", "文件已不可读或不再是文本"));
                continue;
            }

            string replaced;
            int count;
            try
            {
                count = regex.Matches(loaded.Text).Count;
                replaced = regex.Replace(loaded.Text, replacement);
            }
            catch (RegexMatchTimeoutException)
            {
                skipped++;
                rows2.Add(new ResultRow(Path.GetFileName(path), "", "失败", "正则匹配超时"));
                continue;
            }

            if (count == 0 || string.Equals(replaced, loaded.Text, StringComparison.Ordinal))
            {
                skipped++;
                rows2.Add(new ResultRow(Path.GetFileName(path), "", "无需改动", "内容已是最新"));
                continue;
            }

            try
            {
                var bytes = Replacer.Save(loaded, replaced);
                if (backup)
                {
                    var backupPath = path + ".bak";
                    if (!File.Exists(backupPath)) File.WriteAllBytes(backupPath, File.ReadAllBytes(path));
                }
                File.WriteAllBytes(path, bytes);
                changedFiles++;
                changedHits += count;
                rows2.Add(new ResultRow(Path.GetFileName(path), path, "已替换", $"{count} 处（{loaded.Encoding.WebName}）"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EncoderFallbackException)
            {
                skipped++;
                rows2.Add(new ResultRow(Path.GetFileName(path), "", "失败", ex.Message));
            }
        }

        var summary = $"已修改 {changedFiles} 个文件、{changedHits} 处匹配" + (skipped > 0 ? $"，跳过 {skipped} 个" : "");
        return Task.FromResult(PluginContext.Result(summary, rows2.ToArray()));
    }
}
