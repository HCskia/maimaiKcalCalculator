<#
maimaiKcalCalculator 的编译脚本。

用 dotnet SDK 里的 Roslyn csc + 游戏自己的托管程序集（不联网、不装 NuGet）。

    .\build.ps1                 # 编译到 .\bin\maimaiKcalCalculator.dll
    .\build.ps1 -Deploy         # 编译 + 复制到游戏 <Package>\Mods\（默认 SDEZ）
    .\build.ps1 -Deploy -Server sdgb
    .\build.ps1 -Deploy -GameRoot D:\otogame\maimai\SDEZ170
#>
param(
    [switch]$Deploy,
    [string]$GameRoot = '',
    [string]$Server = ''
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'common.ps1')

$p       = Resolve-KcalPaths -ToolDir $here -GameRoot $GameRoot -Server $Server
$managed = $p.Managed
$ml      = $p.MelonNet35
$bin     = Join-Path $here 'bin'
$out     = Join-Path $bin 'maimaiKcalCalculator.dll'

if (-not (Test-Path $managed)) { throw "找不到游戏托管目录: $managed" }
if (-not (Test-Path $ml))      { throw "找不到 MelonLoader 目录: $ml" }

Write-Host "服务器  : $($p.Server)  ($($p.ServerFrom))"
Write-Host "游戏目录: $($p.PackageDir)"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { throw 'PATH 里找不到 dotnet' }
$sdkLine = (& dotnet --list-sdks | Select-Object -Last 1)
if (-not $sdkLine) { throw 'dotnet 没有安装 SDK' }
$sdkVer = ($sdkLine -split '\s+')[0]
$csc = Join-Path (Split-Path $dotnet) "sdk\$sdkVer\Roslyn\bincore\csc.dll"
if (-not (Test-Path $csc)) { throw "找不到 Roslyn 编译器: $csc" }

New-Item -ItemType Directory -Force -Path $bin | Out-Null

$refs = @()
foreach ($n in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xml.dll', 'Assembly-CSharp.dll')) {
    $f = Join-Path $managed $n
    if (-not (Test-Path $f)) { throw "缺少引用程序集: $f" }
    $refs += $f
}
$refs += (Get-ChildItem (Join-Path $managed 'UnityEngine*.dll') | ForEach-Object { $_.FullName })
$tmpro = Join-Path $managed 'Unity.TextMeshPro.dll'
if (Test-Path $tmpro) { $refs += $tmpro } else { throw "缺少 Unity.TextMeshPro.dll" }
foreach ($n in @('MelonLoader.dll', '0Harmony.dll')) {
    $f = Join-Path $ml $n
    if (-not (Test-Path $f)) { throw "缺少 MelonLoader 引用: $f" }
    $refs += $f
}

$src = (Get-ChildItem (Join-Path $here 'src\*.cs') | ForEach-Object { $_.FullName })
if ($src.Count -eq 0) { throw 'src\ 下没有 .cs' }

$cscArgs = @($csc, '-nologo', '-noconfig', '-nostdlib+', '-target:library',
             '-optimize+', '-langversion:latest', '-codepage:65001', "-out:$out")
foreach ($r in $refs) { $cscArgs += "-r:$r" }
$cscArgs += $src

Write-Host "编译中（$($src.Count) 个源文件，$($refs.Count) 个引用）..."
& dotnet @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

$size = (Get-Item $out).Length
Write-Host ("编译成功: {0}  ({1:N0} bytes)" -f $out, $size)

if ($Deploy) {
    New-Item -ItemType Directory -Force -Path $p.Mods | Out-Null
    Copy-Item $out (Join-Path $p.Mods 'maimaiKcalCalculator.dll') -Force
    Write-Host "已部署 DLL: $(Join-Path $p.Mods 'maimaiKcalCalculator.dll')"

    if (-not (Test-Path $p.ConfigFile)) {
        Copy-Item (Join-Path $here 'config.default.cfg') $p.ConfigFile -Force
        Write-Host "已生成配置: $($p.ConfigFile)"
    } else {
        Write-Host "配置已存在，保留: $($p.ConfigFile)"
    }
}
