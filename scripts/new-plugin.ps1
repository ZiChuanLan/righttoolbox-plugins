param(
    [Parameter(Mandatory = $true)][string]$Slug,
    [Parameter(Mandatory = $true)][string]$Name,
    [ValidateSet('csharp', 'powershell', 'python', 'node')][string]$Template = 'csharp',
    [string]$Group = '实用',
    [string]$Author = 'Community Contributor',
    [string]$Description = '适用于 RightToolbox 的通用扩展插件。',
    [switch]$Register
)

$ErrorActionPreference = 'Stop'
if ($Slug -notmatch '^[a-z0-9][a-z0-9-]{1,40}$') {
    throw "Slug 仅允许小写字母、数字与短横线，如 text-helper。"
}

$root = Split-Path $PSScriptRoot -Parent
$pkgDir = Join-Path $root "packages\$Slug"
if (Test-Path -LiteralPath $pkgDir) {
    throw "插件目录已存在：$pkgDir"
}

$templateDir = Join-Path $root "templates\$Template"
if (-not (Test-Path -LiteralPath $templateDir)) {
    throw "找不到模板：$templateDir"
}

$pluginId = "community.$Slug"
$commandId = "$pluginId.run"
$pascal = ($Slug -split '-' | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }) -join ''
$projectName = "${pascal}Plugin"

New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null

$tokens = @{
    '__PLUGIN_ID__'     = $pluginId
    '__PLUGIN_NAME__'   = $Name
    '__COMMAND_ID__'    = $commandId
    '__COMMAND_TITLE__' = $Name
    '__GROUP__'         = $Group
    '__DESCRIPTION__'   = $Description
    '__PROJECT_NAME__'  = $projectName
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$utf8Bom = [System.Text.UTF8Encoding]::new($true)

foreach ($file in Get-ChildItem -LiteralPath $templateDir -File) {
    $raw = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    foreach ($kv in $tokens.GetEnumerator()) {
        $raw = $raw.Replace($kv.Key, $kv.Value)
    }
    $targetName = if ($file.Name -eq 'PluginTemplate.csproj') { "$projectName.csproj" } else { $file.Name }
    $dest = Join-Path $pkgDir $targetName
    $enc = if ($targetName -match '\.(ps1|psm1)$') { $utf8Bom } else { $utf8NoBom }
    [System.IO.File]::WriteAllText($dest, $raw, $enc)
}

$readme = @"
# $Name ($pluginId)

$Description

## 命令

- **$Name** (`$commandId`)

## 本地验证

```powershell
..\..\scripts\check.ps1 $Slug
..\..\scripts\test.ps1 $Slug
```
"@
[System.IO.File]::WriteAllText((Join-Path $pkgDir 'README.md'), $readme, $utf8NoBom)
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $pkgDir 'LICENSE')

$testScript = @"
param([string]`$PluginDir = `$PSScriptRoot)
`$ErrorActionPreference = 'Stop'
Write-Host "[$Slug] smoke check passed via scripts/check.ps1"
"@
[System.IO.File]::WriteAllText((Join-Path $pkgDir 'test.ps1'), $testScript, $utf8Bom)

if ($Register) {
    $regPath = Join-Path $root 'registry.json'
    $reg = if (Test-Path -LiteralPath $regPath) { Get-Content -LiteralPath $regPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { [PSCustomObject]@{ version = 1; repository = ''; plugins = @() } }
    $langLabel = switch ($Template) {
        'csharp' { 'C# (.NET 10)' }
        'powershell' { 'PowerShell' }
        'python' { 'Python 3' }
        'node' { 'Node.js' }
    }
    $entry = [ordered]@{
        id          = $pluginId
        name        = $Name
        author      = $Author
        license     = 'MIT'
        group       = $Group
        language    = $langLabel
        package     = "packages/$Slug"
        permissions = @('fs.read')
        tags        = @($Group)
        summary     = [ordered]@{
            zh = $Description
            en = $Description
        }
    }
    $list = @($reg.plugins | Where-Object { $_.id -ne $pluginId }) + @($entry)
    $reg.plugins = $list
    $json = $reg | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($regPath, $json + "`n", $utf8NoBom)
}

Write-Host "Created plugin '$pluginId' at packages/$Slug ($Template)"
