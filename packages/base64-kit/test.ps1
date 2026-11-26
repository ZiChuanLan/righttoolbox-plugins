$ErrorActionPreference = 'Stop'

$pkgDir = $PSScriptRoot
$entry = Join-Path $pkgDir 'main.ps1'
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$launcher = if (Test-Path -LiteralPath $psExe) { $psExe } else { 'powershell.exe' }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("rt-b64-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

function Invoke-PluginLine([string]$jsonLine) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($launcher)
    $argsList = @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $entry)
    if ($psi.PSObject.Properties['ArgumentList']) {
        foreach ($a in $argsList) { $psi.ArgumentList.Add($a) }
    } else {
        $psi.Arguments = ($argsList | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
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
    $stdinWriter.WriteLine($jsonLine)
    $stdinWriter.Flush()
    $terminal = $null
    while ($null -ne ($line = $proc.StandardOutput.ReadLine())) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $evt = $line | ConvertFrom-Json
        if ($evt.type -ne 'progress') { $terminal = $evt }
    }
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0 -or $null -eq $terminal -or $terminal.type -ne 'result') {
        throw "Plugin failed: $($proc.StandardError.ReadToEnd())"
    }
    return $terminal
}

try {
    $png = Join-Path $work 'icon.png'
    $bytes = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    [System.IO.File]::WriteAllBytes($png, $bytes)

    $encReq = @{
        protocolVersion = 1
        requestId = 'req1'
        commandId = 'community.base64-kit.encode'
        phase = 'execute'
        paths = @($png)
        parameters = @{ outputMode = 'Data URI (含 MIME)'; saveToFile = 'true'; outputFolder = $work }
    } | ConvertTo-Json -Compress -Depth 6

    $encRes = Invoke-PluginLine $encReq
    if (-not $encRes.clipboardText.StartsWith('data:image/png;base64,')) {
        throw "Unexpected clipboard output: $($encRes.clipboardText)"
    }
    $b64File = $encRes.outputs[0]
    if (-not (Test-Path -LiteralPath $b64File)) { throw "Missing .b64.txt output" }

    $decReq = @{
        protocolVersion = 1
        requestId = 'req2'
        commandId = 'community.base64-kit.decode'
        phase = 'execute'
        paths = @($b64File)
        parameters = @{ extension = ''; outputFolder = $work }
    } | ConvertTo-Json -Compress -Depth 6

    $decRes = Invoke-PluginLine $decReq
    $restored = $decRes.outputs[0]
    if ([System.IO.Path]::GetExtension($restored) -ne '.png') { throw "MIME extension not inferred: $restored" }
    $restoredBytes = [System.IO.File]::ReadAllBytes($restored)
    if (-not [System.Linq.Enumerable]::SequenceEqual($bytes, $restoredBytes)) {
        throw "Decoded bytes mismatch"
    }

    Write-Host "PASS base64-kit unit tests"
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
