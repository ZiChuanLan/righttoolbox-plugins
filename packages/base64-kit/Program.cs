using System.IO;
using System.Text;
using RightToolbox.PluginSdk;

namespace Base64Kit;

public static class Kit
{
    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".heic"] = "image/heic",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".bmp"] = "image/bmp",
        [".pdf"] = "application/pdf",
        [".json"] = "application/json",
        [".txt"] = "text/plain;charset=utf-8",
        [".md"] = "text/markdown;charset=utf-8",
        [".html"] = "text/html;charset=utf-8",
        [".css"] = "text/css;charset=utf-8",
        [".js"] = "text/javascript;charset=utf-8",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".mp3"] = "audio/mpeg",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".zip"] = "application/zip"
    };

    public const string DataUri = "Data URI (含 MIME)";
    public const string PlainBase64 = "纯 Base64 字符串";

    public static string MimeOf(string path) =>
        MimeByExtension.TryGetValue(Path.GetExtension(path), out var mime) ? mime : "application/octet-stream";

    public static string ExtensionForMime(string mime)
    {
        foreach (var pair in MimeByExtension)
            if (pair.Value.Equals(mime, StringComparison.OrdinalIgnoreCase)) return pair.Key;
        return "";
    }

    public static string EncodeFile(string path, string mode)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"文件不存在：{Path.GetFileName(path)}");
        var bytes = File.ReadAllBytes(path);
        var base64 = Convert.ToBase64String(bytes);
        return mode == PlainBase64 ? base64 : $"data:{MimeOf(path)};base64,{base64}";
    }

    public static (byte[] Bytes, string Extension) Decode(string raw)
    {
        var text = raw.Trim();
        var extension = "";
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = text.IndexOf(',');
            if (comma > 0)
            {
                var header = text[5..comma];
                var semicolon = header.IndexOf(';');
                var mime = semicolon > 0 ? header[..semicolon] : header;
                extension = ExtensionForMime(mime);
                text = text[(comma + 1)..];
            }
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
            if (!char.IsWhiteSpace(character)) builder.Append(character);
        var cleaned = builder.ToString();

        if (cleaned.Length == 0) throw new InvalidDataException("没有可解码的 Base64 内容。");
        try
        {
            return (Convert.FromBase64String(cleaned), extension);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("内容不是合法的 Base64（可能包含非法字符或长度不对）。");
        }
    }

    public static string SafeExtension(string value)
    {
        var extension = value.Trim();
        if (extension.Length == 0) return "";
        if (!extension.StartsWith('.')) extension = "." + extension;
        return extension.All(character => char.IsLetterOrDigit(character) || character == '.') ? extension : "";
    }
}

public static class Program
{
    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx)
    {
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中文件。");
        var command = ctx.CommandId.Split('.').Last();

        if (command == "decode") return Task.FromResult(Decode(ctx));
        return Task.FromResult(Encode(ctx));
    }

    private static WorkerEvent Encode(PluginContext ctx)
    {
        var mode = ctx.Param("outputMode", Kit.DataUri);
        if (mode != Kit.DataUri && mode != Kit.PlainBase64) throw new InvalidDataException("编码格式无效。");
        var saveToFile = ctx.BoolParam("saveToFile", false);
        var outputFolder = ctx.Param("outputFolder", "");

        var rows = new List<ResultRow>();
        var outputs = new List<string>();
        var payloads = new List<string>();

        for (var index = 0; index < ctx.Paths.Length; index++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = ctx.Paths[index];
            var encoded = Kit.EncodeFile(path, mode);
            payloads.Add(encoded);

            var note = $"{new FileInfo(path).Length} B → {encoded.Length} 字符";
            if (saveToFile)
            {
                var directory = string.IsNullOrWhiteSpace(outputFolder) ? Path.GetDirectoryName(path) ?? Environment.CurrentDirectory : Path.GetFullPath(outputFolder);
                Directory.CreateDirectory(directory);
                var target = PluginContext.UniqueFilePath(Path.Combine(directory, Path.GetFileName(path) + ".b64.txt"));
                File.WriteAllText(target, encoded, new UTF8Encoding(false));
                outputs.Add(target);
                note += " · 已写 .b64.txt";
            }

            rows.Add(new ResultRow(Path.GetFileName(path), saveToFile ? outputs[^1] : "", "已编码", note));
            ctx.ReportProgress(index + 1, ctx.Paths.Length, Path.GetFileName(path));
        }

        var clipboard = string.Join(Environment.NewLine, payloads);
        var message = saveToFile
            ? $"已编码 {rows.Count} 个文件并写入 .b64.txt（内容也已放入剪贴板）。"
            : $"已编码 {rows.Count} 个文件，结果已放入剪贴板。";
        return PluginContext.Result(message, rows.ToArray(), outputs.ToArray(), clipboard);
    }

    private static WorkerEvent Decode(PluginContext ctx)
    {
        var payload = ctx.Param("payload", "").Trim();
        if (payload.Length == 0) throw new InvalidDataException("请把 Base64 或 Data URI 内容填进「内容」参数，或先编码到剪贴板。");

        var (bytes, guessed) = Kit.Decode(payload);
        var extension = Kit.SafeExtension(ctx.Param("extension", ""));
        if (extension.Length == 0) extension = guessed.Length > 0 ? guessed : ".bin";

        var outputFolder = ctx.Param("outputFolder", "");
        var reference = ctx.Paths[0];
        var directory = string.IsNullOrWhiteSpace(outputFolder)
            ? (Directory.Exists(reference) ? reference : Path.GetDirectoryName(reference) ?? Environment.CurrentDirectory)
            : Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(directory);

        var stem = Directory.Exists(reference) ? "decoded" : Path.GetFileNameWithoutExtension(reference);
        var target = PluginContext.UniqueFilePath(Path.Combine(directory, stem + "_decoded" + extension));
        File.WriteAllBytes(target, bytes);

        var row = new ResultRow(Path.GetFileName(reference), target, "已还原", $"{bytes.Length} B · {extension}");
        return PluginContext.Result($"已还原 {Path.GetFileName(target)}（{bytes.Length} 字节）。", [row], [target]);
    }
}
