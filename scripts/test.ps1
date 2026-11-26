param(
    [string]$Only = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$packagesRoot = Join-Path $root 'packages'
$failed = 0
$passed = 0

foreach ($dir in (Get-ChildItem -LiteralPath $packagesRoot -Directory | Sort-Object Name)) {
    if ($Only -and $dir.Name -ne $Only) { continue }
    $pkgOk = $true
    $ranAny = $false

    $psTests = Get-ChildItem -LiteralPath $dir.FullName -Filter '*test*.ps1' -Recurse
    foreach ($t in $psTests) {
        $ranAny = $true
        Write-Host "RUN $($dir.Name): $($t.Name)"
        $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        & $(if (Test-Path -LiteralPath $psExe) { $psExe } else { 'powershell.exe' }) -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $t.FullName
        if ($LASTEXITCODE -ne 0) { $pkgOk = $false }
    }

    $csTests = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.Tests.csproj' -Recurse
    foreach ($t in $csTests) {
        $ranAny = $true
        Write-Host "RUN $($dir.Name): $($t.Name)"
        & dotnet run --project $t.FullName -c Release --nologo
        if ($LASTEXITCODE -ne 0) { $pkgOk = $false }
    }

    $pyTests = Get-ChildItem -LiteralPath $dir.FullName -Filter 'test_*.py' -Recurse
    if ($pyTests.Count -gt 0) {
        $ranAny = $true
        $py = Get-Command python -ErrorAction SilentlyContinue
        if (-not $py) { Write-Host "Missing python for $($dir.Name)" -ForegroundColor Red; $pkgOk = $false }
        else {
            & $py.Source -m unittest discover -s $dir.FullName
            if ($LASTEXITCODE -ne 0) { $pkgOk = $false }
        }
    }

    $jsTests = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.test.mjs' -Recurse
    if ($jsTests.Count -gt 0) {
        $ranAny = $true
        $node = Get-Command node -ErrorAction SilentlyContinue
        if (-not $node) { Write-Host "Missing node for $($dir.Name)" -ForegroundColor Red; $pkgOk = $false }
        else {
            foreach ($jt in $jsTests) {
                & $node.Source --test $jt.FullName
                if ($LASTEXITCODE -ne 0) { $pkgOk = $false }
            }
        }
    }

    if (-not $ranAny) {
        Write-Host "WARN $($dir.Name): no unit tests found" -ForegroundColor Yellow
        $pkgOk = $false
    }

    if ($pkgOk) {
        $passed++
        Write-Host "PASS $($dir.Name)" -ForegroundColor Green
    } else {
        $failed++
        Write-Host "FAIL $($dir.Name)" -ForegroundColor Red
    }
}

Write-Host "Summary: $passed passed, $failed failed."
if ($failed -gt 0) { exit 1 }
exit 0
