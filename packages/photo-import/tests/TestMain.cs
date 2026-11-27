using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoImport;
using ImportProgram = PhotoImport.Program;
using RightToolbox.Images;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

ImageSupport.RunSta(() =>
{
    var root = Path.Combine(Path.GetTempPath(), "rt-import-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "inbox"));
    try
    {
        var taken = new DateTime(2024, 3, 5, 10, 20, 30, DateTimeKind.Local);
        BitmapSource Scene(int width, int height, Color color) =>
            ImageSupport.Compose(width, height, 96, dc => dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height)));

        var png = Path.Combine(root, "inbox", "IMG_0001.png");
        ImageSupport.WriteBytes(png, ImageSupport.EncodePng(Scene(400, 300, Colors.SteelBlue)));
        File.SetLastWriteTime(png, taken);

        var bmp = Path.Combine(root, "inbox", "IMG_0002.bmp");
        ImageSupport.WriteBytes(bmp, ImageSupport.EncodePng(Scene(320, 240, Colors.Orange)));
        File.SetLastWriteTime(bmp, taken);

        File.WriteAllText(Path.Combine(root, "inbox", "readme.txt"), "skip me");

        // 纯函数：命名与归档
        Assert(Importer.BuildFileName(png, taken, "按拍摄日期命名 (20260101_143000)", false) == "20240305_102030.png", "date naming wrong");
        Assert(Importer.BuildFileName(png, taken, "日期前缀 + 原名", false) == "20240305_102030_IMG_0001.png", "prefix naming wrong");
        Assert(Importer.BuildFileName(png, taken, "保留原名", true) == "IMG_0001.jpg", "keep-name naming wrong");
        Assert(Importer.ArchiveDirectory(taken, "按年/月归档 (2026/01)") == Path.Combine("2024", "03"), "year/month archive wrong");
        Assert(Importer.ArchiveDirectory(taken, "按年归档 (2026)") == "2024", "year archive wrong");
        Assert(Importer.ArchiveDirectory(taken, "不归档") == "", "no-archive should be empty");
        try { Importer.BuildFileName(png, taken, "bogus", false); throw new Exception("bogus naming should throw"); }
        catch (InvalidDataException) { }

        Assert(Importer.NeedsConversion(bmp), "bmp should need conversion");
        Assert(!Importer.NeedsConversion(png), "png must not need conversion");

        // 规划：只处理图片，txt 被忽略
        var plan = Importer.Plan([Path.Combine(root, "inbox")], true, "按拍摄日期命名 (20260101_143000)", "按年/月归档 (2026/01)", true, 0, Path.Combine(root, "out"), CancellationToken.None);
        Assert(plan.Count == 2, $"expected 2 planned photos, got {plan.Count}");
        var pngItem = plan.Single(item => item.Source.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        Assert(!pngItem.Reencode, "png should only move");
        Assert(pngItem.Target == Path.Combine(root, "out", "2024", "03", "20240305_102030.png"), $"unexpected target {pngItem.Target}");
        var bmpItem = plan.Single(item => item.Source.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));
        Assert(bmpItem.Reencode, "bmp must be converted");
        Assert(bmpItem.Target.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase), "converted target should be jpg");

        // 执行：移动 + 转换，txt 不受影响
        var (rows, moved, converted) = ImportExecutor.Run(plan, 0, CancellationToken.None);
        Assert(rows.Length == 2 && moved == 1 && converted == 1, $"unexpected outcome moved={moved} converted={converted} rows={rows.Length}");
        Assert(!File.Exists(png) && !File.Exists(bmp), "originals should be moved/converted away");
        Assert(File.Exists(Path.Combine(root, "out", "2024", "03", "20240305_102030.png")), "moved png missing");
        Assert(File.Exists(Path.Combine(root, "out", "2024", "03", "20240305_102030.jpg")), "converted jpg missing");
        Assert(File.Exists(Path.Combine(root, "inbox", "readme.txt")), "non-image file must stay");

        using (var stream = File.OpenRead(Path.Combine(root, "out", "2024", "03", "20240305_102030.jpg")))
        {
            var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert(decoded.PixelWidth == 320 && decoded.PixelHeight == 240, $"converted size wrong: {decoded.PixelWidth}x{decoded.PixelHeight}");
        }

        // 同名冲突：第二次处理同一张图应生成 (1) 而不是覆盖
        var again = Path.Combine(root, "inbox", "IMG_0001.png");
        ImageSupport.WriteBytes(again, ImageSupport.EncodePng(Scene(50, 50, Colors.Green)));
        File.SetLastWriteTime(again, taken);
        var plan2 = Importer.Plan([again], false, "按拍摄日期命名 (20260101_143000)", "按年/月归档 (2026/01)", true, 0, Path.Combine(root, "out"), CancellationToken.None);
        ImportExecutor.Run(plan2, 0, CancellationToken.None);
        Assert(File.Exists(Path.Combine(root, "out", "2024", "03", "20240305_102030 (1).png")), "conflict should be renamed with a suffix");

        // 压缩档位解析
        Assert(ImportProgram.ParseShrinkKb("不压缩") == 0 && ImportProgram.ParseShrinkKb("≤500 KB") == 500, "shrink mapping wrong");
        var squeezed = ImportExecutor.Squeeze(Scene(800, 600, Colors.Navy), 15L * 1024);
        Assert(squeezed.LongLength <= 15L * 1024, $"squeeze exceeded target: {squeezed.LongLength}");

        Console.WriteLine("PASS photo-import unit tests");
        return 0;
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
});
