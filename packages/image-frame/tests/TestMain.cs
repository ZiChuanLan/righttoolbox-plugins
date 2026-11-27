using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Frame = ImageFrame.FrameBuilder;
using RightToolbox.Images;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

ImageSupport.RunSta(() =>
{
    var source = ImageSupport.Compose(400, 300, 96, dc => dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 400, 300)));

    Color PixelAt(BitmapSource bitmap, int x, int y)
    {
        var buffer = new byte[4];
        ImageSupport.Flatten(bitmap).CopyPixels(new Int32Rect(x, y, 1, 1), buffer, 4, 0);
        return Color.FromArgb(buffer[3], buffer[2], buffer[1], buffer[0]);
    }

    // 白边：长边 400 × 10% = 40
    var bordered = Frame.Apply(source, "白边（打印留白）", 10, 8, Colors.White);
    Assert(bordered.PixelWidth == 480 && bordered.PixelHeight == 380, $"white border size {bordered.PixelWidth}x{bordered.PixelHeight}");
    Assert(PixelAt(bordered, 0, 0) == Colors.White, "corner should be the border colour");
    var inner = PixelAt(bordered, 45, 45);
    Assert(inner.A == 255 && Math.Abs(inner.R - 0x46) <= 3 && Math.Abs(inner.G - 0x82) <= 3 && Math.Abs(inner.B - 0xB4) <= 3,
        $"inner area should keep the photo, got {inner}");
    Assert(Frame.BorderPixels(source, "白边（打印留白）", 10) == 40, "border percent math wrong");
    Assert(Frame.BorderPixels(source, "白边（打印留白）", 200) == 80, "border percent must clamp to 20%");

    // 拍立得：底部三倍
    var polaroid = Frame.Apply(source, "拍立得边框", 10, 8, Colors.White);
    Assert(polaroid.PixelWidth == 480 && polaroid.PixelHeight == 460, $"polaroid size {polaroid.PixelWidth}x{polaroid.PixelHeight}");
    Assert(PixelAt(polaroid, 240, 455) == Colors.White, "polaroid bottom band missing");

    // 细黑边
    var thin = Frame.Apply(source, "细黑边", 10, 8, Colors.White);
    Assert(thin.PixelWidth == 420 && thin.PixelHeight == 320, $"thin border size {thin.PixelWidth}x{thin.PixelHeight}");
    Assert(PixelAt(thin, 0, 0) == Colors.Black, "thin border must be black even when colour says white");

    // 圆角：四角透明，中心不透明
    var rounded = Frame.Apply(source, "圆角（透明）", 4, 20, Colors.White);
    Assert(rounded.PixelWidth == 400 && rounded.PixelHeight == 300, "rounded must keep the original size");
    Assert(PixelAt(rounded, 0, 0).A == 0, $"rounded corner should be transparent, alpha={PixelAt(rounded, 0, 0).A}");
    Assert(PixelAt(rounded, 200, 150).A == 255, "rounded center must stay opaque");
    Assert(Frame.ProducesTransparency("圆角（透明）") && !Frame.ProducesTransparency("白边（打印留白）"), "transparency detection wrong");

    var png = ImageSupport.EncodePng(rounded);
    Assert(png.Length > 1000, "rounded PNG encode failed");
    var jpeg = ImageSupport.EncodeJpeg(bordered, 95);
    Assert(jpeg[0] == 0xFF && jpeg[1] == 0xD8, "jpeg encode failed");

    Assert(Frame.ParseColor("米色") == Color.FromRgb(0xF7, 0xF3, 0xE8), "colour parsing wrong");
    try { Frame.ParseColor("bogus"); throw new Exception("bogus colour should throw"); }
    catch (InvalidDataException) { }
    try { Frame.Apply(source, "bogus", 4, 8, Colors.White); throw new Exception("bogus style should throw"); }
    catch (InvalidDataException) { }

    Console.WriteLine("PASS image-frame unit tests");
    return 0;
});
