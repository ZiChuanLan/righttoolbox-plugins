using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RightToolbox.PluginSdk;

public static class Wire
{
    public const int Version = 1;
    public const int MaxMessageChars = 2 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("JSON 消息为空。");

    public static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken token = default)
    {
        var text = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) > 0)
        {
            if (buffer[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= MaxMessageChars) throw new InvalidDataException("消息超过 2 MB 上限。");
            text.Append(buffer[0]);
        }
        return text.Length == 0 ? null : text.ToString();
    }
}

public sealed record Invocation
{
    public int ProtocolVersion { get; init; } = Wire.Version;
    public string RequestId { get; init; } = "";
    public string CommandId { get; init; } = "";
    public string Phase { get; init; } = "execute";
    public string[] Paths { get; init; } = [];
    public Dictionary<string, string> Parameters { get; init; } = [];
    public JsonElement? Plan { get; init; }
}

public sealed record WorkerEvent
{
    public string Type { get; init; } = "progress";
    public string Message { get; init; } = "";
    public int Current { get; init; }
    public int Total { get; init; }
    public string[] Outputs { get; init; } = [];
    public string? ClipboardText { get; init; }
    public string? UndoId { get; init; }
    public JsonElement? Plan { get; init; }
    public ResultRow[] Rows { get; init; } = [];
}

public sealed record ResultRow(string Source, string Target, string Status = "就绪", string Detail = "");

public sealed class PluginContext
{
    private readonly Action<WorkerEvent> emit;

    internal PluginContext(Invocation request, Action<WorkerEvent> emit, CancellationToken cancellation)
    {
        Request = request;
        this.emit = emit;
        Cancellation = cancellation;
    }

    public Invocation Request { get; }
    public string CommandId => Request.CommandId;
    public string Phase => Request.Phase;
    public string[] Paths => Request.Paths;
    public IReadOnlyDictionary<string, string> Parameters => Request.Parameters;
    public JsonElement? Plan => Request.Plan;
    public CancellationToken Cancellation { get; }

    public string Param(string key, string fallback = "") =>
        Request.Parameters.TryGetValue(key, out var value) ? value : fallback;

    public bool BoolParam(string key, bool fallback = false) =>
        Request.Parameters.TryGetValue(key, out var value)
            ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            : fallback;

    public int IntParam(string key, int fallback = 0) =>
        Request.Parameters.TryGetValue(key, out var value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    public double DoubleParam(string key, double fallback = 0) =>
        Request.Parameters.TryGetValue(key, out var value) &&
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
        double.IsFinite(parsed)
            ? parsed
            : fallback;

    public void ReportProgress(int current, int total, string message) =>
        emit(new WorkerEvent { Type = "progress", Current = current, Total = total, Message = message });

    public static WorkerEvent Result(
        string message,
        ResultRow[]? rows = null,
        string[]? outputs = null,
        string? clipboardText = null,
        string? undoId = null) =>
        new()
        {
            Type = "result",
            Message = message,
            Rows = rows ?? [],
            Outputs = outputs ?? [],
            ClipboardText = clipboardText,
            UndoId = undoId
        };

    public static WorkerEvent Preview<TPlan>(string message, TPlan plan, ResultRow[]? rows = null) =>
        new()
        {
            Type = "preview",
            Message = message,
            Plan = JsonSerializer.SerializeToElement(plan, Wire.Json),
            Rows = rows ?? []
        };

    public static string UniqueOutput(string sourcePath, string suffix, string extension, string? outputFolder = null)
    {
        var directory = string.IsNullOrWhiteSpace(outputFolder)
            ? Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory
            : Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(sourcePath) + suffix;
        var ext = string.IsNullOrEmpty(extension) ? "" : (extension.StartsWith('.') ? extension : "." + extension);
        for (var i = 0; i < 10000; i++)
        {
            var name = stem + (i == 0 ? "" : $" ({i})") + ext;
            var candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
        throw new IOException("同名输出过多，请清理目标目录或更换输出文件夹。");
    }

    /// <summary>Keeps the requested file name and extension, appending " (n)" when the target already exists.</summary>
    public static string UniqueFilePath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath)) return desiredPath;
        var directory = Path.GetDirectoryName(desiredPath) ?? Environment.CurrentDirectory;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("同名输出过多，请清理目标目录或更换输出文件夹。");
    }
}

public static class PluginHost
{
    public static async Task<int> RunAsync(Func<PluginContext, Task<WorkerEvent>> handler)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var outputLock = new object();

        void Emit(WorkerEvent evt)
        {
            var json = Wire.Serialize(evt);
            lock (outputLock)
            {
                Console.WriteLine(json);
                Console.Out.Flush();
            }
        }

        try
        {
            var firstLine = await Wire.ReadLineAsync(Console.In);
            if (string.IsNullOrWhiteSpace(firstLine))
                throw new InvalidDataException("未收到插件调用消息。");

            var request = Wire.Deserialize<Invocation>(firstLine);
            if (request.ProtocolVersion != Wire.Version)
                throw new InvalidDataException($"不支持的协议版本：{request.ProtocolVersion}。");

            using var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await Wire.ReadLineAsync(Console.In) is { } line)
                    {
                        if (string.Equals(line, "CANCEL", StringComparison.Ordinal))
                        {
                            cts.Cancel();
                            break;
                        }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            });

            var ctx = new PluginContext(request, Emit, cts.Token);
            var terminal = await handler(ctx);
            if (terminal.Type is not ("result" or "preview" or "error"))
                throw new InvalidOperationException("插件处理器必须返回 result、preview 或 error 终止事件。");

            Emit(terminal);
            return terminal.Type == "error" ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            Emit(new WorkerEvent { Type = "error", Message = "操作已取消。" });
            return 1;
        }
        catch (Exception ex)
        {
            Emit(new WorkerEvent { Type = "error", Message = ex.Message });
            return 1;
        }
    }
}
