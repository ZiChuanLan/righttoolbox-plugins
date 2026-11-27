using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Photo = IdPhoto.IdPhotoBuilder;
using RightToolbox.Images;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

ImageSupport.RunSta(() =>
{
    BitmapSource Box(int width, int height, Color color) =>
        ImageSupport.Compose(width, height, 96, dc => dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height)));

    var (width, height) = Photo.ParseSpec("一寸 (295×413)", 0, 0);
    Assert(width == 295 && height == 413, $"spec mapping wrong: {width}x{height}");
    var (customWidth, customHeight) = Photo.ParseSpec("自定义", 600, 800);
    Assert(customWidth == 600 && customHeight == 800, "custom spec wrong");
    try { Photo.ParseSpec("bogus", 0, 0); throw new Exception("bogus spec should throw"); }
    catch (InvalidDataException) { }

    var landscape = Box(1200, 600, Colors.SteelBlue);
    var cropped = Photo.Build(landscape, 295, 413, crop: true, Colors.White);
    Assert(cropped.PixelWidth == 295 && cropped.PixelHeight == 413, $"cropped size {cropped.PixelWidth}x{cropped.PixelHeight}");

    var padded = Photo.Build(landscape, 295, 413, crop: false, Color.FromRgb(0x43, 0x86, 0xC6));
    Assert(padded.PixelWidth == 295 && padded.PixelHeight == 413, "padded size wrong");
    var pixels = new byte[4 * 295];
    padded.CopyPixels(new Int32Rect(0, 0, 295, 1), pixels, 4 * 295, 0);
    Assert(pixels[0] == 0xC6 && pixels[1] == 0x86 && pixels[2] == 0x43, $"background not filled: {pixels[0]},{pixels[1]},{pixels[2]}");

    var portrait = Box(600, 1200, Colors.SeaGreen);
    var tall = Photo.Build(portrait, 413, 579, crop: true, Colors.White);
    Assert(tall.PixelWidth == 413 && tall.PixelHeight == 579, "portrait crop size wrong");

    var bytes = ImageSupport.EncodeJpeg(cropped, 95);
    Assert(bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes.Length > 1000, "jpeg output invalid");

    Assert(Photo.ParseColor("蓝色") == Color.FromRgb(0x43, 0x86, 0xC6), "color parsing wrong");

    Console.WriteLine("PASS id-photo unit tests");
    return 0;
});
