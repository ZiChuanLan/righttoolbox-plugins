using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shrink = ImageShrink.Program;
using RightToolbox.Images;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

ImageSupport.RunSta(() =>
{
    // 造一张带渐变与文字的图，压缩后必须有明显体积下降。
    var source = ImageSupport.Compose(1200, 900, 96, dc =>
    {
        dc.DrawRectangle(new LinearGradientBrush(Colors.CornflowerBlue, Colors.OrangeRed, 45), null, new Rect(0, 0, 1200, 900));
        dc.DrawText(ImageSupport.Text("RightToolbox 压缩测试", 64, Colors.White, bold: true), new Point(40, 400));
    });

    var big = ImageSupport.EncodePng(source);
    Assert(big.Length > 10_000, $"fixture too small: {big.Length}");

    var target = 20L * 1024;
    var squeezed = Shrink.Squeeze(ImageSupport.Flatten(source), target);
    Assert(squeezed.Bytes.LongLength <= target, $"expected <= {target} bytes, got {squeezed.Bytes.LongLength}");
    Assert(squeezed.Quality is >= 25 and <= 92, $"quality out of range: {squeezed.Quality}");
    Assert(squeezed.Width >= 1 && squeezed.Height >= 1, "invalid output size");

    using (var stream = new MemoryStream(squeezed.Bytes))
    {
        var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert(decoded.PixelWidth == squeezed.Width && decoded.PixelHeight == squeezed.Height,
            $"decoded {decoded.PixelWidth}x{decoded.PixelHeight} != reported {squeezed.Width}x{squeezed.Height}");
    }

    // 目标极小时必须靠缩尺寸达成，而不是无限降质量。
    var tiny = Shrink.Squeeze(ImageSupport.Flatten(source), 3L * 1024);
    Assert(tiny.Bytes.LongLength <= 3L * 1024, "tiny target not reached");
    Assert(tiny.Width < 1200, "should downscale for a tiny target");

    // 已经够小的图不应该被放大。
    var small = ImageSupport.Resize(source, 320, 240);
    var passthrough = Shrink.Squeeze(small, 500L * 1024);
    Assert(passthrough.Width == 320 && passthrough.Height == 240, "must not upscale");

    Assert(Shrink.TargetKb("200 KB", 0) == 200, "preset mapping failed");
    Assert(Shrink.TargetKb("自定义", 650) == 650, "custom mapping failed");
    Assert(Shrink.TargetKb("自定义", 999_999) == 20480, "custom clamp failed");
    Assert(Shrink.MaxEdge("不限制") == 0 && Shrink.MaxEdge("1280 px") == 1280, "max edge mapping failed");
    try { Shrink.TargetKb("bogus", 0); throw new Exception("bogus target should throw"); }
    catch (InvalidDataException) { }

    var fitted = ImageSupport.FitWithin(source, 600);
    Assert(Math.Max(fitted.PixelWidth, fitted.PixelHeight) == 600, "FitWithin did not clamp the long edge");
    Assert(ImageSupport.HammingDistance(0b1010UL, 0b0011UL) == 2, "hamming distance wrong");

    Console.WriteLine("PASS image-shrink unit tests");
    return 0;
});
