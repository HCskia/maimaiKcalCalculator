# maimaiKcalCalculator 的脚本共用部分：解析「游戏装在哪、项目根在哪」。
# 优先级：命令行参数 > settings.json > 环境变量 > 自动探测；候选路径必须能看到
# Package\Sinmai_Data\Managed\Assembly-CSharp.dll 才算数。

function ConvertTo-GameServer {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    switch ($Name.Trim().ToLowerInvariant()) {
        'sdgb'  { return 'sdgb' }
        'cn'    { return 'sdgb' }
        '国服'  { return 'sdgb' }
        'sdez'  { return 'sdez' }
        'jp'    { return 'sdez' }
        '日服'  { return 'sdez' }
        default { return $null }
    }
}

function Test-GamePackage {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try { $full = [System.IO.Path]::GetFullPath($Path) } catch { return $null }
    if (Test-Path (Join-Path $full 'Sinmai_Data\Managed\Assembly-CSharp.dll')) { return $full }
    $sub = Join-Path $full 'Package'
    if (Test-Path (Join-Path $sub 'Sinmai_Data\Managed\Assembly-CSharp.dll')) { return $sub }
    return $null
}

function Resolve-KcalPaths {
    param(
        [Parameter(Mandatory = $true)][string]$ToolDir,
        [string]$GameRoot = '',
        [string]$Server = ''
    )

    $settingsPath = Join-Path $ToolDir 'settings.json'
    $settings = $null
    if (Test-Path $settingsPath) {
        try { $settings = [System.IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json }
        catch { Write-Warning "settings.json 解析失败，改用自动探测: $_" }
    }

    $server = ConvertTo-GameServer $Server
    $serverFrom = '-Server'
    if (-not $server -and $settings -and $settings.server) {
        $server = ConvertTo-GameServer ([string]$settings.server)
        if ($server) { $serverFrom = 'settings.json 的 server' }
    }

    $blockOf = {
        param($s)
        if (-not $settings) { return $null }
        if ($s -eq 'sdez') { return $settings.sdez }
        return $settings.sdgb
    }

    $cands = @()
    if ($GameRoot) { $cands += $GameRoot }
    if ($settings) {
        $block = & $blockOf $server
        if ($block) {
            if ($block.packageDir) { $cands += [string]$block.packageDir }
            if ($block.gameRoot)   { $cands += [string]$block.gameRoot }
        }
        if ($settings.packageDir) { $cands += [string]$settings.packageDir }
        if ($settings.gameRoot)   { $cands += [string]$settings.gameRoot }
    }
    if ($env:MAIMAI_KCAL_GAME_ROOT) { $cands += $env:MAIMAI_KCAL_GAME_ROOT }
    $cands += (Join-Path $ToolDir '..\..')
    $cands += (Join-Path $ToolDir '..')

    $pkg = $null
    foreach ($c in $cands) {
        $pkg = Test-GamePackage $c
        if ($pkg) { break }
    }
    if (-not $pkg -and $settings) {
        foreach ($s in @('sdez', 'sdgb')) {
            $block = & $blockOf $s
            if (-not $block) { continue }
            foreach ($c in @([string]$block.packageDir, [string]$block.gameRoot)) {
                $pkg = Test-GamePackage $c
                if ($pkg) { $server = $s; $serverFrom = '按可用的游戏目录推断'; break }
            }
            if ($pkg) { break }
        }
    }
    if (-not $pkg) {
        throw ("找不到游戏 Package 目录。`n" +
               "  在 $settingsPath 里填 sdez / sdgb 的 gameRoot，或用 -GameRoot <路径>。`n" +
               "  试过的路径：`n    " + ($cands -join "`n    "))
    }
    $pkg = (Resolve-Path $pkg).Path
    if (-not $server) { $server = 'sdez'; $serverFrom = '默认值' }

    $projectDir = (Resolve-Path $ToolDir).Path

    [pscustomobject]@{
        ToolDir        = (Resolve-Path $ToolDir).Path
        ProjectDir     = $projectDir
        SettingsPath   = $settingsPath
        Settings       = $settings
        Server         = $server
        ServerFrom     = $serverFrom
        PackageDir     = $pkg
        GameRoot       = (Split-Path -Parent $pkg)
        Managed        = (Join-Path $pkg 'Sinmai_Data\Managed')
        StreamingAssets= (Join-Path $pkg 'Sinmai_Data\StreamingAssets')
        MelonNet35     = (Join-Path $pkg 'MelonLoader\net35')
        Mods           = (Join-Path $pkg 'Mods')
        LogFile        = (Join-Path $pkg 'MelonLoader\Latest.log')
        ConfigFile     = (Join-Path $pkg 'maimaiKcalCalculator.cfg')
        CacheDir       = (Join-Path $pkg 'Mods\maimaiKcalCalculator')
    }
}
