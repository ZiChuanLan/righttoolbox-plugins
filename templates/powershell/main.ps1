$sdkLocal = Join-Path $PSScriptRoot "sdk\RightToolbox.Plugin.psm1"
$sdkRepo  = Join-Path $PSScriptRoot "..\..\sdk\powershell\RightToolbox.Plugin.psm1"
$sdkPath  = if (Test-Path -LiteralPath $sdkLocal) { $sdkLocal } else { $sdkRepo }
Import-Module $sdkPath -Force

Invoke-RightToolboxPlugin -Handler {
    param($ctx)

    $rows = New-Object System.Collections.Generic.List[object]
    $total = $ctx.Paths.Count
    for ($i = 0; $i -lt $total; $i++) {
        if (Test-RtCancelled $ctx) { throw "操作已取消。" }
        $path = $ctx.Paths[$i]
        $detail = if (Test-Path -LiteralPath $path -PathType Container) { "文件夹" } else { "$((Get-Item -LiteralPath $path).Length) B" }
        $rows.Add((New-RtRow -Source ([System.IO.Path]::GetFileName($path)) -Target $path -Status "已处理" -Detail $detail))
        Write-RtProgress -Current ($i + 1) -Total $total -Message ([System.IO.Path]::GetFileName($path))
    }

    return New-RtResult -Message "已处理 $($rows.Count) 项" -Rows $rows.ToArray()
}
