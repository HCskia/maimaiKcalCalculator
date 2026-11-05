# maimaiKcalCalculator

舞萌 DX 的 MelonLoader mod，把手臂移动的距离折算成卡路里，显示在选曲画面和上屏右下角。

选曲画面上给的是这张谱的预估值（`预计 12.3 kcal · 手部 210 m`），上屏右下角那一行是实时累计的（`本曲 13.8 kcal · 229 m`、`本轮 27.6 kcal · 458 m`）。

这两个数一般对不上，因为口径本来就不同：

- 预估：启动时后台线程把游戏里全部 `.ma2` 离线解析一遍，按音符位置推算手要走多远，结果缓存起来
- 实时：每帧读按键和触摸区的电平，只算你真的按下去的手。所以打漏的音符不计，但手挥出去的空白算

## 换算方式

```
kcal = alpha × 体重(kg) × 手部里程(m)
```

`alpha` 默认 `1.0e-3 kcal/(kg·m)`，体重默认 70 kg，两个都在配置文件里改。

里程这一半是可算的：

- 按键：8 个键心在半径 310 mm 的圆上，45° 等分，1 号键右上、顺时针。位移按弦长
- 滑条：沿按键环的弧长，跨 d 格就是 d × 45°
- 触摸：34 个区各自的中心点。`TouchPannelSVG.txt` 的图形外接框 1064 单位对应 52.5 cm 的整圆，即 1 单位 ≈ 0.4934 mm
- 两只手按「离上一手位最近」贪心分配，避免同一帧双押时左右手路径交叉

## 准头

里程可信，因为它只取决于输入和上面那几个尺寸，量化误差约 ±10%。卡路里就别太当真，绝对误差 ±30–50%，全部来自 `alpha`。

`alpha` 为什么这么大：举臂等长、按压、启停这些纯物理做的功加起来只有 5 kcal 左右，而 70 kg 的人打 287 m、2.5 分钟报出来是 20 kcal，差的那部分实际是全身参与和肌肉低效率，只能一并塞进系数里。反过来推 MET 大约是 6.9，节奏游戏通常落在 3–8 之间，量级至少不荒唐。

想认真标定的话，找条胸带式心率带连打三首（腕式光学手环在舞萌上读数基本没用），然后 `alpha = 手表 kcal ÷ (体重 × 总里程)`。

## 编译与部署

需要 .NET SDK。脚本用 SDK 自带的 Roslyn csc，加上游戏自己的托管程序集，不联网，也不装 NuGet 包。

先改 `settings.json`，把 `gameRoot` 指到自己的游戏目录：

```json
{
  "server": "sdez",
  "sdez": { "gameRoot": "D:\\otogame\\maimai\\SDEZ170" },
  "sdgb": { "gameRoot": "D:\\otogame\\maimai\\SDGB1.56" }
}
```

然后：

```powershell
.\build.ps1                        # 只编译，产物在 bin\maimaiKcalCalculator.dll
.\build.ps1 -Deploy                # 编译 + 部署，默认走 SDEZ（日服）
.\build.ps1 -Deploy -Server sdgb   # 部署到国服
```

`-Deploy` 会把 DLL 复制到 `<Package>\Mods\`，并在这台机器上第一次部署时生成 `<Package>\maimaiKcalCalculator.cfg`；已经存在就原样保留，不会覆盖你调好的参数。

部署到哪个包按 `-Server` → `settings.json` 的 `server` → 自动探测 的顺序决定，三个都没结果时默认 SDEZ。`-GameRoot <路径>` 可以直接指目录，跳过这套推断。

改 `build.ps1` / `common.ps1` 时记得存成 UTF-8 with BOM：Windows PowerShell 5.1 会拿 GBK 去读没有 BOM 的文件，中文注释能让它直接报语法错。

## 配置

`<Package>\maimaiKcalCalculator.cfg`，改完重启游戏生效：

```ini
enabled = 1

weight = 70
alpha  = 1.0e-3

# 选曲画面卡片叠加（偏移相对中心卡片左上角，负 Y 向下）
showselect = 1
selectx    = 0
selecty    = -204
selectfont = 18

# 上屏右下角常驻 HUD（挂 RearCanvas，面板 1080×1920）
showhud    = 1
hudx       = 40
hudy       = 24
hudfont    = 20
hudcanvas  =        ; 留空 = 自动挑上屏画布

# 单人游玩时 2P 的数组会把同一块面板重复报一遍，默认只统计 1P
track2p    = 0

debug = 0           ; 每 5 秒打一次采样统计
ascii = 0           ; 1 = 文案退回纯 ASCII（日服字体缺字形时用）
```

## 谱面缓存

启动时后台线程（`BelowNormal`）跑一次。缓存放在 `<Package>\Mods\maimaiKcalCalculator\charts.cache`，键是「谱面数量 + 总字节数 + 缓存版本」，所以游戏更新加了几首歌会自动失效重算。

本机 5588 张 `.ma2`，单线程解析要 18.7 秒，按 `ProcessorCount` 拆成多个线程之后几秒就完了。命中缓存时是毫秒级。解析没完成之前选曲画面不显示数字，不会先闪一个占位的 0。

## 实时采样

`Tracker` 每帧读两处：

- 按键电平 `Manager.InputManager.PlayerButtonPush[player, 0..7]`，private static，反射取一次数组
- 触摸区电平 `Manager.InputManager.GetTouchPanelAreaPush(player, TouchPanelArea)`，公开接口，内部是位掩码，O(1)

只有「刚按下」那一帧记一次触点，按住不放不累加。一帧固定 2×(8+34) 次布尔读取，没有分配，没有 LINQ。HUD 也只在文字真的变了才 `SetText`。

「本轮」的边界不是「回到选曲画面」，是 `Manager.GamePlayManager.Instance.GetScoreListCount()`：同一轮里递增，换一轮回落，回落到小值才清零。舞萌每打完一首都回一次选曲，按画面判断会让本曲永远等于本轮。

## 日志

`<Package>\MelonLoader\Latest.log`，前缀 `[MaimaiKcal]`：

```
[MaimaiKcal] v0.1.0 载入
[MaimaiKcal] 谱面缓存命中：5588 张，用时 12 ms
[MaimaiKcal] 已挂钩 Process.MusicSelectProcess.OnUpdate
[MaimaiKcal] HUD 已创建（右下角）
[MaimaiKcal] 本曲结束：229.3 m → 16.1 kcal（触点 812）| 本轮累计 458.6 m
```

## 已知限制

- `alpha` 是经验系数，别把 kcal 当医疗或训练数据用
- 触摸只有 34 个离散区，拿不到连续坐标，滑条按区中心连线近似，通常比真实手路径略短
- 卡片上的预估值不含宴谱面（utage 目前没做）
- 随机曲目的卡是游戏内部造的占位（`musicId=8` / 难度 0，True Love Song Basic），mod 靠 `isRandomScore` 认出来并隐藏，否则会对着随机卡显示一个 1.7 kcal 的假数字
- HUD 只在「本曲有数据」时出现，回到选曲且轮次归零就隐藏
- 文字位置要按 `selectx/selecty` 和 `hudx/hudy` 自己调，不同分辨率可能要微调
- 结算画面的排版试过挂 `Monitor.ResultMonitor.Initialize`，挂钩和日志都正常但画不出来，原因没查明，最后改成常驻 HUD 绕过去了
