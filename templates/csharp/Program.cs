using RightToolbox.PluginSdk;

return await PluginHost.RunAsync(async ctx =>
{
    var rows = new List<ResultRow>(ctx.Paths.Length);
    for (var i = 0; i < ctx.Paths.Length; i++)
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        var path = ctx.Paths[i];
        var detail = Directory.Exists(path) ? "文件夹" : $"{new FileInfo(path).Length:N0} B";
        rows.Add(new ResultRow(Path.GetFileName(path), path, "已处理", detail));
        ctx.ReportProgress(i + 1, ctx.Paths.Length, Path.GetFileName(path));
    }

    await Task.CompletedTask;
    return PluginContext.Result($"已处理 {rows.Count} 项", rows.ToArray());
});
