using System.IO;
using QRCoder;
using RightToolbox.PluginSdk;

namespace QrCodeGen;

public static class QrBuilder
{
    private static readonly byte[] White = [0xFF, 0xFF, 0xFF, 0xFF];

    public static QRCodeGenerator.ECCLevel ParseLevel(string value) => value switch
    {
        "L (7%)" => QRCodeGenerator.ECCLevel.L,
        "M (15%)" => QRCodeGenerator.ECCLevel.M,
        "Q (25%)" => QRCodeGenerator.ECCLevel.Q,
        "H (30%)" => QRCodeGenerator.ECCLevel.H,
        _ => throw new InvalidDataException("纠错级别无效。")
    };

    public static byte[] ParseDarkColor(string value) => value switch
    {
        "黑色" => [0x00, 0x00, 0x00, 0xFF],
        "深蓝" => [0x14, 0x3C, 0x8C, 0xFF],
        "深绿" => [0x11, 0x5E, 0x2C, 0xFF],
        "红色" => [0xB3, 0x1B, 0x1B, 0xFF],
        _ => throw new InvalidDataException("前景色无效。")
    };

    public static QRCodeData CreateData(string content, QRCodeGenerator.ECCLevel level)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("请填写要编码的内容。");
        if (content.Length > 1200) throw new InvalidDataException("内容过长（超过 1200 字符），二维码会大到扫不动。");
        using var generator = new QRCodeGenerator();
        return generator.CreateQrCode(content, level);
    }

    public static byte[] Render(string content, QRCodeGenerator.ECCLevel level, int targetSize, byte[] dark)
    {
        targetSize = Math.Clamp(targetSize, 96, 4096);
        using var data = CreateData(content, level);
        var modules = data.ModuleMatrix.Count;
        var pixelsPerModule = Math.Max(1, targetSize / modules);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule, dark, White);
    }
}

public static class Program
{
    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx)
    {
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中一个文件或文件夹作为输出位置。");

        var content = ctx.Param("content", "").Trim();
        if (content.Length == 0) throw new InvalidDataException("请填写要编码的内容（网址、文字、WiFi 密码等）。");

        var level = QrBuilder.ParseLevel(ctx.Param("level", "M (15%)"));
        var size = ctx.IntParam("size", 512);
        var dark = QrBuilder.ParseDarkColor(ctx.Param("darkColor", "黑色"));
        var fileName = ctx.Param("fileName", "qrcode.png").Trim();
        if (fileName.Length == 0) fileName = "qrcode.png";
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("文件名包含非法字符。");
        if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";

        var reference = ctx.Paths[0];
        var directory = Directory.Exists(reference) ? reference : Path.GetDirectoryName(reference) ?? Environment.CurrentDirectory;
        var target = PluginContext.UniqueFilePath(Path.Combine(directory, fileName));

        var bytes = QrBuilder.Render(content, level, size, dark);
        File.WriteAllBytes(target, bytes);

        var preview = content.Length > 40 ? content[..40] + "…" : content;
        var row = new ResultRow(
            preview,
            target,
            "已生成",
            $"{level} · {(bytes.Length > 1024 ? $"{bytes.Length / 1024.0:F1} KB" : $"{bytes.Length} B")}");
        return Task.FromResult(PluginContext.Result($"二维码已保存到 {Path.GetFileName(target)}。", [row], [target]));
    }
}
