using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RightToolbox.PluginSdk;

namespace FileTime;

public sealed record TimePlanItem(
    string Path,
    long SizeBytes,
    long OriginalCreationUtcTicks,
    long OriginalWriteUtcTicks,
    long? TargetCreationUtcTicks,
    long? TargetWriteUtcTicks);

public sealed record TimePlan(TimePlanItem[] Items);

public static partial class TimePlanner
{
    [GeneratedRegex(@"(?<!\d)(20\d{2}|19\d{2})[-_. ]?(0[1-9]|1[0-2])[-_. ]?(0[1-9]|[12]\d|3[01])(?:[-_. T]?([01]\d|2[0-3])[-_.]?([0-5]\d)(?:[-_.]?([0-5]\d))?)?(?!\d)")]
    private static partial Regex FilenameDateRegex();

    public static bool TryParseFilenameDate(string fileName, out DateTime localTime)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var match = FilenameDateRegex().Match(stem);
        if (!match.Success)
        {
            localTime = default;
            return false;
        }

        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var hour = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 12;
        var minute = match.Groups[5].Success ? int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) : 0;
        var second = match.Groups[6].Success ? int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture) : 0;

        try
        {
            localTime = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            localTime = default;
            return false;
        }
    }

    public static DateTime ParseFixedTime(string raw)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new InvalidDataException("请输入固定时间，例如 2026-05-01 12:00:00。");

        string[] formats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd"];
        if (DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            return parsed;

        if (DateTime.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed))
            return parsed;

        throw new InvalidDataException("无法识别固定时间格式，请使用 YYYY-MM-DD HH:mm:ss。");
    }

    public static (TimePlan Plan, ResultRow[] Rows) BuildPreview(
        IReadOnlyList<string> paths,
        string mode,
        string targetField,
        double offsetHours,
        string fixedTimeText)
    {
        DateTime? fixedUtc = mode == "指定固定时间" ? ParseFixedTime(fixedTimeText).ToUniversalTime() : null;
        var updateCreation = targetField is "同时更新创建与修改时间" or "仅更新创建时间";
        var updateWrite = targetField is "同时更新创建与修改时间" or "仅更新最后修改时间";

        var items = new List<TimePlanItem>(paths.Count);
        var rows = new List<ResultRow>(paths.Count);

        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("找不到文件。", path);

            var origCreationUtc = info.CreationTimeUtc;
            var origWriteUtc = info.LastWriteTimeUtc;
            DateTime? nextCreationUtc = null;
            DateTime? nextWriteUtc = null;

            switch (mode)
            {
                case "同步修改时间为创建时间":
                    nextWriteUtc = origCreationUtc;
                    if (updateCreation) nextCreationUtc = origCreationUtc;
                    break;
                case "同步创建时间为修改时间":
                    nextCreationUtc = origWriteUtc;
                    if (updateWrite) nextWriteUtc = origWriteUtc;
                    break;
                case "按小时偏移加减":
                    var delta = TimeSpan.FromHours(offsetHours);
                    if (updateCreation) nextCreationUtc = origCreationUtc.Add(delta);
                    if (updateWrite) nextWriteUtc = origWriteUtc.Add(delta);
                    break;
                case "指定固定时间":
                    if (updateCreation) nextCreationUtc = fixedUtc;
                    if (updateWrite) nextWriteUtc = fixedUtc;
                    break;
                case "从文件名解析日期":
                    if (!TryParseFilenameDate(info.Name, out var parsedLocal))
                    {
                        rows.Add(new ResultRow(info.Name, "未匹配日期", "跳过", origWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                        continue;
                    }
                    var utc = parsedLocal.ToUniversalTime();
                    if (updateCreation) nextCreationUtc = utc;
                    if (updateWrite) nextWriteUtc = utc;
                    break;
                default:
                    throw new InvalidDataException($"未知时间调整模式：{mode}");
            }

            items.Add(new TimePlanItem(
                path,
                info.Length,
                origCreationUtc.Ticks,
                origWriteUtc.Ticks,
                nextCreationUtc?.Ticks,
                nextWriteUtc?.Ticks));

            var targetDesc = (nextWriteUtc ?? nextCreationUtc)!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var origDesc = origWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            rows.Add(new ResultRow(info.Name, targetDesc, "待更新", $"原修改时间：{origDesc}"));
        }

        if (items.Count == 0)
            throw new InvalidDataException("所选文件中没有可更新的项目。");

        return (new TimePlan(items.ToArray()), rows.ToArray());
    }

    public static ResultRow[] ApplyPlan(TimePlan plan)
    {
        var rows = new List<ResultRow>(plan.Items.Length);
        foreach (var item in plan.Items)
        {
            var info = new FileInfo(item.Path);
            if (!info.Exists) throw new FileNotFoundException("文件在预览后被移动或删除。", item.Path);
            if (info.Length != item.SizeBytes || info.LastWriteTimeUtc.Ticks != item.OriginalWriteUtcTicks)
                throw new IOException($"文件在预览后发生外部改动：{info.Name}，请重新预览。");

            if (item.TargetCreationUtcTicks.HasValue)
                File.SetCreationTimeUtc(item.Path, new DateTime(item.TargetCreationUtcTicks.Value, DateTimeKind.Utc));
            if (item.TargetWriteUtcTicks.HasValue)
                File.SetLastWriteTimeUtc(item.Path, new DateTime(item.TargetWriteUtcTicks.Value, DateTimeKind.Utc));

            var finalUtc = new DateTime((item.TargetWriteUtcTicks ?? item.TargetCreationUtcTicks)!.Value, DateTimeKind.Utc);
            rows.Add(new ResultRow(info.Name, finalUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "已更新", item.Path));
        }
        return rows.ToArray();
    }
}

internal static class Program
{
    private static Task<int> Main() => PluginHost.RunAsync(async ctx =>
    {
        await Task.CompletedTask;
        if (ctx.CommandId != "community.file-time.adjust")
            throw new InvalidDataException($"未知命令：{ctx.CommandId}");

        if (ctx.Phase == "preview")
        {
            var mode = ctx.Param("mode", "同步修改时间为创建时间");
            var targetField = ctx.Param("targetField", "同时更新创建与修改时间");
            var offsetHours = ctx.DoubleParam("offsetHours", 0);
            var fixedTime = ctx.Param("fixedTime", "");
            var (plan, rows) = TimePlanner.BuildPreview(ctx.Paths, mode, targetField, offsetHours, fixedTime);
            return PluginContext.Preview($"预览完成：将调整 {plan.Items.Length} 个文件时间属性，核对后点击确认执行。", plan, rows);
        }

        if (ctx.Phase == "execute")
        {
            var plan = ctx.Plan?.Deserialize<TimePlan>(Wire.Json)
                ?? throw new InvalidDataException("请先生成预览并核对时间变更。");
            var rows = TimePlanner.ApplyPlan(plan);
            return PluginContext.Result($"已更新 {rows.Length} 个文件的时间属性", rows);
        }

        throw new InvalidDataException($"不支持的执行阶段：{ctx.Phase}");
    });
}
