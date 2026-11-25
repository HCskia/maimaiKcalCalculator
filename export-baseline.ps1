<#
maimaiKcalCalculator 离线导出「热量基准」：把每张谱的「每公斤体重消耗多少 kcal」算出来写成 JSON。

    .\export-baseline.ps1                 # 按 settings.json（sdez），产物 dataout\kcal-baseline.json
    .\export-baseline.ps1 -Server sdgb    # 国服
    .\export-baseline.ps1 -Alpha 1.4e-3   # 换 alpha（要重新标定时用）
    .\export-baseline.ps1 -SkipBuild      # 已经编译过，跳过 dotnet build

为什么要单独出「基准」：
    热量 = alpha × 体重 × 手部里程，体重只是一次乘法。基准里存的是
    kcalPerKg = alpha × 里程（与体重无关），之后用 .\export-weight.ps1 -Weight <kg>
    乘任意体重都行，不用再解析一遍 7000 多张谱面、也不用碰游戏包。

产物：dataout\kcal-baseline.json —— 逐谱面：曲名 / 难度 / 等级 / 音符数 / 手部里程 / kcalPerKg。
数值口径与 mod 选曲画面那行「预计 12.3 kcal · 手部 210 m」同源（src\Ma2.cs + src\Geo.cs）；
alpha 默认取游戏目录里的 maimaiKcalCalculator.cfg（没有就用项目的 config.default.cfg）。
#>
param(
    [string]$GameRoot = '',
    [string]$Server = '',
    [string]$OutDir = '',
    [string]$Alpha = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'common.ps1')

$p = Resolve-KcalPaths -ToolDir $here -GameRoot $GameRoot -Server $Server
if (-not $OutDir) { $OutDir = Join-Path $here 'dataout' }

Write-Host "服务器  : $($p.Server)  ($($p.ServerFrom))"
Write-Host "谱面目录: $($p.StreamingAssets)"
Write-Host "输出目录: $OutDir"

$proj = Join-Path $here 'tools\KcalExport\KcalExport.csproj'
$exe  = Join-Path $here 'tools\KcalExport\bin\Release\net10.0\KcalExport.exe'

if (-not $SkipBuild) {
    & dotnet build $proj -c Release -nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw "编译 KcalExport 失败（exit $LASTEXITCODE）" }
}
if (-not (Test-Path $exe)) { throw "找不到导出程序: $exe（去掉 -SkipBuild 重新编译）" }

# alpha：优先用游戏目录里那份（用户实际在跑的配置），没有就退回项目默认值
$cfg = $p.ConfigFile
if (-not (Test-Path $cfg)) { $cfg = Join-Path $here 'config.default.cfg' }
Write-Host "alpha 来源: $cfg"

$exeArgs = @('--mode', 'baseline', '--sa', $p.StreamingAssets, '--out', $OutDir, '--cfg', $cfg, '--server', $p.Server)
if ($Alpha) { $exeArgs += @('--alpha', $Alpha) }

& $exe @exeArgs
if ($LASTEXITCODE -ne 0) { throw "导出失败（exit $LASTEXITCODE）" }

$baseline = Join-Path $OutDir 'kcal-baseline.json'
Write-Host ''
Write-Host ("基准: {0}  ({1:N0} bytes)" -f $baseline, (Get-Item $baseline).Length)
Write-Host '下一步：.\export-weight.ps1 -Weight 65   # 换成你自己的体重'
