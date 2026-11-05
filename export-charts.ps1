<#
maimaiKcalCalculator 离线导出：把游戏包里全部 .ma2 谱面的「预计消耗热量」算出来写成 JSON。

    .\export-charts.ps1                  # 默认按 settings.json（sdez）
    .\export-charts.ps1 -Server sdgb
    .\export-charts.ps1 -OutDir dataout
    .\export-charts.ps1 -Alpha 1.4e-3 -Weight 68      # 临时换个系数试试
    .\export-charts.ps1 -Table                        # 顺带生成 CSV / XLSX 表格（要 python + UnityPy）

产物（默认写到项目根的 dataout\）：
    charts.json   逐谱面：曲名 / 难度 / 等级 / 音符数 / 手部里程 / 热量
    summary.json  汇总：总计、按难度分布、最耗热量的谱面与曲目、数据质量
    -Table 另出：charts.csv（7 列，含封面相对路径）、charts.xlsx（嵌封面缩略图 + 难度配色）、
                 jackets\<id>.png（从游戏包 AssetBundle 里解出来的 400×400 封面）

数值口径与 mod 选曲画面那行「预计 12.3 kcal · 手部 210 m」同源：都走 src\Ma2.cs + src\Geo.cs。
alpha / weight 默认取游戏目录里的 maimaiKcalCalculator.cfg（没有就用项目的 config.default.cfg）。
#>
param(
    [string]$GameRoot = '',
    [string]$Server = '',
    [string]$OutDir = '',
    [string]$Alpha = '',
    [string]$Weight = '',
    [switch]$SkipBuild,
    [switch]$Table,
    [ValidateSet('png', 'jpg')][string]$JacketFormat = 'png',
    [switch]$NoXlsx
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

# alpha / weight：优先用游戏目录里那份（用户实际在跑的配置），没有就退回项目默认值
$cfg = $p.ConfigFile
if (-not (Test-Path $cfg)) { $cfg = Join-Path $here 'config.default.cfg' }
Write-Host "系数来源: $cfg"

$exeArgs = @('--sa', $p.StreamingAssets, '--out', $OutDir, '--cfg', $cfg, '--server', $p.Server)
if ($Alpha)  { $exeArgs += @('--alpha', $Alpha) }
if ($Weight) { $exeArgs += @('--weight', $Weight) }

& $exe @exeArgs
if ($LASTEXITCODE -ne 0) { throw "导出失败（exit $LASTEXITCODE）" }

Write-Host ''
Get-ChildItem $OutDir -Filter *.json | Sort-Object Name | ForEach-Object {
    Write-Host ("产物: {0}  ({1:N0} bytes)" -f $_.FullName, $_.Length)
}

if ($Table) {
    $py = (Get-Command python -ErrorAction SilentlyContinue)
    if (-not $py) { throw '找不到 python；封面要走 UnityPy，先装 python + UnityPy + Pillow' }
    $tableArgs = @((Join-Path $here 'tools\export-table.py'), '--dataout', $OutDir, '--format', $JacketFormat)
    if ($NoXlsx) { $tableArgs += '--no-xlsx' }
    Write-Host ''
    & $py.Source @tableArgs
    if ($LASTEXITCODE -ne 0) { throw "生成表格失败（exit $LASTEXITCODE）" }
}
