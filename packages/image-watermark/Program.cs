using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RightToolbox.Images;
using RightToolbox.PluginSdk;

namespace ImageWatermark;

public static class WatermarkBuilder
{
    public static Color ParseColor(string value) => value switch
    {
        "白色" => Colors.White,
        "黑色" => Colors.Black,
        "灰色" => Color.FromRgb(0x9A, 0x9A, 0x9A),
        "红色" => Color.FromRgb(0xD9, 0x30, 0x25),
        _ => throw new InvalidDataException("水印颜色无效。")
    };

    public static (double X, double Y) Anchor(string position, double width, double height, double textWidth, double textHeight, double margin) => position switch
    {
        "右下角" => (width - textWidth - margin, height - textHeight - margin),
        "左下角" => (margin, height - textHeight - margin),
        "右上角" => (width - textWidth - margin, margin),
        "左上角" => (margin, margin),
        "居中" => ((width - textWidth) / 2, (height - textHeight) / 2),
        _ => throw new InvalidDataException("水印位置无效。")
    };

    public static bool IsTiled(string position) => position == "平铺";

    public static BitmapSource Apply(BitmapSource source, string text, string position, double fontSize, double opacity, Color color, double margin)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("请填写水印文字。");
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        margin = Math.Clamp(margin, 0, Math.Min(width, height) / 3.0);
        var size = Math.Clamp(fontSize, 8, Math.Max(8, width / 8.0));

        var brush = new SolidColorBrush(color) { Opacity = Math.Clamp(opacity, 0.05, 1.0) };
        var formatted = ImageSupport.Text(text, size, color, bold: true);
        formatted.SetForegroundBrush(brush);

        return ImageSupport.Compose(width, height, 96, dc =>
        {
            // 先铺白底：透明 PNG 转 JPEG 不会变成黑底。
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawImage(source, new Rect(0, 0, width, height));

            if (IsTiled(position))
            {
                var stepX = formatted.Width + size * 3;
                var stepY = formatted.Height + size * 6;
                for (double y = -height; y < height * 2; y += stepY)
                {
                    for (double x = -width; x < width * 2; x += stepX)
                    {
                        dc.PushTransform(new RotateTransform(-30, x, y));
                        dc.DrawText(formatted, new Point(x, y));
                        dc.Pop();
                    }
                }
                return;
            }

            var (left, top) = Anchor(position, width, height, formatted.Width, formatted.Height, margin);
            dc.DrawText(formatted, new Point(left, top));
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
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中要加水印的图片。");

        var text = ctx.Param("text", "").Trim();
        if (text.Length == 0) throw new InvalidDataException("请填写水印文字。");
        if (text.Length > 60) throw new InvalidDataException("水印文字不要超过 60 个字符。");

        var position = ctx.Param("position", "右下角");
        var fontSize = ctx.DoubleParam("fontSize", 36);
        var opacity = ctx.DoubleParam("opacity", 45) / 100.0;
        var color = WatermarkBuilder.ParseColor(ctx.Param("color", "白色"));
        var margin = ctx.DoubleParam("margin", 32);
        var outputFolder = ctx.Param("outputFolder", "");

        var rows = new List<ResultRow>();
        var outputs = new List<string>();

        for (var index = 0; index < ctx.Paths.Length; index++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = ctx.Paths[index];
            if (!ImageSupport.IsSupported(path)) throw new InvalidDataException($"不支持的图片格式：{Path.GetFileName(path)}");

            var frame = ImageSupport.Flatten(ImageSupport.Load(path));
            var marked = WatermarkBuilder.Apply(frame, text, position, fontSize, opacity, color, margin);
            var bytes = ImageSupport.EncodeJpeg(marked, 92);
            var output = PluginContext.UniqueOutput(path, "_watermark", ".jpg", outputFolder);
            ImageSupport.WriteBytes(output, bytes);

            rows.Add(new ResultRow(
                Path.GetFileName(path),
                output,
                "已加水印",
                $"{position} · {color} · 不透明度 {(int)(opacity * 100)}% · {ImageSupport.FormatBytes(bytes.LongLength)}"));
            outputs.Add(output);
            ctx.ReportProgress(index + 1, ctx.Paths.Length, Path.GetFileName(output));
        }

        return PluginContext.Result($"已为 {rows.Count} 张图片加上水印，原件保持不变。", rows.ToArray(), outputs.ToArray());
    }
}
