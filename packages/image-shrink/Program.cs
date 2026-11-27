using System.IO;
using System.Windows.Media.Imaging;
using RightToolbox.Images;
using RightToolbox.PluginSdk;

namespace ImageShrink;

public sealed record SqueezeResult(byte[] Bytes, int Quality, int Width, int Height);

public static class Program
{
    private static readonly int[] Qualities = [92, 85, 78, 70, 62, 55, 48, 40, 32, 25];
    private static readonly double[] Scales = [1.0, 0.85, 0.7, 0.58, 0.46, 0.35, 0.25];

    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx) =>
        Task.FromResult(ImageSupport.RunSta(() => Handle(ctx)));

    public static int TargetKb(string choice, int custom) => choice switch
    {
        "100 KB" => 100,
        "200 KB" => 200,
        "500 KB" => 500,
        "1 MB" => 1024,
        "2 MB" => 2048,
        "自定义" => Math.Clamp(custom, 10, 20480),
        _ => throw new InvalidDataException("目标大小选项无效。")
    };

    public static int MaxEdge(string choice) => choice switch
    {
        "不限制" => 0,
        "1920 px" => 1920,
        "1600 px" => 1600,
        "1280 px" => 1280,
        "1080 px" => 1080,
        "800 px" => 800,
        _ => throw new InvalidDataException("最长边选项无效。")
    };

    /// <summary>Lowers JPEG quality first and only downscales once quality alone cannot reach the target.</summary>
    public static SqueezeResult Squeeze(BitmapSource source, long targetBytes)
    {
        foreach (var scale in Scales)
        {
            var width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
            var height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
            var frame = ImageSupport.Resize(source, width, height);
            foreach (var quality in Qualities)
            {
                var bytes = ImageSupport.EncodeJpeg(frame, quality);
                if (bytes.LongLength <= targetBytes) return new SqueezeResult(bytes, quality, width, height);
            }
        }
        throw new IOException($"已经压到最小尺寸与最低质量，仍超过目标 {ImageSupport.FormatBytes(targetBytes)}。请抬高目标或先裁剪图片。");
    }

    private static WorkerEvent Handle(PluginContext ctx)
    {
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中要压缩的图片。");

        var targetBytes = TargetKb(ctx.Param("target", "200 KB"), ctx.IntParam("customKb", 200)) * 1024L;
        var maxEdge = MaxEdge(ctx.Param("maxEdge", "不限制"));
        var outputFolder = ctx.Param("outputFolder", "");
        var rows = new List<ResultRow>();
        var outputs = new List<string>();

        for (var index = 0; index < ctx.Paths.Length; index++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = ctx.Paths[index];
            if (!ImageSupport.IsSupported(path)) throw new InvalidDataException($"不支持的图片格式：{Path.GetFileName(path)}");

            var frame = ImageSupport.Load(path);
            var prepared = ImageSupport.FitWithin(ImageSupport.Flatten(frame), maxEdge);
            var squeezed = Squeeze(prepared, targetBytes);
            var output = PluginContext.UniqueOutput(path, "_squeezed", ".jpg", outputFolder);
            ImageSupport.WriteBytes(output, squeezed.Bytes);

            var before = new FileInfo(path).Length;
            rows.Add(new ResultRow(
                Path.GetFileName(path),
                output,
                "已压缩",
                $"{ImageSupport.FormatBytes(before)} → {ImageSupport.FormatBytes(squeezed.Bytes.LongLength)}（质量 {squeezed.Quality}，{squeezed.Width}×{squeezed.Height}）"));
            outputs.Add(output);
            ctx.ReportProgress(index + 1, ctx.Paths.Length, Path.GetFileName(output));
        }

        return PluginContext.Result($"已输出 {rows.Count} 张图片，原件保持不变。", rows.ToArray(), outputs.ToArray());
    }
}
