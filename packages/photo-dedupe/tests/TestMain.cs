using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoDedupe;
using RightToolbox.Images;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

ImageSupport.RunSta(() =>
{
    var root = Path.Combine(Path.GetTempPath(), "rt-dedupe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        BitmapSource Scene(int width, int height) => ImageSupport.Compose(width, height, 96, dc =>
        {
            dc.DrawRectangle(new LinearGradientBrush(Colors.Navy, Colors.Gold, 30), null, new Rect(0, 0, width, height));
            dc.DrawEllipse(Brushes.White, null, new Point(width * 0.3, height * 0.4), width * 0.15, height * 0.2);
        });

        var a = Path.Combine(root, "a.png");
        var b = Path.Combine(root, "b.png");
        var c = Path.Combine(root, "c.png");
        ImageSupport.WriteBytes(a, ImageSupport.EncodePng(Scene(400, 300)));
        ImageSupport.WriteBytes(b, ImageSupport.EncodePng(Scene(200, 150)));  // 同一内容、不同尺寸
        ImageSupport.WriteBytes(c, ImageSupport.EncodePng(ImageSupport.Compose(400, 300, 96,
            dc => dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 400, 300)))));
        File.WriteAllText(Path.Combine(root, "notes.txt"), "ignore me");

        var entries = PhotoScanner.Scan([root], recursive: true, CancellationToken.None);
        Assert(entries.Count == 3, $"expected 3 images, got {entries.Count}");

        var groups = PhotoScanner.Group(entries, 6);
        Assert(groups.Count == 1, $"expected 1 similar group, got {groups.Count}");
        Assert(groups[0].Count == 2, $"expected 2 similar photos, got {groups[0].Count}");

        var plan = PhotoScanner.Plan(groups);
        Assert(plan.Count == 1 && plan[0].Duplicates.Count == 1, "plan shape wrong");
        Assert(plan[0].Keep == a, $"should keep the larger original, kept {Path.GetFileName(plan[0].Keep)}");

        var strictGroups = PhotoScanner.Group(entries, 0);
        Assert(strictGroups.Count == 1, $"identical content should still group at threshold 0, got {strictGroups.Count}");
        Assert(strictGroups[0].All(entry => Path.GetFileName(entry.Path) != "c.png"), "unrelated photo must not be grouped");

        var (rows, moved, skipped) = DedupeExecutor.Move(plan, CancellationToken.None);
        Assert(moved == 1 && skipped == 0, $"expected 1 moved, got {moved}/{skipped}");
        Assert(rows.Length == 1 && rows[0].Status == "已移出", "result row wrong");
        Assert(File.Exists(a), "kept file must stay");
        Assert(File.Exists(c), "unrelated file must stay");
        Assert(File.Exists(Path.Combine(root, "_duplicates", "b.png")), "duplicate should be moved into _duplicates");

        var target = DedupeExecutor.UniqueTarget(Path.Combine(root, "_duplicates"), "b.png");
        Assert(Path.GetFileName(target) == "b (1).png", $"unique target wrong: {target}");

        Console.WriteLine("PASS photo-dedupe unit tests");
        return 0;
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
});
