using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RightToolbox.PluginSdk;

namespace JsonFormat;

public sealed record Outcome(bool Ok, bool Changed, string Error, string Detail, byte[] Output, byte[] Original);

public static class Formatter
{
    public const string Pretty2 = "美化（2 空格）";
    public const string Pretty4 = "美化（4 空格）";
    public const string Minify = "压缩为单行";
    public const string Sort = "按字母排序键后美化";
    public const string Validate = "仅校验，不写回";

    private static readonly JsonSerializerOptions LooseParse = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static bool IsMode(string value) => value is Pretty2 or Pretty4 or Minify or Sort or Validate;

    public static JsonNode? SortKeys(JsonNode? node) => node switch
    {
        JsonObject obj => BuildSorted(obj),
        JsonArray array => BuildSortedArray(array),
        _ => node?.DeepClone()
    };

    private static JsonObject BuildSorted(JsonObject source)
    {
        var sorted = new JsonObject();
        foreach (var pair in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            sorted[pair.Key] = SortKeys(pair.Value);
        return sorted;
    }

    private static JsonArray BuildSortedArray(JsonArray source)
    {
        var sorted = new JsonArray();
        foreach (var item in source) sorted.Add(SortKeys(item));
        return sorted;
    }

    public static Outcome Process(string path, string mode)
    {
        if (!File.Exists(path)) return new Outcome(false, false, "文件已不存在", "", [], []);
        var original = File.ReadAllBytes(path);
        if (original.Length == 0) return new Outcome(false, false, "文件为空", "", [], original);

        var hasBom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        var body = hasBom ? original[3..] : original;
        var text = Encoding.UTF8.GetString(body);
        if (string.IsNullOrWhiteSpace(text)) return new Outcome(false, false, "文件为空", "", [], original);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            return new Outcome(false, false, "JSON 语法错误：" + ex.Message, "", [], original);
        }

        if (mode == Validate) return new Outcome(true, false, "", "语法有效", [], original);

        var indentSize = mode == Pretty4 ? 4 : 2;
        var options = new JsonSerializerOptions
        {
            WriteIndented = mode != Minify,
            IndentSize = indentSize,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = 256
        };

        var source = mode == Sort ? SortKeys(node) : node;
        string output;
        try
        {
            output = source is null ? "null" : source.ToJsonString(options);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return new Outcome(false, false, "无法序列化：" + ex.Message, "", [], original);
        }

        // 保留原有换行风格与末尾换行
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (newline == "\r\n") output = output.Replace("\n", "\r\n", StringComparison.Ordinal);
        output += newline;

        var bytes = new UTF8Encoding(false).GetBytes(output);
        if (hasBom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        if (bytes.AsSpan().SequenceEqual(original)) return new Outcome(true, false, "", "已是目标格式", bytes, original);
        return new Outcome(true, true, "", mode, bytes, original);
    }
}

public static class Program
{
    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx)
    {
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中 JSON 文件。");
        var mode = ctx.Param("mode", Formatter.Pretty2);
        if (!Formatter.IsMode(mode)) throw new InvalidDataException("处理方式无效。");
        var backup = ctx.BoolParam("backup", true);

        if (ctx.Phase == "preview")
        {
            var outcomes = ctx.Paths.Select(path => (Path: path, Outcome: Formatter.Process(path, mode))).ToArray();
            var rows = outcomes.Select(pair => new ResultRow(
                Path.GetFileName(pair.Path),
                "",
                !pair.Outcome.Ok ? "无法处理" : pair.Outcome.Changed ? "待格式化" : "无需改动",
                pair.Outcome.Ok ? pair.Outcome.Detail : pair.Outcome.Error)).ToArray();
            var usable = outcomes.Count(pair => pair.Outcome.Ok && pair.Outcome.Changed);
            var suffix = mode == Formatter.Validate ? "（仅校验，不写回）" : backup ? "（确认后写回，保留 .bak）" : "（确认后写回，不保留备份）";
            var message = usable == 0
                ? $"没有需要写回的文件{suffix}，共检查 {outcomes.Length} 个。"
                : $"可处理 {usable} 个文件{suffix}";
            var plan = new { kind = "json-format", items = outcomes.Select(pair => new { path = pair.Path, ok = pair.Outcome.Ok }).ToArray() };
            return Task.FromResult(PluginContext.Preview(message, plan, rows));
        }

        if (ctx.Plan is not { } planElement || !planElement.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("请先预览并确认要格式化的文件。");

        var rows2 = new List<ResultRow>();
        var outputs = new List<string>();
        var changed = 0;
        var skipped = 0;

        foreach (var item in itemsElement.EnumerateArray())
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = item.TryGetProperty("path", out var pathValue) ? pathValue.GetString() ?? "" : "";
            var plannedOk = item.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
            if (path.Length == 0) continue;
            if (!plannedOk)
            {
                skipped++;
                rows2.Add(new ResultRow(Path.GetFileName(path), "", "已跳过", "预览时不可处理"));
                continue;
            }

            var outcome = Formatter.Process(path, mode);
            if (!outcome.Ok)
            {
                skipped++;
                rows2.Add(new ResultRow(Path.GetFileName(path), "", "失败", outcome.Error));
                continue;
            }
            if (!outcome.Changed)
            {
                rows2.Add(new ResultRow(Path.GetFileName(path), "", "无需改动", outcome.Detail));
                continue;
            }

            try
            {
                if (backup)
                {
                    var backupPath = path + ".bak";
                    if (!File.Exists(backupPath)) File.WriteAllBytes(backupPath, outcome.Original);
                }
                File.WriteAllBytes(path, outcome.Output);
                changed++;
                outputs.Add(path);
                rows2.Add(new ResultRow(Path.GetFileName(path), path, "已写回", $"{outcome.Original.Length} → {outcome.Output.Length} 字节"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                rows2.Add(new ResultRow(Path.GetFileName(path), "", "失败", ex.Message));
            }
        }

        var summary = mode == Formatter.Validate
            ? $"已校验 {rows2.Count(row => row.Status != "失败")} 个文件，语法有效"
            : $"已格式化 {changed} 个文件" + (skipped > 0 ? $"，跳过 {skipped} 个" : "");
        return Task.FromResult(PluginContext.Result(summary, rows2.ToArray(), outputs.ToArray()));
    }
}
