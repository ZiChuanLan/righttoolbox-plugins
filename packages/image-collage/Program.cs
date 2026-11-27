using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RightToolbox.Images;
using RightToolbox.PluginSdk;

namespace ImageCollage;

public enum CollageLayout { Vertical, Horizontal, Grid3, Grid2 }

public sealed record CollageResult(BitmapSource Image, int Columns, int Rows);

public static class CollageBuilder
{
    private const long MaxPixels = 100_000_000;

    public static CollageLayout ParseLayout(string value) => value switch
    {
        "竖向长图" => CollageLayout.Vertical,
        "横向长图" => CollageLayout.Horizontal,
        "九宫格 (3×3)" => CollageLayout.Grid3,
        "四宫格 (2×2)" => CollageLayout.Grid2,
        _ => throw new InvalidDataException("拼接方式无效。")
    };

    public static Color ParseColor(string value) => value switch
    {
        "白色" => Colors.White,
        "黑色" => Colors.Black,
        "灰色" => Color.FromRgb(0xF0, 0xF0, 0xF0),
        "透明" => Colors.Transparent,
        _ => throw new InvalidDataException("背景颜色无效。")
    };

    public static CollageResult Build(IReadOnlyList<BitmapSource> images, CollageLayout layout, int gap, Color background, int maxEdge)
    {
        if (images.Count == 0) throw new InvalidDataException("没有可拼接的图片。");
        gap = Math.Clamp(gap, 0, 200);
        maxEdge = Math.Clamp(maxEdge, 500, 12000);

        return layout switch
        {
            CollageLayout.Vertical => Stack(images, vertical: true, gap, background, maxEdge),
            CollageLayout.Horizontal => Stack(images, vertical: false, gap, background, maxEdge),
            CollageLayout.Grid3 => Grid(images, 3, gap, background, maxEdge),
            CollageLayout.Grid2 => Grid(images, 2, gap, background, maxEdge),
            _ => throw new InvalidDataException("拼接方式无效。")
        };
    }

    private static CollageResult Stack(IReadOnlyList<BitmapSource> images, bool vertical, int gap, Color background, int maxEdge)
    {
        var span = vertical
            ? images.Max(image => image.PixelWidth)
            : images.Max(image => image.PixelHeight);
        span = Math.Min(span, maxEdge);

        var scaled = images.Select(image => vertical
            ? ImageSupport.Resize(image, span, Math.Max(1, (int)Math.Round((double)image.PixelHeight * span / image.PixelWidth)))
            : ImageSupport.Resize(image, Math.Max(1, (int)Math.Round((double)image.PixelWidth * span / image.PixelHeight)), span)).ToList();

        var total = scaled.Sum(image => vertical ? image.PixelHeight : image.PixelWidth) + gap * (scaled.Count - 1);
        var width = vertical ? span : total;
        var height = vertical ? total : span;

        var shrink = Math.Min(1.0, (double)maxEdge / Math.Max(width, height));
        shrink = Math.Min(shrink, Math.Sqrt((double)MaxPixels / Math.Max(1L, (long)width * height)));
        if (shrink < 1.0)
        {
            width = Math.Max(1, (int)Math.Round(width * shrink));
            height = Math.Max(1, (int)Math.Round(height * shrink));
            scaled = scaled.Select(image => ImageSupport.Resize(
                image,
                Math.Max(1, (int)Math.Round(image.PixelWidth * shrink)),
                Math.Max(1, (int)Math.Round(image.PixelHeight * shrink)))).ToList();
        }

        var placements = new List<(BitmapSource Image, double X, double Y)>();
        var offset = 0.0;
        foreach (var image in scaled)
        {
            var x = vertical ? (width - image.PixelWidth) / 2.0 : offset;
            var y = vertical ? offset : (height - image.PixelHeight) / 2.0;
            placements.Add((image, x, y));
            offset += (vertical ? image.PixelHeight : image.PixelWidth) + gap;
        }

        return new CollageResult(Paint(width, height, background, placements), vertical ? 1 : scaled.Count, vertical ? scaled.Count : 1);
    }

    private static CollageResult Grid(IReadOnlyList<BitmapSource> images, int columns, int gap, Color background, int maxEdge)
    {
        var rows = (int)Math.Ceiling(images.Count / (double)columns);
        var cell = images.Max(image => Math.Max(image.PixelWidth, image.PixelHeight));
        var available = Math.Max(1, maxEdge - gap * (columns - 1));
        cell = Math.Min(cell, available / columns);
        var rowAvailable = Math.Max(1, maxEdge - gap * (rows - 1));
        cell = Math.Min(cell, rowAvailable / rows);
        cell = Math.Max(1, cell);

        var width = cell * columns + gap * (columns - 1);
        var height = cell * rows + gap * (rows - 1);
        if ((long)width * height > MaxPixels)
        {
            var shrink = Math.Sqrt((double)MaxPixels / ((long)width * height));
            cell = Math.Max(1, (int)Math.Round(cell * shrink));
            width = cell * columns + gap * (columns - 1);
            height = cell * rows + gap * (rows - 1);
        }

        var placements = new List<(BitmapSource Image, double X, double Y)>();
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            var scale = Math.Min((double)cell / image.PixelWidth, (double)cell / image.PixelHeight);
            var fitted = ImageSupport.Resize(image, Math.Max(1, (int)Math.Round(image.PixelWidth * scale)), Math.Max(1, (int)Math.Round(image.PixelHeight * scale)));
            var column = index % columns;
            var row = index / columns;
            var x = column * (cell + gap) + (cell - fitted.PixelWidth) / 2.0;
            var y = row * (cell + gap) + (cell - fitted.PixelHeight) / 2.0;
            placements.Add((fitted, x, y));
        }

        return new CollageResult(Paint(width, height, background, placements), columns, rows);
    }

    private static BitmapSource Paint(int width, int height, Color background, List<(BitmapSource Image, double X, double Y)> placements) =>
        ImageSupport.Compose(width, height, 96, dc =>
        {
            if (background != Colors.Transparent)
                dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
            foreach (var (image, x, y) in placements)
                dc.DrawImage(image, new Rect(x, y, image.PixelWidth, image.PixelHeight));
        });
}

public static class Program
{
    public static async Task<int> Main() => await PluginHost.RunAsync(HandleAsync);

    private static Task<WorkerEvent> HandleAsync(PluginContext ctx) =>
        Task.FromResult(ImageSupport.RunSta(() => Handle(ctx)));

    private static WorkerEvent Handle(PluginContext ctx)
    {
        if (ctx.Paths.Length < 2) throw new InvalidDataException("至少选中两张图片才能拼接。");

        var layout = CollageBuilder.ParseLayout(ctx.Param("layout", "竖向长图"));
        var background = CollageBuilder.ParseColor(ctx.Param("background", "白色"));
        var gap = ctx.IntParam("gap", 8);
        var maxEdge = ctx.IntParam("maxEdge", 4000);
        var outputFolder = ctx.Param("outputFolder", "");

        var frames = new List<BitmapSource>();
        foreach (var path in ctx.Paths)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            if (!ImageSupport.IsSupported(path)) throw new InvalidDataException($"不支持的图片格式：{Path.GetFileName(path)}");
            frames.Add(ImageSupport.Flatten(ImageSupport.Load(path)));
        }

        var result = CollageBuilder.Build(frames, layout, gap, background, maxEdge);
        var extension = background == Colors.Transparent ? ".png" : ".jpg";
        var output = PluginContext.UniqueOutput(ctx.Paths[0], "_collage", extension, outputFolder);
        var bytes = extension == ".png" ? ImageSupport.EncodePng(result.Image) : ImageSupport.EncodeJpeg(result.Image, 92);
        ImageSupport.WriteBytes(output, bytes);

        var row = new ResultRow(
            $"{ctx.Paths.Length} 张图片",
            output,
            "已拼接",
            $"{result.Image.PixelWidth}×{result.Image.PixelHeight}，{result.Columns}×{result.Rows} 布局，{ImageSupport.FormatBytes(bytes.LongLength)}");
        return PluginContext.Result($"已生成拼接图 {Path.GetFileName(output)}。", [row], [output]);
    }
}
