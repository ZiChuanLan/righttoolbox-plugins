# PowerShell 5.1 & 7+ SDK for RightToolbox v1 JSON Lines plugins

Set-StrictMode -Version Latest

# 标准输出统一走 UTF-8 StreamWriter，绕开 PowerShell 自带的 ANSI Console.Out。
$script:RtStdout = $null

function Initialize-RtConsole {
    # [Console]::OutputEncoding 只在 Console.Out 尚未创建时生效；PowerShell 启动时
    # 已按系统 ANSI（GBK）建好 Console.Out，直接改编码会让插件输出 GBK 字节，
    # 宿主按 UTF-8 读取就会得到乱码甚至 JSON 解析失败。
    # 因此标准输出统一走自己创建的 UTF-8 StreamWriter（见 Get-RtStdout）。
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    try { [Console]::InputEncoding = $utf8 } catch { }
    try { [Console]::OutputEncoding = $utf8 } catch { }
    [void](Get-RtStdout)
}

function Get-RtStdout {
    if ($null -eq $script:RtStdout) {
        $writer = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), (New-Object System.Text.UTF8Encoding($false)))
        $writer.AutoFlush = $true
        $script:RtStdout = $writer
    }
    return $script:RtStdout
}

function Write-RtEvent {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Event
    )
    $json = $Event | ConvertTo-Json -Compress -Depth 12
    Get-RtStdout | ForEach-Object { $_.WriteLine($json) }
    [Console]::Out.Flush()
}

function Write-RtProgress {
    param(
        [int]$Current = 0,
        [int]$Total = 0,
        [string]$Message = ""
    )
    Write-RtEvent @{
        type    = "progress"
        current = $Current
        total   = $Total
        message = $Message
    }
}

function New-RtRow {
    param(
        [string]$Source = "",
        [string]$Target = "",
        [string]$Status = "就绪",
        [string]$Detail = ""
    )
    return [ordered]@{
        source = $Source
        target = $Target
        status = $Status
        detail = $Detail
    }
}

function New-RtResult {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [object[]]$Rows = @(),
        [string[]]$Outputs = @(),
        [string]$ClipboardText = $null,
        [string]$UndoId = $null
    )
    $evt = [ordered]@{
        type    = "result"
        message = $Message
        rows    = @($Rows)
        outputs = @($Outputs)
    }
    if ($null -ne $ClipboardText) { $evt["clipboardText"] = $ClipboardText }
    if ($null -ne $UndoId) { $evt["undoId"] = $UndoId }
    return $evt
}

function New-RtPreview {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)]$Plan,
        [object[]]$Rows = @()
    )
    return [ordered]@{
        type    = "preview"
        message = $Message
        plan    = $Plan
        rows    = @($Rows)
    }
}

function Get-RtParam {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string]$Key,
        [string]$Default = ""
    )
    if ($null -ne $Context.Parameters -and $Context.Parameters.ContainsKey($Key)) {
        return [string]$Context.Parameters[$Key]
    }
    return $Default
}

function Get-RtUniqueOutput {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [string]$Suffix = "",
        [string]$Extension = "",
        [string]$OutputFolder = ""
    )
    $dir = if ([string]::IsNullOrWhiteSpace($OutputFolder)) {
        [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($SourcePath))
    } else {
        [System.IO.Path]::GetFullPath($OutputFolder)
    }
    [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($SourcePath) + $Suffix
    $ext = if ([string]::IsNullOrEmpty($Extension)) { "" } elseif ($Extension.StartsWith(".")) { $Extension } else { "." + $Extension }
    for ($i = 0; $i -lt 10000; $i++) {
        $name = if ($i -eq 0) { "$stem$ext" } else { "$stem ($i)$ext" }
        $candidate = [System.IO.Path]::Combine($dir, $name)
        if (-not [System.IO.File]::Exists($candidate) -and -not [System.IO.Directory]::Exists($candidate)) {
            return $candidate
        }
    }
    throw "同名输出过多，请清理目标目录或更换输出文件夹。"
}

function Read-RtStdinLine {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream
    )
    # 直接从标准输入字节流按行读取：Console.In.ReadLineAsync() 在 .NET Framework 下
    # 退化为同步读取，stdin 未关闭时会永久阻塞，导致插件卡死、宿主/CI 读不到任何输出。
    $bytes = New-Object System.Collections.Generic.List[byte]
    while ($true) {
        $value = $Stream.ReadByte()
        if ($value -lt 0) {
            if ($bytes.Count -eq 0) { return $null }
            break
        }
        if ($value -eq 10) { break }
        if ($value -eq 13) { continue }
        $bytes.Add([byte]$value)
        if ($bytes.Count -gt 2097152) { throw "消息超过 2 MB 上限。" }
    }
    return [System.Text.Encoding]::UTF8.GetString($bytes.ToArray())
}

function Start-RtCancelRead {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream
    )
    $buffer = New-Object byte[] 64
    return [PSCustomObject]@{
        Stream = $Stream
        Buffer = $buffer
        Async  = $Stream.BeginRead($buffer, 0, $buffer.Length, $null, $null)
    }
}
function Test-RtCancelled {
    param(
        [Parameter(Mandatory = $true)]$Context
    )
    $read = $Context._CancelRead
    if ($null -eq $read) { return $false }
    if (-not $read.Async.IsCompleted) { return $false }
    try {
        $count = $read.Stream.EndRead($read.Async)
    } catch {
        $Context._CancelRead = $null
        return $false
    }
    if ($count -le 0) {
        $Context._CancelRead = $null
        return $false
    }
    $text = [System.Text.Encoding]::UTF8.GetString($read.Buffer, 0, $count)
    $Context._CancelRead = Start-RtCancelRead -Stream $read.Stream
    return $text.Contains("CANCEL")
}

function Invoke-RightToolboxPlugin {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Handler
    )
    Initialize-RtConsole
    $stdin = [Console]::OpenStandardInput()
    try {
        $firstLine = Read-RtStdinLine -Stream $stdin
        if ([string]::IsNullOrWhiteSpace($firstLine)) {
            throw "未收到插件调用消息。"
        }
        $raw = $firstLine | ConvertFrom-Json
        $verProp = $raw.PSObject.Properties['protocolVersion']
        if ($null -eq $verProp -or $verProp.Value -ne 1) {
            throw "不支持的协议版本。"
        }

        $paramsMap = @{}
        $paramsProp = $raw.PSObject.Properties['parameters']
        if ($null -ne $paramsProp -and $null -ne $paramsProp.Value) {
            foreach ($prop in $paramsProp.Value.PSObject.Properties) {
                $paramsMap[$prop.Name] = [string]$prop.Value
            }
        }

        $pathsList = @()
        $pathsProp = $raw.PSObject.Properties['paths']
        if ($null -ne $pathsProp -and $null -ne $pathsProp.Value) {
            $pathsList = @($pathsProp.Value | ForEach-Object { [string]$_ })
        }

        $planProp = $raw.PSObject.Properties['plan']
        $planVal = if ($null -ne $planProp) { $planProp.Value } else { $null }
        $cmdProp = $raw.PSObject.Properties['commandId']
        $phaseProp = $raw.PSObject.Properties['phase']

        $ctx = [PSCustomObject]@{
            Request     = $raw
            CommandId   = if ($null -ne $cmdProp) { [string]$cmdProp.Value } else { "" }
            Phase       = if ($null -ne $phaseProp) { [string]$phaseProp.Value } else { "execute" }
            Paths       = $pathsList
            Parameters  = $paramsMap
            Plan        = $planVal
            _CancelRead = $null
        }

        $ctx._CancelRead = Start-RtCancelRead -Stream $stdin
        $terminal = & $Handler $ctx
        if ($terminal -is [System.Array]) {
            $terminal = $terminal[-1]
        }
        if ($null -eq $terminal -or -not ($terminal.type -in @("result", "preview", "error"))) {
            throw "插件必须返回 New-RtResult 或 New-RtPreview 对象。"
        }
        Write-RtEvent $terminal
        if ($terminal.type -eq "error") { [Environment]::Exit(1) }
        # [Console]::In.ReadLineAsync() 仍挂起时，PowerShell 的 exit 会等待标准输入关闭，
        # 导致宿主/CI 读不到 stdout EOF。Environment.Exit 直接结束进程，输出已 flush。
        [Environment]::Exit(0)
    }
    catch {
        Write-RtEvent ([ordered]@{
            type    = "error"
            message = $_.Exception.Message
        })
        [Environment]::Exit(1)
    }
}

Export-ModuleMember -Function Initialize-RtConsole, Write-RtEvent, Write-RtProgress, New-RtRow, New-RtResult, New-RtPreview, Get-RtParam, Get-RtUniqueOutput, Test-RtCancelled, Invoke-RightToolboxPlugin
