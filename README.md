# maimaiKcalCalculator

舞萌 DX 的 MelonLoader mod，把手臂移动的距离折算成卡路里，显示在选曲画面和上屏的信息栏底板上。

选曲画面上给的是这张谱的预估值（`预计 12.3 kcal · 手部 210 m`）。上屏那边是实时累计的三行，挂在玩家信息栏底板的右下角：
`本曲 13.8 kcal · 229 m`、`本轮 27.6 kcal · 458 m`、`总计 41.4 kcal · 689 m`。

三行的范围不一样：**本曲**是正在打的这一首；**本轮**是这次投币，`GetScoreListCount` 回落即换轮；
**总计**是从打开游戏到现在所有曲目的合计，换轮、回选曲、重开一轮都不归零，只有重启游戏才归零。

预估和实时一般对不上，因为口径本来就不同：

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

需要 .NET SDK。mod 本身用 SDK 自带的 Roslyn csc 加上游戏自己的托管程序集编译，不联网、也不装 NuGet 包；
`tools\` 下那两个离线工具（导出、自检）是普通 `net10.0` 控制台项目，走 `dotnet build`，所以要用 .NET 10 SDK（本机 10.0.301）。

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

## 导出全谱面热量（离线，不用起游戏）

想把「每张谱预计烧多少」成批拿走（做图表、算歌单、喂别的工具），用 `export-charts.ps1`：

```powershell
.\export-charts.ps1                      # 按 settings.json，默认 SDEZ；产物在 .\dataout\
.\export-charts.ps1 -Server sdgb         # 国服
.\export-charts.ps1 -OutDir D:\tmp\kcal  # 换输出目录
.\export-charts.ps1 -Alpha 1.4e-3 -Weight 68   # 临时换系数（不动配置文件）
.\export-charts.ps1 -SkipBuild           # 已经编译过，跳过 dotnet build
```

被 PowerShell 执行策略拦住时用 `powershell -NoProfile -ExecutionPolicy Bypass -File .\export-charts.ps1`。

它编译 `tools\KcalExport`（一个普通 .NET 控制台程序），**链接** `src\Ma2.cs` + `src\Geo.cs` 编译 ——
不是另写一份算法，所以导出的米数就是选曲画面上那个「预计」的手部里程，mod 改了算法这里自动跟着变。
`alpha` / `weight` 优先读游戏目录里的 `maimaiKcalCalculator.cfg`（没有就用本项目的 `config.default.cfg`），
所以导出的 kcal 和你在这台机器上打一首看到的是同一套系数。

产物两份：

| 文件 | 内容 |
|---|---|
| `dataout\charts.json` | 逐谱面：曲名 / 曲师 / 分类 / BPM / 等级 / 谱师 / 音符数 / 手部里程 / kcal，`meta` 里写明系数来源与口径 |
| `dataout\summary.json` | 汇总：总热量、按难度分布、最耗热量的 30 张谱与 25 首歌、以及数据质量与交叉校验报告 |

本机 `SDEZ170` 实测：7454 个 `.ma2` 族文件 → **7449 张谱 / 1927 首歌**（跳过 1 个 `.ma21` 残留、
去掉 4 个重复的 12055），手部里程合计 897 km、热量合计 **62 789 kcal**，
中位数一张谱 6.7 kcal，最高 43.3 kcal（`Xaleid◆scopiX` MASTER）；解析 + 写盘 1 秒上下（冷盘第一次 3 秒左右）。

几条口径说明（不然容易被数字绊住）：

- **宴谱面也在里面**：`music1xxxxx` 那批是宴谱，`112xxx…` 有 25 首是双人谱拆成 `_L` / `_R` 两份，
  导出成两条（`variant` 字段）。mod 自己的缓存键放不下这个后缀，所以选曲画面里看不到它们。
- **`notes` 和 `gameNotes` 不是一回事**：`notes` 是里程模型真正走过的音符事件数（一条滑条走一次），
  `gameNotes` 是 `Music.xml` 的 `maxNotes`（游戏把滑条起手也算一个音符，新格式谱面还按判定单元另算）。
- **`meters: 0` 不是解析失败**：`000854`「全世界共通リズム感テスト」和 `[X]` 那两张占位谱的音符
  全落在同一个位置上，手确实不用动。
- **`enabled: false`** 是 `Music.xml` 里 `isEnable=false` 的占位条目（游戏不显示，但盘上有文件），
  照样算了一份，方便你自己过滤。
- 等级文案 `levelText` 按 `musicLevelID` 反推（`1..6` 没有 `+`，从 7 起 `7 / 7+ / 8 / 8+ …`）。
  有 16 张谱的 `levelDecimal` 和 `musicLevelID` 打架（N100 里几首老曲），以 `musicLevelID` 为准 ——
  卡面上那个「+」就是 `MusicLevelIDEnum.GetLevelNum(musicLevelID)` 出的。

导出时会做三条独立对账，结果写在 `summary.json` 的 `validation` 里：从原始文本按记录名再数一遍
音符数（应当等于 `Ma2` 的结果）、独立数出的按钮/触摸数对谱面自带的 `T_REC_*` 表头、
等级文案对 `musicLevelID`。本机三条分别是 7449/7449、7449/7449、7431/7447。

### 七列总表（CSV / XLSX，带封面）

```powershell
.\export-charts.ps1 -Table                 # 导出 JSON 后接着生成表格（要 python + UnityPy + Pillow）
python tools\export-table.py               # 也可以只重做表格这一层（读 dataout\charts.json）
python tools\export-table.py --limit 20    # 试跑 20 行
```

一张谱一行，七列固定：

| # | 列 | 说明 |
|---|---|---|
| 1 | 乐曲ID | `musicId`（宴谱是 6 位数，如 110281） |
| 2 | 封面 | 从游戏包 `AssetBundleImages\jacket\ui_jacket_0<id%10000:05d>.ab` 里解出来的 400×400 封面；CSV 里写相对路径 `jackets/000008.png`，XLSX 里直接嵌缩略图 |
| 3 | 乐曲名称 | 日服就是日文原名；`music000000/000001` 没有 `Music.xml`、`11422` 是空格占位，所以空着 |
| 4 | 难度 | BASIC / ADVANCED / EXPERT / MASTER / Re:MASTER / UTAGE，**白字**，底色按难度（绿 / 黄 / 红 / 深紫 / 浅一点的紫 / 粉） |
| 5 | 难度等级 | 卡面上那个等级（`12` / `13+`），按 `musicLevelID` 反推 |
| 6 | 预计手部移动距离(m) | 就是 `charts.json` 的 `meters` |
| 7 | 预计消耗的卡路里(kcal) | 就是 `charts.json` 的 `kcal` |

**CSV 存不了颜色和图片**（纯文本格式），所以第 4 列配色、第 2 列封面只存在于 `charts.xlsx`；
CSV 那两列分别是难度文本和封面文件路径 —— 需要的话在 Excel 里用查询函数把图贴进来。
CSV 是 UTF-8 with BOM，Excel / WPS 双击打开中文和日文都不乱码。

本机产物：`charts.csv` 516 KB（7450 行含表头）、`charts.xlsx` 14.5 MB（7449 行 × 封面缩略图）、
`jackets\` 1926 张共 **441 MB**（400×400 无损 PNG，`--format jpg` 可以压到 60 MB 左右）。

整个 `dataout\` 都在 `.gitignore` 里：里面的东西一条命令就能重生成，而 `jackets\` 单独就有 441 MB。
想公开这批数据的话，挂到 Release 附件比塞进仓库历史合适。

## 自检（不用起游戏）

「本曲 / 本轮 / 总计」这三个数字的逻辑最容易改坏（`EndTrack` 重复调用、换轮只清本轮、总计单调不减）。
`tools\KcalTotalTest` 把这堆断言做成了一个控制台程序：

```powershell
dotnet run --project tools\KcalTotalTest -c Release     # 全过打 ALL PASS、退出码 0；有失败打 FAILED、退出码 1
```

和导出工具同一个套路：`csproj` 里 `<Compile Include="..\..\src\Tracker.cs" />` 把 `Tracker.cs` 和 `Strings.cs`
链进来编译，Unity / Manager / Melon 的 API 由 `Stubs.cs` 打桩。所以验的是 mod 的真实逻辑，不是另抄一份实现。

覆盖的断言：开局为 0、进行中实时计入、`EndTrack` 重复调用不会重复入账、同一轮下一曲连续、换轮只清本轮、
防御性 `BeginTrack` 只入账一次、总计单调不减、三行 HUD 文案的内容与顺序（含 `ascii=1` 的英文版）。

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

# 上屏常驻 HUD：挂在「上屏信息栏底板」的右下角（自动按在场玩家分 1P/2P 两块）
# 三行：本曲 / 本轮 / 总计（总计 = 本次启动游戏以来的全部消耗，换轮不清零）
showhud    = 1
# 距底板上右边 / 下边的像素（1P / 2P 两块屏用同一组偏移；想贴死角落就填 0）
hudx       = 40
hudy       = 24
hudfont    = 20
hudcanvas  =        ; 强制指定画布名（留空 = 自动找 1P/2P 各自的 RearCanvas/Sub）
hudmonitor = auto   ; auto / 1p / 2p / both：HUD 画在哪几块屏上

# 统计哪几位玩家：auto（按游戏里的入场状态自动判断 1P / 2P / 1P+2P）
# 也可写 off / 1p / 2p / both；旧配置 track2p=1 等价于 both
players    = auto

debug = 0           ; 1 = 每 5 秒打一次采样统计；没开始打时还会在 HUD 位置画一个 0 值面板方便对位
ascii = 0           ; 1 = 文案退回纯 ASCII（日服字体缺字形时用）
```

### HUD 为什么挂 `RearCanvas/Sub` 而不是整块画布

整块上屏画布是 **1080×1920**，但机台真正看得到的只有两块：

- 顶部 **1080×450 的 `Sub`** —— 玩家信息栏 / 玩家装饰底板（上屏）
- 下面 **1080×1080 的 `Main`** —— 中间那个圆形游玩区，**四角被机台挡住**

所以挂到整块画布右下角 = 画在圆形区域的死角里，看不见。HUD 现在挂到 `RearCanvas/Sub`
的右下角，也就是信息栏底板那块区域的右下角；`hudx/hudy` 就是相对这个角的偏移。

## 1P / 2P / 1P+2P 三种玩家构成

机台（或家用改装）可以只开 1P、只开 2P、或两边都开。游戏自己是用
`Manager.UserDataManager.GetUserData(i).IsEntry` 判断某一位玩家在不在场
（`Process.GameProcess.OnStart` 拿它去 `MonitorBase.Initialize(i, isEntry)`），
mod 直接沿用这个判据：

| 玩家构成 | 采样 | HUD 画在哪 |
|---|---|---|
| 只有 1P | 只统计 1P | `LeftMonitor/CommonProcess(Clone)/RearCanvas/Sub`（monitorIndex 0） |
| 只有 2P | 只统计 2P | `RightMonitor/CommonProcess(Clone)/RearCanvas/Sub`（monitorIndex 1） |
| 1P+2P | 两位都统计，数字是合计 | 两块屏都画 |

判据来自游戏本身，不靠猜画布名字；万一读不到入场状态（例如换版本后接口变了），会退到
`Monitor.GameMonitor.IsActive()`，再不行就两块屏都画，并在日志里写一行 `玩家构成：…`。
强制指定：`players=1p|2p|both|off`、`hudmonitor=1p|2p|both`，`hudcanvas=<画布名>` 可以完全绕过自动挑选。

`hudmonitor` 只决定「按玩家构成该画哪几块」。相机那一侧还盖着一层视口过滤：整机只有一台相机，
游戏会把它挪到当前活动的那块屏上（实测标题画面时相机对着 LeftMonitor，RightMonitor 整个落在窗口外），
画在相机看不到的半块上等于没画，所以被挡住的那块会自动跳过，日志里 `visible=False` 就是了。

> 单人/单侧机台里，2P 的按键与触摸数组会把同一块面板再报一遍；以前两边都累加会让里程翻倍。
> 现在「不在场的玩家根本不采样」，所以不用再靠 `track2p=0` 手动躲这个坑。

## 谱面缓存

启动时后台线程（`BelowNormal`）跑一次。缓存放在 `<Package>\Mods\maimaiKcalCalculator\charts.cache`，键是「谱面数量 + 总字节数 + 缓存版本」，所以游戏更新加了几首歌会自动失效重算。

本机 `SDEZ170` 是 7454 个 `.ma2`、57 MB，缓存里落在 7398 张（宴谱双人谱的 `_L`/`_R` 后缀和 0 音符的占位谱进不了缓存键）。
单线程从头解析要十几到二十秒，按 `ProcessorCount` 拆成多个线程（上限 8 个）之后几秒就完了；命中缓存时是毫秒级。
解析没完成之前选曲画面不显示数字，不会先闪一个占位的 0。

## 实时采样

`Tracker` 每帧读两处：

- 按键电平 `Manager.InputManager.PlayerButtonPush[player, 0..7]`，private static，反射取一次数组
- 触摸区电平 `Manager.InputManager.GetTouchPanelAreaPush(player, TouchPanelArea)`，公开接口，内部是位掩码，O(1)

只有「刚按下」那一帧记一次触点，按住不放不累加。一帧固定 2×(8+34) 次布尔读取，没有分配，没有 LINQ。HUD 也只在文字真的变了才 `SetText`。

「本轮」的边界不是「回到选曲画面」，是 `Manager.GamePlayManager.Instance.GetScoreListCount()`：同一轮里递增，换一轮回落，回落到小值才清零。舞萌每打完一首都回一次选曲，按画面判断会让本曲永远等于本轮。

「总计」则完全不看轮次：每首结束（`EndTrack`）往 `_totalMeters` 上加一次，正在打的这一曲由 `TotalMeters` 现算，
所以它只会单调增长，重启游戏（静态字段随进程重建）才归零。

## 日志

`<Package>\MelonLoader\Latest.log`，前缀 `[MaimaiKcal]`：

```
[MaimaiKcal] v0.1.0 载入
[MaimaiKcal] 谱面缓存命中：7398 张，用时 12 ms
[MaimaiKcal] 已挂钩 Process.MusicSelectProcess.OnUpdate
[MaimaiKcal] 已挂钩 Process.GameProcess.OnStart
[MaimaiKcal] 已挂钩 Process.GameProcess.OnRelease
[MaimaiKcal] 玩家构成：1P+2P（1P entry=1，2P entry=1）
[MaimaiKcal] HUD 挂到 1P(LeftMonitor): GameMain/LeftMonitor/CommonProcess(Clone)/RearCanvas/Sub  rect=1080x450  visible=True …
[MaimaiKcal] HUD 挂到 2P(RightMonitor): GameMain/RightMonitor/CommonProcess(Clone)/RearCanvas/Sub  rect=1080x450  visible=False …
[MaimaiKcal] 本曲结束：229.3 m → 16.1 kcal（1P 120.4 m / 2P 108.9 m，触点 812，用时 143 s）| 本轮累计 458.6 m | 启动以来总计 688.0 m / 48.2 kcal
```

## 已知限制

- `alpha` 是经验系数，别把 kcal 当医疗或训练数据用
- 触摸只有 34 个离散区，拿不到连续坐标，滑条按区中心连线近似，通常比真实手路径略短
- 卡片上的预估值不含宴谱面（utage 目前没做）
- 随机曲目的卡是游戏内部造的占位（`musicId=8` / 难度 0，True Love Song Basic），mod 靠 `isRandomScore` 认出来并隐藏，否则会对着随机卡显示一个 1.7 kcal 的假数字
- HUD 在「本曲有数据」或「总计 > 0」时显示：每曲开头本曲还是 0 也不会闪没了，但一旦打过一个音符，这次启动期间它就一直挂在屏上（想彻底关掉用 `showhud=0` 或重启游戏）
- 文字位置要按 `selectx/selecty` 和 `hudx/hudy` 自己调，不同分辨率可能要微调；1P/2P 两块屏各自独立挂载，偏移量相同
- 两块屏都显示同一组数字（本曲 / 本轮 / 总计的合计），不按玩家拆账：单侧机台里 2P 的输入可能是 1P 面板的镜像，拆开反而会出现两边都等于总和的假数字
- 结算画面的排版试过挂 `Monitor.ResultMonitor.Initialize`，挂钩和日志都正常但画不出来，原因没查明，最后改成常驻 HUD 绕过去了
