using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orient = ImageOrient.OrientBuilder;
using RightToolbox.Images;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

ImageSupport.RunSta(() =>
{
    // 4x2：左上角红，其余蓝
    var source = ImageSupport.Compose(4, 2, 96, dc =>
    {
        dc.DrawRectangle(Brushes.Blue, null, new Rect(0, 0, 4, 2));
        dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 1, 1));
    });

    Color PixelAt(BitmapSource bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var converted = ImageSupport.Flatten(bitmap);
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, 4, 0);
        return Color.FromArgb(buffer[3], buffer[2], buffer[1], buffer[0]);
    }

    Assert(PixelAt(source, 0, 0) == Colors.Red, "fixture top-left should be red");

    var clockwise = Orient.Rotate(source, 90);
    Assert(clockwise.PixelWidth == 2 && clockwise.PixelHeight == 4, $"clockwise size {clockwise.PixelWidth}x{clockwise.PixelHeight}");
    Assert(PixelAt(clockwise, 1, 0) == Colors.Red, "clockwise should move top-left to top-right");

    var counter = Orient.Rotate(source, 270);
    Assert(counter.PixelWidth == 2 && counter.PixelHeight == 4, "counter-clockwise size wrong");
    Assert(PixelAt(counter, 0, 3) == Colors.Red, "counter-clockwise should move top-left to bottom-left");

    var upsideDown = Orient.Rotate(source, 180);
    Assert(upsideDown.PixelWidth == 4 && upsideDown.PixelHeight == 2, "180 size wrong");
    Assert(PixelAt(upsideDown, 3, 1) == Colors.Red, "180 should move top-left to bottom-right");

    var mirrored = Orient.Mirror(source, horizontal: true);
    Assert(PixelAt(mirrored, 3, 0) == Colors.Red, "horizontal mirror should move red to the right");
    var flipped = Orient.Mirror(source, horizontal: false);
    Assert(PixelAt(flipped, 0, 1) == Colors.Red, "vertical mirror should move red to the bottom");

    Assert(Orient.ExifTransform(1) == (0, false, false), "orientation 1 wrong");
    Assert(Orient.ExifTransform(3) == (180, false, false), "orientation 3 wrong");
    Assert(Orient.ExifTransform(6) == (90, false, false), "orientation 6 wrong");
    Assert(Orient.ExifTransform(8) == (270, false, false), "orientation 8 wrong");
    Assert(Orient.ExifTransform(2) == (0, true, false), "orientation 2 wrong");
    Assert(Orient.ExifTransform(5) == (90, true, false), "orientation 5 wrong");

    Assert(Orient.ParseManualRotation("顺时针旋转 90°") == 90, "manual rotation parse wrong");
    Assert(Orient.ParseManualRotation("自动（按 EXIF 修正）") == -1, "auto parse wrong");
    try { Orient.ParseManualRotation("bogus"); throw new Exception("bogus rotation should throw"); }
    catch (InvalidDataException) { }
    Assert(Orient.ParseFlip("水平翻转（左右镜像）") == (true, false), "flip parse wrong");

    // 无 EXIF 的文件必须回落到 1，不能抛异常
    var root = Path.Combine(Path.GetTempPath(), "rt-orient-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var plain = Path.Combine(root, "plain.png");
        ImageSupport.WriteBytes(plain, ImageSupport.EncodePng(source));
        Assert(Orient.ReadOrientation(plain) == 1, "png without EXIF should report orientation 1");
        Assert(Orient.ReadOrientation(Path.Combine(root, "missing.png")) == 1, "missing file should report orientation 1");
        File.WriteAllText(Path.Combine(root, "broken.jpg"), "not an image");
        Assert(Orient.ReadOrientation(Path.Combine(root, "broken.jpg")) == 1, "unreadable file should report orientation 1");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    Console.WriteLine("PASS image-orient unit tests");
    return 0;
});
