using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mark = ImageWatermark.WatermarkBuilder;
using RightToolbox.Images;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

ImageSupport.RunSta(() =>
{
    BitmapSource Box(int width, int height) =>
        ImageSupport.Compose(width, height, 96, dc => dc.DrawRectangle(Brushes.LightSteelBlue, null, new Rect(0, 0, width, height)));

    var source = Box(800, 600);
    var plain = ImageSupport.EncodeJpeg(source, 92);
    var marked = ImageSupport.EncodeJpeg(Mark.Apply(source, "测试水印", "右下角", 40, 0.5, Colors.Red, 24), 92);
    Assert(!plain.SequenceEqual(marked), "watermark did not change any pixel");

    var markedImage = Mark.Apply(source, "测试水印", "右下角", 40, 0.5, Colors.Red, 24);
    Assert(markedImage.PixelWidth == 800 && markedImage.PixelHeight == 600, "watermark must keep the canvas size");

    foreach (var position in new[] { "右下角", "左下角", "右上角", "左上角", "居中", "平铺" })
    {
        var bytes = ImageSupport.EncodeJpeg(Mark.Apply(source, "W", position, 30, 0.6, Colors.White, 16), 80);
        Assert(bytes.Length > 500, $"position {position} produced nothing");
        Assert(!bytes.SequenceEqual(plain), $"position {position} changed nothing");
    }

    var (left, top) = Mark.Anchor("右下角", 800, 600, 100, 40, 20);
    Assert(left == 680 && top == 540, $"bottom-right anchor wrong: {left},{top}");
    var (centerX, centerY) = Mark.Anchor("居中", 800, 600, 100, 40, 0);
    Assert(centerX == 350 && centerY == 280, $"center anchor wrong: {centerX},{centerY}");
    try { Mark.Anchor("bogus", 800, 600, 10, 10, 0); throw new Exception("bogus position should throw"); }
    catch (InvalidDataException) { }

    Assert(Mark.IsTiled("平铺") && !Mark.IsTiled("居中"), "tile detection wrong");
    Assert(Mark.ParseColor("红色") == Color.FromRgb(0xD9, 0x30, 0x25), "color parsing wrong");

    // 小图不会被打上超大字号：100px 宽 + 200 号字，结果仍应可编码。
    var small = Mark.Apply(Box(100, 80), "XXL", "居中", 200, 1.0, Colors.Black, 4);
    var smallBytes = ImageSupport.EncodeJpeg(small, 80);
    Assert(small.PixelWidth == 100 && smallBytes.Length > 200, "small image watermark failed");

    try { Mark.Apply(source, "  ", "居中", 20, 0.5, Colors.White, 0); throw new Exception("empty text should throw"); }
    catch (InvalidDataException) { }

    Console.WriteLine("PASS image-watermark unit tests");
    return 0;
});
