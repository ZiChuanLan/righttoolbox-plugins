using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RightToolbox.Images;
using RightToolbox.PluginSdk;

namespace IdPhoto;

public static class IdPhotoBuilder
{
    public static (int Width, int Height) ParseSpec(string spec, int customWidth, int customHeight) => spec switch
    {
        "一寸 (295×413)" => (295, 413),
        "二寸 (413×579)" => (413, 579),
        "小一寸 (260×378)" => (260, 378),
        "大一寸 (390×567)" => (390, 567),
        "自定义" => (Math.Clamp(customWidth, 100, 4000), Math.Clamp(customHeight, 100, 4000)),
        _ => throw new InvalidDataException("证件照规格无效。")
    };

    public static Color ParseColor(string value) => value switch
    {
        "白色" => Colors.White,
        "蓝色" => Color.FromRgb(0x43, 0x86, 0xC6),
        "红色" => Color.FromRgb(0xD9, 0x30, 0x25),
        "浅灰" => Color.FromRgb(0xF2, 0xF2, 0xF2),
        _ => throw new InvalidDataException("背景颜色无效。")
    };

    /// <summary>crop=true 时按目标比例居中裁切（不缩放内容、不留白）；false 时完整留白。</summary>
    public static BitmapSource Build(BitmapSource source, int targetWidth, int targetHeight, bool crop, Color background)
    {
        if (crop)
        {
            var ratio = (double)targetWidth / targetHeight;
            var sourceRatio = (double)source.PixelWidth / source.PixelHeight;
            int width, height;
            if (sourceRatio > ratio)
            {
                height = source.PixelHeight;
                width = Math.Max(1, (int)Math.Round(height * ratio));
            }
            else
            {
                width = source.PixelWidth;
                height = Math.Max(1, (int)Math.Round(width / ratio));
            }
            var left = (source.PixelWidth - width) / 2;
            var top = (source.PixelHeight - height) / 2;
            var cropped = ImageSupport.Crop(source, left, top, width, height);
            return ImageSupport.Resize(cropped, targetWidth, targetHeight);
        }

        var scale = Math.Min((double)targetWidth / source.PixelWidth, (double)targetHeight / source.PixelHeight);
        var fitted = ImageSupport.Resize(
            source,
            Math.Max(1, (int)Math.Round(source.PixelWidth * scale)),
            Math.Max(1, (int)Math.Round(source.PixelHeight * scale)));
        return ImageSupport.Compose(targetWidth, targetHeight, 96, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, targetWidth, targetHeight));
            dc.DrawImage(fitted, new Rect((targetWidth - fitted.PixelWidth) / 2.0, (targetHeight - fitted.PixelHeight) / 2.0, fitted.PixelWidth, fitted.PixelHeight));
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
        if (ctx.Paths.Length == 0) throw new InvalidDataException("请先选中要裁切成证件照的图片。");

        var (width, height) = IdPhotoBuilder.ParseSpec(ctx.Param("spec", "一寸 (295×413)"), ctx.IntParam("customWidth", 295), ctx.IntParam("customHeight", 413));
        var crop = ctx.Param("mode", "居中裁切（推荐）") != "完整留白";
        var background = IdPhotoBuilder.ParseColor(ctx.Param("background", "白色"));
        var outputFolder = ctx.Param("outputFolder", "");

        var rows = new List<ResultRow>();
        var outputs = new List<string>();

        for (var index = 0; index < ctx.Paths.Length; index++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = ctx.Paths[index];
            if (!ImageSupport.IsSupported(path)) throw new InvalidDataException($"不支持的图片格式：{Path.GetFileName(path)}");

            var frame = ImageSupport.Flatten(ImageSupport.Load(path));
            var photo = IdPhotoBuilder.Build(frame, width, height, crop, background);
            var bytes = ImageSupport.EncodeJpeg(photo, 95);
            var output = PluginContext.UniqueOutput(path, "_idphoto", ".jpg", outputFolder);
            ImageSupport.WriteBytes(output, bytes);

            rows.Add(new ResultRow(
                Path.GetFileName(path),
                output,
                "已生成",
                $"{width}×{height} · {(crop ? "居中裁切" : "完整留白")} · {ImageSupport.FormatBytes(bytes.LongLength)}"));
            outputs.Add(output);
            ctx.ReportProgress(index + 1, ctx.Paths.Length, Path.GetFileName(output));
        }

        return PluginContext.Result($"已生成 {rows.Count} 张证件照，原件保持不变。", rows.ToArray(), outputs.ToArray());
    }
}
