using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Collage = ImageCollage.CollageBuilder;
using RightToolbox.Images;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

ImageSupport.RunSta(() =>
{
    BitmapSource Box(int width, int height, Color color) =>
        ImageSupport.Compose(width, height, 96, dc => dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height)));

    var images = new List<BitmapSource>
    {
        Box(400, 300, Colors.CornflowerBlue),
        Box(200, 500, Colors.OrangeRed),
        Box(320, 320, Colors.SeaGreen)
    };

    var vertical = Collage.Build(images, ImageCollage.CollageLayout.Vertical, 8, Colors.White, 4000);
    Assert(vertical.Image.PixelWidth == 400, $"vertical width {vertical.Image.PixelWidth} should be the widest image");
    var expectedHeight = 300 + 8 + (int)Math.Round(500 * 400.0 / 200) + 8 + 400 * 320 / 320;
    Assert(Math.Abs(vertical.Image.PixelHeight - (300 + 8 + 1000 + 8 + 400)) <= 2, $"vertical height {vertical.Image.PixelHeight} vs expected ~{300 + 8 + 1000 + 8 + 400} (calc {expectedHeight})");
    Assert(vertical.Rows == 3 && vertical.Columns == 1, "vertical layout counters wrong");

    var horizontal = Collage.Build(images, ImageCollage.CollageLayout.Horizontal, 0, Colors.Black, 4000);
    Assert(horizontal.Image.PixelHeight == 500, $"horizontal height {horizontal.Image.PixelHeight} should be the tallest image");

    var grid = Collage.Build(images, ImageCollage.CollageLayout.Grid2, 10, Colors.White, 4000);
    Assert(grid.Columns == 2 && grid.Rows == 2, $"grid counters wrong: {grid.Columns}x{grid.Rows}");
    Assert(grid.Image.PixelWidth == grid.Image.PixelHeight, "2x2 grid should be square");

    // 最长边限制必须真的生效
    var limited = Collage.Build(images, ImageCollage.CollageLayout.Vertical, 8, Colors.White, 600);
    Assert(Math.Max(limited.Image.PixelWidth, limited.Image.PixelHeight) <= 600, $"max edge not respected: {limited.Image.PixelWidth}x{limited.Image.PixelHeight}");

    // 透明背景走 PNG 编码
    var transparent = Collage.Build(images, ImageCollage.CollageLayout.Grid3, 6, Colors.Transparent, 2000);
    var png = ImageSupport.EncodePng(transparent.Image);
    Assert(png.Length > 1000 && png[0] == 0x89 && png[1] == 0x50, "transparent collage should encode as a real PNG");
    var jpeg = ImageSupport.EncodeJpeg(vertical.Image, 92);
    Assert(jpeg[0] == 0xFF && jpeg[1] == 0xD8, "jpeg signature missing");

    Assert(Collage.ParseLayout("九宫格 (3×3)") == ImageCollage.CollageLayout.Grid3, "layout parsing failed");
    Assert(Collage.ParseColor("透明") == Colors.Transparent, "color parsing failed");
    try { Collage.ParseLayout("bogus"); throw new Exception("bogus layout should throw"); }
    catch (InvalidDataException) { }
    try { Collage.Build([], ImageCollage.CollageLayout.Vertical, 8, Colors.White, 4000); throw new Exception("empty input should throw"); }
    catch (InvalidDataException) { }

    Console.WriteLine("PASS image-collage unit tests");
    return 0;
});
