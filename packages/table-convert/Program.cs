using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using RightToolbox.PluginSdk;

namespace TableConvert;

public static class TableEngine
{
    static TableEngine()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string ReadTextAuto(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    public static List<List<string>> ParseDelimited(string input, char delimiter)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(input)) return rows;

        var currentRow = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < input.Length && input[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }
                continue;
            }

            if (ch == '"' && cell.Length == 0)
            {
                inQuotes = true;
            }
            else if (ch == delimiter)
            {
                currentRow.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < input.Length && input[i + 1] == '\n') i++;
                currentRow.Add(cell.ToString());
                cell.Clear();
                rows.Add(currentRow);
                currentRow = [];
            }
            else
            {
                cell.Append(ch);
            }
        }

        if (cell.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(cell.ToString());
            rows.Add(currentRow);
        }

        return rows;
    }

    public static string ToJson(List<List<string>> rows, bool hasHeader)
    {
        if (rows.Count == 0) return "[]\n";
        var colCount = rows.Max(r => r.Count);
        var headers = BuildHeaders(rows[0], colCount, hasHeader);
        var startRow = hasHeader ? 1 : 0;

        var records = new List<Dictionary<string, string>>(Math.Max(0, rows.Count - startRow));
        for (var r = startRow; r < rows.Count; r++)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < colCount; c++)
            {
                dict[headers[c]] = c < rows[r].Count ? rows[r][c] : "";
            }
            records.Add(dict);
        }

        return JsonSerializer.Serialize(records, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n";
    }

    public static string ToMarkdownTable(List<List<string>> rows, bool hasHeader)
    {
        if (rows.Count == 0) return "";
        var colCount = rows.Max(r => r.Count);
        var headers = BuildHeaders(rows[0], colCount, hasHeader);
        var startRow = hasHeader ? 1 : 0;

        static string EscapeMd(string value) =>
            value.Replace("\r\n", " ", StringComparison.Ordinal)
                 .Replace("\n", " ", StringComparison.Ordinal)
                 .Replace("|", "\\|", StringComparison.Ordinal);

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", headers.Select(EscapeMd))).AppendLine(" |");
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", colCount))).AppendLine(" |");
        for (var r = startRow; r < rows.Count; r++)
        {
            var cells = Enumerable.Range(0, colCount).Select(c => EscapeMd(c < rows[r].Count ? rows[r][c] : ""));
            sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
        }
        return sb.ToString();
    }

    public static string ToDelimited(List<List<string>> rows, char delimiter)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                if (c > 0) sb.Append(delimiter);
                var val = row[c];
                if (delimiter == ',')
                {
                    if (val.Contains('"') || val.Contains(',') || val.Contains('\n') || val.Contains('\r'))
                        sb.Append('"').Append(val.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                    else
                        sb.Append(val);
                }
                else
                {
                    sb.Append(val.Replace("\t", " ", StringComparison.Ordinal).Replace("\r\n", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string JsonToCsv(string jsonText, out int rowCount)
    {
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;
        var items = new List<JsonElement>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object) items.Add(el);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            items.Add(root);
        }
        else
        {
            throw new InvalidDataException("JSON 根元素须为对象数组或对象。");
        }

        if (items.Count == 0) throw new InvalidDataException("JSON 中没有可导出的对象记录。");

        var keys = new List<string>();
        var keySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var prop in item.EnumerateObject())
            {
                if (keySet.Add(prop.Name)) keys.Add(prop.Name);
            }
        }

        var table = new List<List<string>>(items.Count + 1) { keys };
        foreach (var item in items)
        {
            var row = new List<string>(keys.Count);
            foreach (var key in keys)
            {
                if (!item.TryGetProperty(key, out var val) || val.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    row.Add("");
                else if (val.ValueKind == JsonValueKind.String)
                    row.Add(val.GetString() ?? "");
                else
                    row.Add(val.GetRawText());
            }
            table.Add(row);
        }

        rowCount = items.Count;
        return ToDelimited(table, ',');
    }

    private static string[] BuildHeaders(List<string> firstRow, int colCount, bool hasHeader)
    {
        var headers = new string[colCount];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < colCount; i++)
        {
            var raw = hasHeader && i < firstRow.Count && !string.IsNullOrWhiteSpace(firstRow[i])
                ? firstRow[i].Trim()
                : $"col_{i + 1}";
            var name = raw;
            var suffix = 2;
            while (!used.Add(name)) name = $"{raw}_{suffix++}";
            headers[i] = name;
        }
        return headers;
    }
}

internal static class Program
{
    private static Task<int> Main() => PluginHost.RunAsync(async ctx =>
    {
        await Task.CompletedTask;
        return ctx.CommandId switch
        {
            "community.table-convert.csv-to-other" => RunCsvConvert(ctx),
            "community.table-convert.json-to-csv" => RunJsonToCsv(ctx),
            _ => throw new InvalidDataException($"未知命令：{ctx.CommandId}")
        };
    });

    private static WorkerEvent RunCsvConvert(PluginContext ctx)
    {
        var targetFormat = ctx.Param("targetFormat", "JSON 对象数组");
        var hasHeader = ctx.BoolParam("hasHeader", true);
        var outputFolder = ctx.Param("outputFolder", "");

        var rows = new List<ResultRow>(ctx.Paths.Length);
        var outputs = new List<string>(ctx.Paths.Length);

        for (var i = 0; i < ctx.Paths.Length; i++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = ctx.Paths[i];
            var delimiter = Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
            var parsed = TableEngine.ParseDelimited(TableEngine.ReadTextAuto(path), delimiter);

            var (ext, suffix, content, encoding) = targetFormat switch
            {
                "Markdown 表格" => (".md", "", TableEngine.ToMarkdownTable(parsed, hasHeader), new UTF8Encoding(false)),
                "TSV (制表符分隔)" => (".tsv", "", TableEngine.ToDelimited(parsed, '\t'), new UTF8Encoding(false)),
                "CSV (UTF-8 BOM)" => (".csv", "_utf8", TableEngine.ToDelimited(parsed, ','), new UTF8Encoding(true)),
                _ => (".json", "", TableEngine.ToJson(parsed, hasHeader), new UTF8Encoding(false))
            };

            var outPath = PluginContext.UniqueOutput(path, suffix, ext, outputFolder);
            File.WriteAllText(outPath, content, encoding);
            outputs.Add(outPath);
            rows.Add(new ResultRow(Path.GetFileName(path), outPath, "已转换", $"{parsed.Count} 行"));
            ctx.ReportProgress(i + 1, ctx.Paths.Length, Path.GetFileName(path));
        }

        return PluginContext.Result($"已转换 {outputs.Count} 个表格文件", rows.ToArray(), outputs.ToArray());
    }

    private static WorkerEvent RunJsonToCsv(PluginContext ctx)
    {
        var outputFolder = ctx.Param("outputFolder", "");
        var rows = new List<ResultRow>(ctx.Paths.Length);
        var outputs = new List<string>(ctx.Paths.Length);

        for (var i = 0; i < ctx.Paths.Length; i++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = ctx.Paths[i];
            var csv = TableEngine.JsonToCsv(TableEngine.ReadTextAuto(path), out var count);
            var outPath = PluginContext.UniqueOutput(path, "", ".csv", outputFolder);
            File.WriteAllText(outPath, csv, new UTF8Encoding(true));
            outputs.Add(outPath);
            rows.Add(new ResultRow(Path.GetFileName(path), outPath, "已导出", $"{count} 条记录"));
            ctx.ReportProgress(i + 1, ctx.Paths.Length, Path.GetFileName(path));
        }

        return PluginContext.Result($"已导出 {outputs.Count} 个 CSV 文件（UTF-8 BOM）", rows.ToArray(), outputs.ToArray());
    }
}
