param(
    [string]$OutDir = "",
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $root 'artifacts\dist'
}
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$zipsDir = Join-Path $OutDir 'zips'
New-Item -ItemType Directory -Path $zipsDir -Force | Out-Null

$indexFile = Join-Path $OutDir 'index.json'
$index = if (Test-Path -LiteralPath $indexFile) {
    Get-Content -LiteralPath $indexFile -Raw -Encoding UTF8 | ConvertFrom-Json
} else {
    [PSCustomObject]@{ version = 1; packages = [PSCustomObject]@{} }
}

$packagesMap = @{}
if ($null -ne $index.packages) {
    foreach ($prop in $index.packages.PSObject.Properties) {
        $versionsMap = @{}
        if ($null -ne $prop.Value.versions) {
            foreach ($vp in $prop.Value.versions.PSObject.Properties) {
                $versionsMap[$vp.Name] = $vp.Value
            }
        }
        $packagesMap[$prop.Name] = [ordered]@{
            latest   = [string]$prop.Value.latest
            readme   = [string]$prop.Value.readme
            versions = $versionsMap
        }
    }
}

$added = New-Object System.Collections.Generic.List[string]
$stageRoot = Join-Path $root '.test-work\dist-stage'
if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

try {
    foreach ($dir in (Get-ChildItem -LiteralPath (Join-Path $root 'packages') -Directory | Sort-Object Name)) {
        $manifestPath = Join-Path $dir.FullName 'plugin.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) { continue }

        $manifestText = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8)
        $manifest = $manifestText | ConvertFrom-Json
        $id = [string]$manifest.id
        $version = [string]$manifest.version
        $zipName = "$id-$version.zip"
        $zipRel = "zips/$zipName"
        $zipFull = Join-Path $zipsDir $zipName

        if (-not $packagesMap.ContainsKey($id)) {
            $packagesMap[$id] = [ordered]@{ latest = $version; readme = ''; versions = @{} }
        }
        $pkgRecord = $packagesMap[$id]

        if ($Force -or -not $pkgRecord.versions.ContainsKey($version) -or -not (Test-Path -LiteralPath $zipFull)) {
            $pkgStage = Join-Path $stageRoot $id
            if (Test-Path -LiteralPath $pkgStage) { Remove-Item -LiteralPath $pkgStage -Recurse -Force }
            New-Item -ItemType Directory -Path $pkgStage -Force | Out-Null

            $csproj = Get-ChildItem -LiteralPath $dir.FullName -Filter '*.csproj' | Where-Object { $_.Name -notlike '*.Tests.csproj' } | Select-Object -First 1
            if ($null -ne $csproj) {
                & dotnet build $csproj.FullName -c Release --nologo | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "编译失败：$($csproj.FullName)" }
                $tfm = Get-ChildItem -LiteralPath (Join-Path $dir.FullName 'bin\Release') -Directory -ErrorAction SilentlyContinue |
                    Sort-Object { $_.Name -ne 'net10.0' }, Name | Select-Object -First 1
                $binDir = Join-Path $dir.FullName "bin\Release\$(if ($tfm) { $tfm.Name } else { 'net10.0' })"
                Copy-Item -Path (Join-Path $binDir '*') -Destination $pkgStage -Recurse -Force
                Get-ChildItem -LiteralPath $pkgStage -Filter '*.pdb' -Recurse | Remove-Item -Force
            } else {
                foreach ($item in Get-ChildItem -LiteralPath $dir.FullName -Force) {
                    if ($item.Name -in @('bin', 'obj', 'tests', '__pycache__', 'node_modules', 'smoke.json')) { continue }
                    if ($item.Name -like '*test*') { continue }
                    Copy-Item -LiteralPath $item.FullName -Destination $pkgStage -Recurse -Force
                }
                $ext = [System.IO.Path]::GetExtension([string]$manifest.entry).ToLowerInvariant()
                $sdkSub = Join-Path $pkgStage 'sdk'
                New-Item -ItemType Directory -Path $sdkSub -Force | Out-Null
                switch ($ext) {
                    '.ps1' { Copy-Item -LiteralPath (Join-Path $root 'sdk\powershell\RightToolbox.Plugin.psm1') -Destination $sdkSub -Force }
                    '.py'  { Copy-Item -LiteralPath (Join-Path $root 'sdk\python\righttoolbox_plugin.py') -Destination $sdkSub -Force }
                    '.mjs' { Copy-Item -LiteralPath (Join-Path $root 'sdk\node\righttoolbox-plugin.mjs') -Destination $sdkSub -Force }
                    '.js'  { Copy-Item -LiteralPath (Join-Path $root 'sdk\node\righttoolbox-plugin.mjs') -Destination $sdkSub -Force }
                }
            }

            Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $pkgStage 'plugin.json') -Force
            if (Test-Path -LiteralPath (Join-Path $dir.FullName 'README.md')) {
                Copy-Item -LiteralPath (Join-Path $dir.FullName 'README.md') -Destination (Join-Path $pkgStage 'README.md') -Force
            }
            if (Test-Path -LiteralPath (Join-Path $dir.FullName 'LICENSE')) {
                Copy-Item -LiteralPath (Join-Path $dir.FullName 'LICENSE') -Destination (Join-Path $pkgStage 'LICENSE') -Force
            }

            $entryInside = Join-Path $pkgStage ([string]$manifest.entry -replace '/', '\')
            if (-not (Test-Path -LiteralPath $entryInside)) {
                throw "打包目录缺少入口文件：$entryInside"
            }

            if (Test-Path -LiteralPath $zipFull) { Remove-Item -LiteralPath $zipFull -Force }
            [System.IO.Compression.ZipFile]::CreateFromDirectory($pkgStage, $zipFull, [System.IO.Compression.CompressionLevel]::Optimal, $false)

            $hash = (Get-FileHash -LiteralPath $zipFull -Algorithm SHA256).Hash.ToLowerInvariant()
            $size = (Get-Item -LiteralPath $zipFull).Length
            $pkgRecord.versions[$version] = [ordered]@{
                file     = $zipRel
                sha256   = $hash
                size     = $size
                time     = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
                manifest = $manifest
            }
            $added.Add("$id@$version")
        }

        $pkgRecord.latest = $version
        $readmeFile = Join-Path $dir.FullName 'README.md'
        if (Test-Path -LiteralPath $readmeFile) {
            $readmeText = [System.IO.File]::ReadAllText($readmeFile, [System.Text.Encoding]::UTF8)
            if ($readmeText.Length -gt 200000) { $readmeText = $readmeText.Substring(0, 200000) }
            $pkgRecord.readme = $readmeText
        }
    }

    $sortedPackages = [ordered]@{}
    foreach ($k in ($packagesMap.Keys | Sort-Object)) {
        $sortedPackages[$k] = $packagesMap[$k]
    }
    $outObj = [ordered]@{
        version  = 1
        packages = $sortedPackages
    }
    $outJson = ($outObj | ConvertTo-Json -Depth 14) + "`n"
    [System.IO.File]::WriteAllText($indexFile, $outJson, [System.Text.UTF8Encoding]::new($false))
}
finally {
    if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
}

if ($added.Count -gt 0) {
    Write-Host ("packed: " + ($added -join ' '))
} else {
    Write-Host "every version is packed already"
}
Write-Host "index: $indexFile"
