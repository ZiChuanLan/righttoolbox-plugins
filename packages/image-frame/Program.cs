using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RightToolbox.Images;
using RightToolbox.PluginSdk;

namespace ImageFrame;

public static class FrameBuilder
{
    public const string RoundedStyle = "圆角（透明）";

    public static Color ParseColor(string value) => value switch
    {
        "白色" => Colors.White,
        "黑色" => Colors.Black,
        "米色" => Color.FromRgb(0xF7, 0xF3, 0xE8),
        _ => throw new InvalidDataException("边框颜色无效。")
    };

    public static bool ProducesTransparency(string style) => style == RoundedStyle;

    /// <summary>边框宽度按长边百分比取整，避免小图被压成一条缝、大图只剩一圈细线。</summary>
    public static int BorderPixels(BitmapSource source, string style, double borderPercent)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        var percent = Math.Clamp(borderPercent, 0, 20);
        var border = (int)Math.Round(longest * percent / 100.0);
        return style == "细黑边" ? Math.Max(2, border / 4) : border;
    }

    public static BitmapSource Apply(BitmapSource source, string style, double borderPercent, double radiusPercent, Color color)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;

        if (style == RoundedStyle)
        {
            var radius = (int)Math.Round(Math.Min(width, height) * Math.Clamp(radiusPercent, 2, 50) / 100.0);
            return ImageSupport.Compose(width, height, 96, dc =>
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), radius, radius));
                dc.DrawImage(source, new Rect(0, 0, width, height));
                dc.Pop();
            });
        }

        if (style is not ("白边（打印留白）" or "拍立得边框" or "细黑边"))
            throw new InvalidDataException("边框样式无效。");

        var border = BorderPixels(source, style, borderPercent);
        var bottom = style == "拍立得边框" ? border * 3 : border;
        var canvasWidth = width + border * 2;
        var canvasHeight = height + border + bottom;
        var brush = new SolidColorBrush(style == "细黑边" ? Colors.Black : color);

        return ImageSupport.Compose(canvasWidth, canvasHeight, 96, dc =>
        {
            dc.DrawRectangle(brush, null, new Rect(0, 0, canvasWidth, canvasHeight));
            dc.DrawImage(source, new Rect(border, border, width, height));
        });
    }
}

public static class Program
{
    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx) =>
        Task.FromResult(ImageSupport.RunSta(() => Handle(ctx)));

    private static WorkerEvent Handle(PluginContext ctx)
    {
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中图片。");

        var style = ctx.Param("style", "白边（打印留白）");
        var borderPercent = ctx.DoubleParam("borderPercent", 4);
        var radiusPercent = ctx.DoubleParam("radiusPercent", 8);
        var color = FrameBuilder.ParseColor(ctx.Param("color", "白色"));
        var outputFolder = ctx.Param("outputFolder", "");
        var transparent = FrameBuilder.ProducesTransparency(style);

        var rows = new List<ResultRow>();
        var outputs = new List<string>();

        for (var index = 0; index < ctx.Paths.Length; index++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = ctx.Paths[index];
            if (!ImageSupport.IsSupported(path)) throw new InvalidDataException($"不支持的图片格式：{Path.GetFileName(path)}");

            var frame = ImageSupport.Flatten(ImageSupport.Load(path));
            var framed = FrameBuilder.Apply(frame, style, borderPercent, radiusPercent, color);
            var bytes = transparent ? ImageSupport.EncodePng(framed) : ImageSupport.EncodeJpeg(framed, 95);
            var output = PluginContext.UniqueOutput(path, "_frame", transparent ? ".png" : ".jpg", outputFolder);
            ImageSupport.WriteBytes(output, bytes);

            var detail = transparent
                ? $"{style} · 圆角 {radiusPercent:0.#}% · {framed.PixelWidth}×{framed.PixelHeight} · 透明背景"
                : $"{style} · {(style == "细黑边" ? "黑色" : ctx.Param("color", "白色"))} · {framed.PixelWidth}×{framed.PixelHeight}";
            rows.Add(new ResultRow(Path.GetFileName(path), output, "已加边框", $"{detail} · {ImageSupport.FormatBytes(bytes.LongLength)}"));
            outputs.Add(output);
            ctx.ReportProgress(index + 1, ctx.Paths.Length, Path.GetFileName(output));
        }

        return PluginContext.Result($"已为 {rows.Count} 张图片加好边框，原件保持不变。", rows.ToArray(), outputs.ToArray());
    }
}
