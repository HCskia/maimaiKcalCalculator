<#
maimaiKcalCalculator 按体重出热量：读「基准」（与体重无关的 kcalPerKg），乘上体重，写出新的 JSON。

    .\export-weight.ps1 -Weight 65                    # → dataout\kcal\65kg.json
    .\export-weight.ps1 -Weight 82.5                  # → dataout\kcal\82.5kg.json
    .\export-weight.ps1 -Weight 70 -Out D:\tmp\70kg.json
    .\export-weight.ps1 -Weight 60 -SkipBuild         # 已经编译过

基准只跟「谱面 + alpha」有关（见 .\export-baseline.ps1），所以换体重不用重新解析游戏包，
一秒出结果。产物里：
    charts[]  每张谱一行：曲名 / 难度 / 等级 / 音符数 / 手部里程 / kcalPerKg / kcal
    songs[]   按曲目聚合：每首下列出各难度在这一体重下的 kcal
kcal = kcalPerKg × 体重，四舍五入到 3 位小数，和 charts.json 同一精度。
#>
param(
    [Parameter(Mandatory = $true)][double]$Weight,
    [string]$Baseline = '',
    [string]$Out = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Baseline) { $Baseline = Join-Path $here 'dataout\kcal-baseline.json' }
if (-not (Test-Path $Baseline)) {
    throw "找不到基准文件: $Baseline`n先跑 .\export-baseline.ps1 生成基准（只需要跑一次）"
}

$proj = Join-Path $here 'tools\KcalWeight\KcalWeight.csproj'
$exe  = Join-Path $here 'tools\KcalWeight\bin\Release\net10.0\KcalWeight.exe'

if (-not $SkipBuild) {
    & dotnet build $proj -c Release -nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw "编译 KcalWeight 失败（exit $LASTEXITCODE）" }
}
if (-not (Test-Path $exe)) { throw "找不到换算程序: $exe（去掉 -SkipBuild 重新编译）" }

$inv = [System.Globalization.CultureInfo]::InvariantCulture
$exeArgs = @('--baseline', $Baseline, '--weight', $Weight.ToString('0.###', $inv))
if ($Out) { $exeArgs += @('--out', $Out) }

& $exe @exeArgs
if ($LASTEXITCODE -ne 0) { throw "换算失败（exit $LASTEXITCODE）" }
