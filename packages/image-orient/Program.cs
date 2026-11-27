using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RightToolbox.Images;
using RightToolbox.PluginSdk;

namespace ImageOrient;

public static class OrientBuilder
{
    /// <summary>EXIF Orientation (0x0112) of a photo; 1 when absent or unreadable.</summary>
    public static int ReadOrientation(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
            if (decoder.Metadata is not BitmapMetadata metadata) return 1;
            var value = metadata.GetQuery("/app1/ifd/{ushort=274}");
            return value switch
            {
                ushort unsigned => unsigned,
                short signed => signed,
                int number => number,
                uint wide => (int)wide,
                _ => 1
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException)
        {
            return 1;
        }
    }

    public static (int Rotate, bool MirrorX, bool MirrorY) ExifTransform(int orientation) => orientation switch
    {
        1 => (0, false, false),
        2 => (0, true, false),
        3 => (180, false, false),
        4 => (0, false, true),
        5 => (90, true, false),
        6 => (90, false, false),
        7 => (270, true, false),
        8 => (270, false, false),
        _ => (0, false, false)
    };

    public static int ParseManualRotation(string choice) => choice switch
    {
        "自动（按 EXIF 修正）" => -1,
        "顺时针旋转 90°" => 90,
        "逆时针旋转 90°" => 270,
        "旋转 180°" => 180,
        _ => throw new InvalidDataException("旋转方式无效。")
    };

    public static (bool MirrorX, bool MirrorY) ParseFlip(string choice) => choice switch
    {
        "不翻转" => (false, false),
        "水平翻转（左右镜像）" => (true, false),
        "垂直翻转（上下镜像）" => (false, true),
        _ => throw new InvalidDataException("翻转方式无效。")
    };

    public static BitmapSource Rotate(BitmapSource source, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        return degrees switch
        {
            0 => source,
            90 => ImageSupport.Compose(height, width, 96, dc =>
            {
                dc.PushTransform(new TranslateTransform(height, 0));
                dc.PushTransform(new RotateTransform(90));
                dc.DrawImage(source, new Rect(0, 0, width, height));
                dc.Pop();
                dc.Pop();
            }),
            180 => ImageSupport.Compose(width, height, 96, dc =>
            {
                dc.PushTransform(new TranslateTransform(width, height));
                dc.PushTransform(new RotateTransform(180));
                dc.DrawImage(source, new Rect(0, 0, width, height));
                dc.Pop();
                dc.Pop();
            }),
            270 => ImageSupport.Compose(height, width, 96, dc =>
            {
                dc.PushTransform(new TranslateTransform(0, width));
                dc.PushTransform(new RotateTransform(-90));
                dc.DrawImage(source, new Rect(0, 0, width, height));
                dc.Pop();
                dc.Pop();
            }),
            _ => throw new InvalidDataException("旋转角度只支持 90 / 180 / 270。")
        };
    }

    public static BitmapSource Mirror(BitmapSource source, bool horizontal) =>
        ImageSupport.Compose(source.PixelWidth, source.PixelHeight, 96, dc =>
        {
            dc.PushTransform(horizontal
                ? new TranslateTransform(source.PixelWidth, 0)
                : new TranslateTransform(0, source.PixelHeight));
            dc.PushTransform(horizontal ? new ScaleTransform(-1, 1) : new ScaleTransform(1, -1));
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.Pop();
            dc.Pop();
        });

    public static BitmapSource Apply(BitmapSource source, int rotate, bool mirrorX, bool mirrorY)
    {
        var result = Rotate(source, rotate);
        if (mirrorX) result = Mirror(result, horizontal: true);
        if (mirrorY) result = Mirror(result, horizontal: false);
        return result;
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

        var manual = OrientBuilder.ParseManualRotation(ctx.Param("action", "自动（按 EXIF 修正）"));
        var (flipX, flipY) = OrientBuilder.ParseFlip(ctx.Param("flip", "不翻转"));
        var outputFolder = ctx.Param("outputFolder", "");
        var rows = new List<ResultRow>();
        var outputs = new List<string>();

        for (var index = 0; index < ctx.Paths.Length; index++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var path = ctx.Paths[index];
            if (!ImageSupport.IsSupported(path)) throw new InvalidDataException($"不支持的图片格式：{Path.GetFileName(path)}");

            int rotate;
            var source = string.Empty;
            if (manual < 0)
            {
                var orientation = OrientBuilder.ReadOrientation(path);
                (rotate, flipX, flipY) = OrientBuilder.ExifTransform(orientation);
                source = $"EXIF={orientation}";
                if (rotate == 0 && !flipX && !flipY)
                {
                    rows.Add(new ResultRow(Path.GetFileName(path), path, "无需改动", "方向已经正确"));
                    ctx.ReportProgress(index + 1, ctx.Paths.Length, Path.GetFileName(path));
                    continue;
                }
            }
            else
            {
                rotate = manual;
                source = "手动";
            }

            var frame = ImageSupport.Flatten(ImageSupport.Load(path));
            var oriented = OrientBuilder.Apply(frame, rotate, flipX, flipY);
            var keepPng = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);
            var bytes = keepPng ? ImageSupport.EncodePng(oriented) : ImageSupport.EncodeJpeg(oriented, 95);
            var output = PluginContext.UniqueOutput(path, "_oriented", keepPng ? ".png" : ".jpg", outputFolder);
            ImageSupport.WriteBytes(output, bytes);

            var steps = new List<string>();
            if (rotate != 0) steps.Add($"旋转 {rotate}°");
            if (flipX) steps.Add("左右镜像");
            if (flipY) steps.Add("上下镜像");
            rows.Add(new ResultRow(
                Path.GetFileName(path),
                output,
                "已修正",
                $"{source} · {string.Join(" + ", steps)} · {oriented.PixelWidth}×{oriented.PixelHeight}"));
            outputs.Add(output);
            ctx.ReportProgress(index + 1, ctx.Paths.Length, Path.GetFileName(output));

            // 自动模式下每组参数来自各自文件的 EXIF，不能带到下一张。
            if (manual < 0) { flipX = false; flipY = false; }
        }

        var changed = rows.Count(row => row.Status == "已修正");
        return PluginContext.Result($"已修正 {changed} 张图片方向，原件保持不变。", rows.ToArray(), outputs.ToArray());
    }
}
