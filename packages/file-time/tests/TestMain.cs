using FileTime;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

Assert(TimePlanner.TryParseFilenameDate("IMG_20260501_143022.jpg", out var dt1), "Should parse IMG date");
Assert(dt1.Year == 2026 && dt1.Month == 5 && dt1.Day == 1 && dt1.Hour == 14 && dt1.Minute == 30 && dt1.Second == 22, "Mismatch IMG dt");

Assert(TimePlanner.TryParseFilenameDate("report-2025-11-09.pdf", out var dt2), "Should parse ISO date");
Assert(dt2.Year == 2025 && dt2.Month == 11 && dt2.Day == 9, "Mismatch ISO date");

var temp = Path.Combine(Path.GetTempPath(), "rt-filetime-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var f = Path.Combine(temp, "photo_20240203_091500.txt");
    File.WriteAllText(f, "test");
    var (plan, _) = TimePlanner.BuildPreview([f], "从文件名解析日期", "同时更新创建与修改时间", 0, "");
    var rows = TimePlanner.ApplyPlan(plan);
    Assert(rows.Length == 1 && rows[0].Status == "已更新", "Expected apply success");
    var updated = File.GetLastWriteTime(f);
    Assert(updated.Year == 2024 && updated.Month == 2 && updated.Day == 3 && updated.Hour == 9, "Timestamp not updated");
    Console.WriteLine("PASS file-time unit tests");
}
finally
{
    Directory.Delete(temp, true);
}
