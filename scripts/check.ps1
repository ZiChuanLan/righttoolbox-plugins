param(
    [string]$Only = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$packagesRoot = Join-Path $root 'packages'
$registryPath = Join-Path $root 'registry.json'

if (-not (Test-Path -LiteralPath $registryPath)) { throw "缺少 registry.json" }
$registry = Get-Content -LiteralPath $registryPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($registry.version -ne 1) { throw "registry.json version 必须为 1" }

$seenIds = @{}
$seenCmds = @{}
$registeredIds = @{}
foreach ($item in $registry.plugins) {
    if ($item.id -notmatch '^[a-z][a-z0-9_.-]{2,79}$') { throw "registry.json 插件 ID 无效：$($item.id)" }
    if ($item.id.StartsWith('core.')) { throw "社区插件不得使用 core. 前缀：$($item.id)" }
    if ($registeredIds.ContainsKey($item.id)) { throw "registry.json 存在重复插件 ID：$($item.id)" }
    $registeredIds[$item.id] = $item
    $pkgPath = Join-Path $root ($item.package -replace '/', '\')
    if (-not (Test-Path -LiteralPath (Join-Path $pkgPath 'plugin.json'))) {
        throw "registry.json 条目 $($item.id) 指向缺失的插件目录：$($item.package)"
    }
}

$allowedPermissions = @('fs.read', 'fs.write', 'clipboard.write', 'network', 'process.inspect', 'process.spawn')
$allowedParamTypes = @('text', 'number', 'select', 'toggle', 'folder', 'password')
$allowedKinds = @('files', 'folders', 'any')
$allowedExts = @('.exe', '.com', '.ps1', '.py', '.mjs', '.js')

$failed = 0
$tempBase = Join-Path $root '.test-work\check'
if (Test-Path -LiteralPath $tempBase) { Remove-Item -LiteralPath $tempBase -Recurse -Force }
New-Item -ItemType Directory -Path $tempBase -Force | Out-Null

function Assert-NoReparse([string]$dir) {
    $rootAttr = (Get-Item -LiteralPath $dir -Force).Attributes
    if ($rootAttr -band [System.IO.FileAttributes]::ReparsePoint) { throw "目录包含符号链接或联接：$dir" }
    Get-ChildItem -LiteralPath $dir -Recurse -Force | Where-Object { $_.FullName -notmatch '\\(bin|obj|__pycache__|node_modules)\\' } | ForEach-Object {
        if ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "包含符号链接或联接：$($_.FullName)"
        }
    }
}

function Invoke-SmokeHandshake([string]$pkgDir, $manifest, [string]$entryFullPath) {
    $cmd = $manifest.commands[0]
    $work = Join-Path $tempBase ($manifest.id -replace '[^a-zA-Z0-9._-]', '_')
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    $samplePaths = @()
    $kind = if ($cmd.selection -and $cmd.selection.kind) { $cmd.selection.kind } else { 'any' }
    $minimum = 1
    if ($cmd.selection -and $null -ne $cmd.selection.min) { $minimum = [Math]::Max(1, [int]$cmd.selection.min) }
    $maximum = 3
    if ($cmd.selection -and $null -ne $cmd.selection.max) { $maximum = [Math]::Max(1, [int]$cmd.selection.max) }
    $sampleCount = [Math]::Min([Math]::Max($minimum, 2), [Math]::Min($maximum, 3))

    # 图片夹具：check 不依赖外部图片，用一张 1x1 PNG；WPF 按内容嗅探格式，扩展名只影响插件的选择逻辑。
    $imageExts = @('.png', '.jpg', '.jpeg', '.bmp', '.gif', '.tif', '.tiff', '.ico', '.webp', '.heic', '.heif')
    $tinyPng = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8AAAwAB/AL+2wAAAABJRU5ErkJggg==')
    $defaultText = "Hello RightToolbox`n测试中文 🚀`nHello RightToolbox`n"

    $script:minimalPdf = $null
    function New-MinimalPdfBytes {
        if ($null -ne $script:minimalPdf) { return $script:minimalPdf }
        $objects = @(
            "1 0 obj`r`n<< /Type /Catalog /Pages 2 0 R >>`r`nendobj`r`n",
            "2 0 obj`r`n<< /Type /Pages /Kids [3 0 R] /Count 1 >>`r`nendobj`r`n",
            "3 0 obj`r`n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>`r`nendobj`r`n"
        )
        $body = "%PDF-1.4`r`n"
        $offsets = @()
        foreach ($object in $objects) { $offsets += $body.Length; $body += $object }
        $startxref = $body.Length
        $xref = "xref`r`n0 4`r`n0000000000 65535 f`r`n"
        foreach ($offset in $offsets) { $xref += ("{0:D10} 00000 n`r`n" -f $offset) }
        $body += $xref + "trailer`r`n<< /Size 4 /Root 1 0 R >>`r`nstartxref`r`n$startxref`r`n%%EOF`r`n"
        $script:minimalPdf = [System.Text.Encoding]::ASCII.GetBytes($body)
        return $script:minimalPdf
    }

    function New-SmokeFile([string]$relative, [string]$text) {
        $separator = [System.IO.Path]::DirectorySeparatorChar
        $target = Join-Path $work ($relative -replace '/', $separator)
        $parent = Split-Path -Parent $target
        if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        $extension = [System.IO.Path]::GetExtension($target).ToLowerInvariant()
        if ($imageExts -contains $extension) {
            [System.IO.File]::WriteAllBytes($target, $tinyPng)
        } elseif ($extension -eq '.pdf') {
            # PDF 夹具：一页的最小有效 PDF，带正确 xref，PDFsharp 能打开。
            [System.IO.File]::WriteAllBytes($target, (New-MinimalPdfBytes))
        } else {
            [System.IO.File]::WriteAllText($target, $text, [System.Text.UTF8Encoding]::new($false))
        }
        return $target
    }

    $smokePath = Join-Path $pkgDir 'smoke.json'
    $smoke = $null
    if (Test-Path -LiteralPath $smokePath) {
        $smoke = Get-Content -LiteralPath $smokePath -Raw -Encoding UTF8 | ConvertFrom-Json
    }

    if ($null -ne $smoke -and $smoke.files) {
        $smokeText = if ($smoke.text) { [string]$smoke.text } else { $defaultText }
        foreach ($relative in @($smoke.files)) {
            if ([string]::IsNullOrWhiteSpace($relative)) { continue }
            $samplePaths += New-SmokeFile ([string]$relative) $smokeText
        }
        if ($smoke.directories) {
            foreach ($relative in @($smoke.directories)) {
                if ([string]::IsNullOrWhiteSpace($relative)) { continue }
                $directory = Join-Path $work ([string]$relative)
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
                $samplePaths += $directory
            }
        }
    } elseif ($kind -eq 'folders') {
        $sub = Join-Path $work 'sample-dir'
        New-Item -ItemType Directory -Path $sub -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $sub 'hello.txt'), $defaultText, [System.Text.UTF8Encoding]::new($false))
        $samplePaths = @($sub)
    } else {
        $ext = '.txt'
        if ($cmd.selection -and $cmd.selection.extensions -and $cmd.selection.extensions.Count -gt 0) {
            $ext = [string]$cmd.selection.extensions[0]
        }
        $content = switch ($ext.ToLowerInvariant()) {
            '.csv'  { "name,score`nAlice,95`nBob,88`n" }
            '.json' { '[{"name":"Alice","score":95}]' }
            '.b64'  { 'SGVsbG8=' }
            default { $defaultText }
        }
        if ($imageExts -contains $ext.ToLowerInvariant()) {
            for ($i = 1; $i -le $sampleCount; $i++) {
                $samplePaths += New-SmokeFile ("sample$i$ext") $content
            }
        } else {
            for ($i = 1; $i -le $sampleCount; $i++) {
                $samplePaths += New-SmokeFile ("sample$i$ext") $content
            }
        }
    }

    $parameters = @{}
    if ($cmd.parameters) {
        foreach ($p in $cmd.parameters) {
            if ($p.type -eq 'folder') {
                $parameters[$p.key] = $work
            } elseif ($null -ne $p.default -and -not [string]::IsNullOrEmpty([string]$p.default)) {
                $parameters[$p.key] = [string]$p.default
            } elseif ($p.required) {
                $parameters[$p.key] = 'sample'
            } else {
                $parameters[$p.key] = ''
            }
        }
    }

    $phase = if ($cmd.requiresPreview -eq $true) { 'preview' } else { 'execute' }
    $invocation = [ordered]@{
        protocolVersion = 1
        requestId       = [Guid]::NewGuid().ToString('N')
        commandId       = [string]$cmd.id
        phase           = $phase
        paths           = @($samplePaths)
        parameters      = $parameters
    } | ConvertTo-Json -Compress -Depth 8

    function Set-PsiArgs($p, [string[]]$argList) {
        if ($p.PSObject.Properties['ArgumentList']) {
            foreach ($a in $argList) { $p.ArgumentList.Add($a) }
        } else {
            $p.Arguments = ($argList | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
        }
    }

    $extLower = [System.IO.Path]::GetExtension($entryFullPath).ToLowerInvariant()
    $psi = $null
    switch ($extLower) {
        '.exe' {
            $psi = [System.Diagnostics.ProcessStartInfo]::new($entryFullPath)
        }
        '.ps1' {
            $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $psi = [System.Diagnostics.ProcessStartInfo]::new($(if (Test-Path -LiteralPath $psExe) { $psExe } else { 'powershell.exe' }))
            Set-PsiArgs $psi @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $entryFullPath)
        }
        '.py' {
            $py = Get-Command python -ErrorAction SilentlyContinue
            if (-not $py) { throw "缺少 Python 运行时以验证 $($manifest.id)" }
            $psi = [System.Diagnostics.ProcessStartInfo]::new($py.Source)
            Set-PsiArgs $psi @('-u', $entryFullPath)
            $psi.Environment['PYTHONUTF8'] = '1'
        }
        '.mjs' {
            $node = Get-Command node -ErrorAction SilentlyContinue
            if (-not $node) { throw "缺少 Node.js 运行时以验证 $($manifest.id)" }
            $psi = [System.Diagnostics.ProcessStartInfo]::new($node.Source)
            Set-PsiArgs $psi @($entryFullPath)
        }
        default { throw "不支持的入口类型：$extLower" }
    }

    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $pkgDir
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    if ($psi.PSObject.Properties['StandardInputEncoding']) { $psi.StandardInputEncoding = $utf8 }
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding = $utf8

    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdinWriter = [System.IO.StreamWriter]::new($proc.StandardInput.BaseStream, $utf8)
    $stdinWriter.WriteLine($invocation)
    $stdinWriter.Flush()

    $terminal = $null
    while ($null -ne ($line = $proc.StandardOutput.ReadLine())) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $evt = $line | ConvertFrom-Json
        if ($evt.type -notin @('progress', 'preview', 'result', 'error')) {
            throw "非法事件类型：$($evt.type)"
        }
        if ($null -ne $terminal) {
            throw "插件在终止事件后继续输出"
        }
        if ($evt.type -ne 'progress') {
            $terminal = $evt
        }
    }
    if (-not $proc.WaitForExit(20000)) {
        if ($PSVersionTable.PSEdition -eq 'Core') { $proc.Kill($true) } else { $proc.Kill() }
        throw "插件协议烟测超时 (20s)"
    }
    $stderr = $proc.StandardError.ReadToEnd()
    if ($proc.ExitCode -ne 0 -or $null -eq $terminal -or $terminal.type -eq 'error') {
        $msg = if ($terminal) { $terminal.message } else { $stderr }
        throw "插件烟测失败 (exit=$($proc.ExitCode)): $msg"
    }
}

foreach ($dir in (Get-ChildItem -LiteralPath $packagesRoot -Directory | Sort-Object Name)) {
    if ($Only -and $dir.Name -ne $Only) { continue }
    $problems = New-Object System.Collections.Generic.List[string]
    try {
        Assert-NoReparse $dir.FullName
        $manifestPath = Join-Path $dir.FullName 'plugin.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) { throw "缺少 plugin.json" }
        if (-not (Test-Path -LiteralPath (Join-Path $dir.FullName 'README.md'))) { $problems.Add("缺少 README.md") }
        if (-not (Test-Path -LiteralPath (Join-Path $dir.FullName 'LICENSE'))) { $problems.Add("缺少 LICENSE") }

        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($manifest.id -notmatch '^[a-z][a-z0-9_.-]{2,79}$' -or $manifest.id.StartsWith('core.')) { $problems.Add("id 无效或使用了保留前缀 core.") }
        if ([string]::IsNullOrWhiteSpace($manifest.name)) { $problems.Add("name 不能为空") }
        if ($manifest.apiVersion -ne 1) { $problems.Add("apiVersion 必须为 1") }
        if ($manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { $problems.Add("version 格式无效") }
        if ([string]::IsNullOrWhiteSpace($manifest.license)) { $problems.Add("license 不能为空") }
        if ($manifest.runtime -ne 'process') { $problems.Add("社区插件 runtime 必须为 process") }
        if ([string]::IsNullOrWhiteSpace($manifest.entry)) { $problems.Add("entry 不能为空") }
        if ($seenIds.ContainsKey($manifest.id)) { $problems.Add("重复插件 ID：$($manifest.id)") }
        $seenIds[$manifest.id] = $true

        if (-not $registeredIds.ContainsKey($manifest.id)) {
            $problems.Add("尚未在 registry.json 中登记 $($manifest.id)")
        }

        $perms = if ($null -ne $manifest.permissions) { $manifest.permissions } else { @() }
        foreach ($perm in $perms) {
            if ($perm -notin $allowedPermissions) { $problems.Add("未知权限声明：$perm") }
        }

        if (-not $manifest.commands -or $manifest.commands.Count -lt 1 -or $manifest.commands.Count -gt 128) {
            $problems.Add("commands 数量须在 1..128 之间")
        } else {
            foreach ($cmd in $manifest.commands) {
                if ($cmd.id -notmatch '^[a-z][a-z0-9_.-]{2,79}$' -or $cmd.id.StartsWith('core.')) { $problems.Add("命令 ID 无效：$($cmd.id)") }
                if ($seenCmds.ContainsKey($cmd.id)) { $problems.Add("重复命令 ID：$($cmd.id)") }
                $seenCmds[$cmd.id] = $true
                if ([string]::IsNullOrWhiteSpace($cmd.title) -or $cmd.title.Length -gt 80) { $problems.Add("命令标题无效：$($cmd.id)") }
                if ($cmd.selection) {
                    if ($cmd.selection.kind -and ($cmd.selection.kind -notin $allowedKinds)) { $problems.Add("selection.kind 无效：$($cmd.selection.kind)") }
                    if ($cmd.selection.extensions) {
                        foreach ($e in $cmd.selection.extensions) {
                            if ($e -notmatch '^\.[a-zA-Z0-9]+$') { $problems.Add("扩展名格式无效：$e") }
                        }
                    }
                }
                if ($cmd.parameters) {
                    $pKeys = @{}
                    foreach ($p in $cmd.parameters) {
                        if ($p.key -notmatch '^[a-z][a-zA-Z0-9_.-]{2,79}$') { $problems.Add("参数 key 无效：$($p.key)") }
                        if ($pKeys.ContainsKey($p.key)) { $problems.Add("重复参数 key：$($p.key)") }
                        $pKeys[$p.key] = $true
                        if ($p.type -notin $allowedParamTypes) { $problems.Add("参数类型无效：$($p.type)") }
                        if ($p.type -eq 'select' -and (-not $p.options -or $p.default -notin $p.options)) {
                            $problems.Add("下拉参数 $($p.key) 默认值必须属于 options")
                        }
                    }
                }
            }
        }

        $csproj = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.csproj' | Select-Object -First 1
        $entryPath = $null
        if ($null -ne $csproj) {
            $buildOut = & dotnet build $csproj.FullName -c Release --nologo 2>&1
            if ($LASTEXITCODE -ne 0) {
                $problems.Add("dotnet build 失败：" + ($buildOut -join ' '))
            } else {
                $tfm = Get-ChildItem -LiteralPath (Join-Path $dir.FullName 'bin\Release') -Directory -ErrorAction SilentlyContinue |
                    Sort-Object { $_.Name -ne 'net10.0' }, Name | Select-Object -First 1
                $tfmName = if ($tfm) { $tfm.Name } else { 'net10.0' }
                $entryPath = Join-Path $dir.FullName "bin\Release\$tfmName\$($manifest.entry)"
            }
        } else {
            $entryPath = Join-Path $dir.FullName ($manifest.entry -replace '/', '\')
        }

        if ($null -ne $entryPath) {
            $ext = [System.IO.Path]::GetExtension($entryPath).ToLowerInvariant()
            if ($ext -notin $allowedExts) { $problems.Add("不支持的入口扩展名：$ext") }
            if (-not (Test-Path -LiteralPath $entryPath)) {
                $problems.Add("构建后入口文件不存在：$entryPath")
            } elseif ($problems.Count -eq 0) {
                Invoke-SmokeHandshake -pkgDir $dir.FullName -manifest $manifest -entryFullPath $entryPath
            }
        }
    }
    catch {
        $problems.Add($_.Exception.Message)
    }

    if ($problems.Count -gt 0) {
        $failed++
        Write-Host "✗ $($dir.Name)" -ForegroundColor Red
        foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
    } else {
        Write-Host "✓ $($dir.Name)" -ForegroundColor Green
    }
}

if (Test-Path -LiteralPath $tempBase) { Remove-Item -LiteralPath $tempBase -Recurse -Force }
if ($failed -gt 0) { exit 1 }
exit 0
