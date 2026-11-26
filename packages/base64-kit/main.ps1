$sdkLocal = Join-Path $PSScriptRoot "sdk\RightToolbox.Plugin.psm1"
$sdkRepo  = Join-Path $PSScriptRoot "..\..\sdk\powershell\RightToolbox.Plugin.psm1"
$sdkPath  = if (Test-Path -LiteralPath $sdkLocal) { $sdkLocal } else { $sdkRepo }
Import-Module $sdkPath -Force

$mimeByExt = @{
    ".png"  = "image/png"
    ".jpg"  = "image/jpeg"
    ".jpeg" = "image/jpeg"
    ".gif"  = "image/gif"
    ".webp" = "image/webp"
    ".svg"  = "image/svg+xml"
    ".ico"  = "image/x-icon"
    ".bmp"  = "image/bmp"
    ".pdf"  = "application/pdf"
    ".json" = "application/json"
    ".txt"  = "text/plain"
    ".html" = "text/html"
    ".css"  = "text/css"
    ".js"   = "application/javascript"
    ".woff2"= "font/woff2"
}

$extByMime = @{
    "image/png"               = ".png"
    "image/jpeg"              = ".jpg"
    "image/gif"               = ".gif"
    "image/webp"              = ".webp"
    "image/svg+xml"           = ".svg"
    "image/x-icon"            = ".ico"
    "image/vnd.microsoft.icon"= ".ico"
    "image/bmp"               = ".bmp"
    "application/pdf"         = ".pdf"
    "application/json"        = ".json"
    "text/plain"              = ".txt"
    "text/html"               = ".html"
    "text/css"                = ".css"
}

Invoke-RightToolboxPlugin -Handler {
    param($ctx)

    $rows = New-Object System.Collections.Generic.List[object]
    $outputs = New-Object System.Collections.Generic.List[string]
    $total = $ctx.Paths.Count

    if ($ctx.CommandId -eq "community.base64-kit.encode") {
        $outputMode = Get-RtParam $ctx "outputMode" "Data URI (含 MIME)"
        $saveToFile = (Get-RtParam $ctx "saveToFile" "false") -eq "true"
        $outputFolder = Get-RtParam $ctx "outputFolder" ""
        $clipParts = New-Object System.Collections.Generic.List[string]
        $maxBytes = 10 * 1024 * 1024

        for ($i = 0; $i -lt $total; $i++) {
            if (Test-RtCancelled $ctx) { throw "操作已取消。" }
            $path = $ctx.Paths[$i]
            $info = Get-Item -LiteralPath $path
            if ($info.Length -gt $maxBytes) {
                throw "文件过大（超过 10 MB Base64 编码上限）：$($info.Name)"
            }
            $bytes = [System.IO.File]::ReadAllBytes($path)
            $b64 = [System.Convert]::ToBase64String($bytes)
            $ext = $info.Extension.ToLowerInvariant()
            $mime = if ($mimeByExt.ContainsKey($ext)) { $mimeByExt[$ext] } else { "application/octet-stream" }
            $encoded = if ($outputMode -eq "Data URI (含 MIME)") { "data:$mime;base64,$b64" } else { $b64 }

            $clipParts.Add($encoded)
            $targetLabel = "已复制到剪贴板"
            if ($saveToFile) {
                $outPath = Get-RtUniqueOutput -SourcePath $path -Suffix ".b64" -Extension ".txt" -OutputFolder $outputFolder
                [System.IO.File]::WriteAllText($outPath, $encoded, [System.Text.UTF8Encoding]::new($false))
                $outputs.Add($outPath)
                $targetLabel = $outPath
            }
            $rows.Add((New-RtRow -Source $info.Name -Target $targetLabel -Status "已编码" -Detail ("{0:N0} → {1:N0} 字符" -f $bytes.Length, $encoded.Length)))
            Write-RtProgress -Current ($i + 1) -Total $total -Message $info.Name
        }

        $clipText = [string]::Join([Environment]::NewLine, $clipParts)
        return New-RtResult -Message "已编码 $total 个文件并写入剪贴板" -Rows $rows.ToArray() -Outputs $outputs.ToArray() -ClipboardText $clipText
    }
    elseif ($ctx.CommandId -eq "community.base64-kit.decode") {
        $extParam = (Get-RtParam $ctx "extension" "").Trim()
        $outputFolder = Get-RtParam $ctx "outputFolder" ""

        for ($i = 0; $i -lt $total; $i++) {
            if (Test-RtCancelled $ctx) { throw "操作已取消。" }
            $path = $ctx.Paths[$i]
            $raw = ([System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)).Trim()
            $detectedExt = ".bin"
            if ($raw -match '^(?s)data:([^;,]+)(?:;charset=[^;,]+)?;base64,(.+)$') {
                $mime = $Matches[1].ToLowerInvariant()
                $raw = $Matches[2]
                if ($extByMime.ContainsKey($mime)) { $detectedExt = $extByMime[$mime] }
            }
            $cleanB64 = ($raw -replace '\s+', '')
            if ([string]::IsNullOrEmpty($cleanB64)) {
                throw "文件不含有效的 Base64 数据：$([System.IO.Path]::GetFileName($path))"
            }
            $bytes = [System.Convert]::FromBase64String($cleanB64)
            $finalExt = if (-not [string]::IsNullOrEmpty($extParam)) {
                if ($extParam.StartsWith(".")) { $extParam } else { ".$extParam" }
            } else {
                $detectedExt
            }
            $outPath = Get-RtUniqueOutput -SourcePath $path -Suffix "_decoded" -Extension $finalExt -OutputFolder $outputFolder
            [System.IO.File]::WriteAllBytes($outPath, $bytes)
            $outputs.Add($outPath)
            $rows.Add((New-RtRow -Source ([System.IO.Path]::GetFileName($path)) -Target $outPath -Status "已还原" -Detail ("{0:N0} B" -f $bytes.Length)))
            Write-RtProgress -Current ($i + 1) -Total $total -Message ([System.IO.Path]::GetFileName($path))
        }

        return New-RtResult -Message "已还原 $($outputs.Count) 个文件（原件已保留）" -Rows $rows.ToArray() -Outputs $outputs.ToArray()
    }
    else {
        throw "未知命令：$($ctx.CommandId)"
    }
}
