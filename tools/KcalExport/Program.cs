// maimaiKcalCalculator 离线导出工具：把游戏包里全部 .ma2 谱面的「预计消耗热量」算出来写成 JSON。
//
// 口径与 mod 选曲画面上那行「预计 12.3 kcal · 手部 210 m」完全同源：
//   手部里程 = Ma2.Parse(...)             —— src\Ma2.cs，链接进来的是同一份源码
//   平面几何 = Geo.Btn* / Geo.Zone*       —— src\Geo.cs，同上
//   kcal     = alpha × 体重(kg) × 里程(m) —— 与 src\Energy.cs 同一个公式，alpha/weight 从 cfg 读
//
// 为什么要再扫一遍原始文本：
//   1) 谱面末尾自带 T_REC_* / T_NUM_* 统计块（游戏自己数出来的音符数），可以拿来对账；
//   2) 独立按「记录名前缀/后缀」再数一遍每种记录，若和 Ma2 解析出的事件数不符，
//      说明 Ma2 静默丢过记录（字段数不够 / 位置越界），这是里程算错最可能的来源。
//   3) 顺手统计全包里出现过的所有记录名，确认没有「没被识别成音符」的类型。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MaimaiKcal;

namespace KcalExport
{
    internal static class Program
    {
        const string ToolName = "maimaiKcalCalculator/export-charts";
        const string ToolVersion = "1.0.0";

        /// <summary>基准文件的 kind，tools\KcalWeight 靠它认文件（也靠它拒绝把产物当基准再乘一次）。</summary>
        const string BaselineKind = "maimaiKcalCalculator/kcal-baseline";

        static readonly string[] DiffNames = { "BASIC", "ADVANCED", "EXPERT", "MASTER", "Re:MASTER" };

        // 文件名：000008_03.ma2 / 110281_00_L.ma2（宴谱双人谱会拆成 L / R 两份）
        static readonly Regex ChartName = new Regex(
            @"^(?<id>\d{6})_(?<diff>\d{2})(?:_(?<variant>[LR]))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,   // 和 meta 里的匿名对象保持一致
        };

        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // ------------------------------------------------------------------ 入口

        static int Main(string[] args)
        {
            string sa = null, outDir = null, cfg = null, server = "", mode = "charts";
            string alphaArg = null, weightArg = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--sa": sa = Next(args, ref i, a); break;
                    case "--out": outDir = Next(args, ref i, a); break;
                    case "--cfg": cfg = Next(args, ref i, a); break;
                    case "--server": server = Next(args, ref i, a); break;
                    case "--alpha": alphaArg = Next(args, ref i, a); break;
                    case "--weight": weightArg = Next(args, ref i, a); break;
                    case "--mode": mode = Next(args, ref i, a); break;
                    case "-h": case "--help": Usage(); return 0;
                    default:
                        Console.Error.WriteLine("未知参数: " + a);
                        Usage();
                        return 2;
                }
            }

            bool baselineMode = string.Equals(mode, "baseline", StringComparison.OrdinalIgnoreCase);
            if (!baselineMode && !string.Equals(mode, "charts", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("--mode 只认 charts / baseline: " + mode);
                return 2;
            }

            if (string.IsNullOrEmpty(sa)) { Console.Error.WriteLine("缺少 --sa <StreamingAssets>"); return 2; }
            if (string.IsNullOrEmpty(outDir)) { Console.Error.WriteLine("缺少 --out <输出目录>"); return 2; }

            string streamRoot = ResolveStreamingAssets(sa);
            if (streamRoot == null) { Console.Error.WriteLine("找不到 StreamingAssets: " + sa); return 2; }

            // ---- 换算系数：命令行 > cfg > 默认值（默认值与 src\Core.cs 的 Cfg 一致）
            double alpha = 1.0e-3, weight = 70.0;
            string alphaSrc = "默认值 1.0e-3 kcal/(kg·m)（src\\Core.cs Cfg.Alpha）";
            string weightSrc = "默认值 70 kg（src\\Core.cs Cfg.WeightKg）";

            if (!string.IsNullOrEmpty(cfg) && File.Exists(cfg))
            {
                double a, w;
                bool ha, hw;
                ReadCfg(cfg, out a, out w, out ha, out hw);
                if (ha) { alpha = a; alphaSrc = "cfg: " + cfg; }
                if (hw) { weight = w; weightSrc = "cfg: " + cfg; }
            }
            else if (!string.IsNullOrEmpty(cfg))
            {
                Console.WriteLine("[KcalExport] 提示：cfg 不存在，用默认系数: " + cfg);
            }

            if (alphaArg != null)
            {
                if (!double.TryParse(alphaArg, NumberStyles.Float, CultureInfo.InvariantCulture, out alpha))
                { Console.Error.WriteLine("--alpha 不是数字: " + alphaArg); return 2; }
                alphaSrc = "命令行 --alpha";
            }
            if (weightArg != null)
            {
                if (!double.TryParse(weightArg, NumberStyles.Float, CultureInfo.InvariantCulture, out weight))
                { Console.Error.WriteLine("--weight 不是数字: " + weightArg); return 2; }
                weightSrc = "命令行 --weight";
            }

            Console.WriteLine("[KcalExport] StreamingAssets: " + streamRoot);
            Console.WriteLine("[KcalExport] 模式: " + (baselineMode
                ? "baseline（只写与体重无关的 kcalPerKg，weight 不参与）"
                : "charts（写 charts.json / summary.json，weight 参与）"));
            Console.WriteLine("[KcalExport] 系数: alpha=" + F(alpha) + " kcal/(kg·m), weight=" + F(weight) + " kg");

            var swAll = Stopwatch.StartNew();

            // ---- 1. 找谱面文件
            var filesScanned = new List<string>();
            var skipped = new List<object>();
            var charts0 = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string f in Enumerate(streamRoot, "*.ma2*"))
            {
                if (!seen.Add(f)) continue;
                filesScanned.Add(f);

                if (!string.Equals(Path.GetExtension(f), ".ma2", StringComparison.OrdinalIgnoreCase))
                {
                    // 包里有极少数 .ma21 之类残留，游戏按 Music.xml 里的精确路径加载，用不到
                    skipped.Add(new { file = Rel(streamRoot, f), reason = "扩展名不是 .ma2（游戏不加载）" });
                    continue;
                }
                if (!ChartName.IsMatch(Path.GetFileNameWithoutExtension(f)))
                {
                    skipped.Add(new { file = Rel(streamRoot, f), reason = "文件名不符合 <6 位曲目编号>_<2 位难度>[_L|_R].ma2" });
                    continue;
                }
                charts0.Add(f);
            }

            Console.WriteLine(string.Format("[KcalExport] 扫到 {0:N0} 个 ma2 族文件，其中谱面 {1:N0} 个、跳过 {2} 个",
                filesScanned.Count, charts0.Count, skipped.Count));

            // ---- 2. 并行解析（Ma2.Parse 只读静态几何表，天然线程安全）
            var swParse = Stopwatch.StartNew();
            var raws = new ConcurrentBag<Raw>();
            var songCache = new ConcurrentDictionary<string, SongMeta>(StringComparer.OrdinalIgnoreCase);
            var xmlErrors = new ConcurrentBag<object>();
            var tokenCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

            int done = 0;
            Parallel.ForEach(charts0, f =>
            {
                int n = Interlocked.Increment(ref done);
                if (n % 1500 == 0) Console.WriteLine("  解析中 … " + n.ToString("N0") + " / " + charts0.Count.ToString("N0"));

                Match m = ChartName.Match(Path.GetFileNameWithoutExtension(f));
                int musicId = int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture);
                int diff = int.Parse(m.Groups["diff"].Value, CultureInfo.InvariantCulture);
                string variant = m.Groups["variant"].Success ? m.Groups["variant"].Value.ToUpperInvariant() : null;

                string dir = Path.GetDirectoryName(f);
                SongMeta song = songCache.GetOrAdd(dir, d => LoadSong(d, xmlErrors));

                ChartText ct = ScanChartText(f, tokenCounts);

                raws.Add(new Raw
                {
                    Path = f,
                    Rel = Rel(streamRoot, f),
                    Bytes = Length(f),
                    MusicId = musicId,
                    Diff = diff,
                    Variant = variant,
                    Stat = Ma2.Parse(f),
                    Header = ct.Header,
                    Walk = ct.Walk,
                    Song = song,
                });
            });
            long parseMs = swParse.ElapsedMilliseconds;
            Console.WriteLine(string.Format("[KcalExport] 解析完成：{0:N0} 张，用时 {1:N0} ms", raws.Count, parseMs));

            // ---- 3. 去重（同一 曲目+难度+变体 出现在多个 A0xx 包里时，保留字节数最大的那份）
            var groups = raws.GroupBy(r => Key(r.MusicId, r.Diff, r.Variant)).ToList();
            var keep = new List<Raw>(groups.Count);
            var duplicates = new List<object>();

            foreach (var g in groups)
            {
                if (g.Count() == 1) { keep.Add(g.First()); continue; }

                var ordered = g.OrderByDescending(r => r.Bytes).ThenBy(r => r.Rel, StringComparer.Ordinal).ToList();
                Raw kept = ordered[0];
                keep.Add(kept);

                string keptHash = Sha(kept.Path);
                var dropped = new List<object>();
                foreach (Raw d in ordered.Skip(1))
                {
                    dropped.Add(new
                    {
                        file = d.Rel,
                        bytes = d.Bytes,
                        identicalToKept = string.Equals(Sha(d.Path), keptHash, StringComparison.OrdinalIgnoreCase),
                    });
                }
                duplicates.Add(new { key = Key(kept.MusicId, kept.Diff, kept.Variant), kept = kept.Rel, dropped = dropped });
            }

            if (duplicates.Count > 0)
                Console.WriteLine(string.Format("[KcalExport] 同键重复 {0} 组（已去重）", duplicates.Count));

            // ---- 4. 组装记录
            var charts = new List<ChartDto>(keep.Count);
            foreach (Raw r in keep)
            {
                SongMeta song = r.Song;
                NotesMeta nm = song != null ? song.NotesFor(Path.GetFileName(r.Path)) : null;

                bool isUtage = r.MusicId >= 100000 || (song != null && song.UtagePlayStyle > 0);
                string diffName = isUtage
                    ? "UTAGE"
                    : (r.Diff < DiffNames.Length ? DiffNames[r.Diff] : "DIFF_" + r.Diff.ToString(CultureInfo.InvariantCulture));

                int? level = null, levelDec = null, levelId = null;
                if (nm != null && nm.HasLevel)
                {
                    level = nm.Level;
                    levelDec = nm.LevelDecimal;
                    if (nm.HasLevelId) levelId = nm.MusicLevelId;
                }

                int? gameNotes = null;
                if (nm != null && nm.HasMaxNotes) gameNotes = nm.MaxNotes;
                else if (r.Header.Present && r.Header.NumAll > 0) gameNotes = r.Header.NumAll;

                charts.Add(new ChartDto
                {
                    MusicId = r.MusicId,
                    Difficulty = r.Diff,
                    DifficultyName = diffName,
                    IsUtage = isUtage,
                    UtageName = isUtage && song != null && !string.IsNullOrEmpty(song.UtageKanjiName) ? song.UtageKanjiName : null,
                    Variant = r.Variant,
                    Enabled = nm != null && nm.HasEnabled ? nm.IsEnabled : (bool?)null,
                    Title = song != null && !string.IsNullOrEmpty(song.Title) ? song.Title : null,
                    Artist = song != null && !string.IsNullOrEmpty(song.Artist) ? song.Artist : null,
                    Genre = song != null && !string.IsNullOrEmpty(song.Genre) ? song.Genre : null,
                    Version = song != null && !string.IsNullOrEmpty(song.Version) ? song.Version : null,
                    Bpm = song != null ? song.Bpm : null,
                    Level = level,
                    LevelDecimal = levelDec,
                    LevelText = level.HasValue ? LevelText(level.Value, levelDec ?? 0, levelId ?? 0) : null,
                    Designer = nm != null && !string.IsNullOrEmpty(nm.Designer) ? nm.Designer : null,
                    Notes = r.Stat.Notes,
                    TouchNotes = r.Stat.Touches,
                    GameNotes = gameNotes,
                    Meters = Round3(r.Stat.Meters),
                    KcalPerKg = alpha * r.Stat.Meters,       // 与体重无关：乘以体重就是 kcal
                    Kcal = Round3(alpha * weight * r.Stat.Meters),
                    File = r.Rel,
                });
            }

            charts.Sort((a, b) =>
            {
                int c = a.MusicId.CompareTo(b.MusicId);
                if (c != 0) return c;
                c = a.Difficulty.CompareTo(b.Difficulty);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Variant ?? "", b.Variant ?? "");
            });

            // ---- 4.5 基准模式：只写与体重无关的 kcalPerKg（= alpha × 里程），
            //          体重由 tools\KcalWeight 乘进来，同一份基准能出任意体重的表
            if (baselineMode)
                return WriteBaseline(outDir, streamRoot, server, filesScanned.Count,
                                     alpha, alphaSrc, charts, swAll.ElapsedMilliseconds);

            // ---- 5. 汇总
            var meta = new
            {
                generatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                tool = ToolName + " v" + ToolVersion,
                source = new
                {
                    streamingAssets = streamRoot,
                    server = string.IsNullOrEmpty(server) ? null : server,
                    ma2FilesScanned = filesScanned.Count,
                },
                model = new
                {
                    formula = "kcal = alpha × weightKg × meters",
                    alpha = alpha,
                    weightKg = weight,
                    alphaSource = alphaSrc,
                    weightSource = weightSrc,
                },
                fields = new
                {
                    meters = "手部里程（米），与 mod 选曲画面「预计」同一份算法（src/Ma2.cs）",
                    kcal = "alpha × weightKg × meters，与 src/Energy.cs 同一公式",
                    notes = "解析到的音符事件数：TAP/HLD/BRK/EXTAP/BRTAP/BXTAP/XTP/XHO + 滑条体 + 触摸（一条滑条算 1 次，起手不重复算）",
                    touchNotes = "触摸音符数（TTP + THO）",
                    gameNotes = "游戏口径的音符数（Music.xml 的 maxNotes）。新格式谱面游戏内部按判定单元计数，和 notes 不等是正常的",
                    enabled = "Music.xml 的 isEnable。false = 游戏里不会出现的占位条目（盘上仍有 .ma2 文件，比如 [X] 那种 16 个音的壳）",
                    level = "Music.xml 的 level + levelDecimal（定数小数）",
                    levelText = "游戏卡面上显示的等级，按 musicLevelID 反推（1..6 无 +，7 起 7/7+/8/8+…）；" +
                                "有 16 张谱的 levelDecimal 与 musicLevelID 打架，以 musicLevelID 为准",
                },
                metersDefinition = new
                {
                    summary = "手部里程，与 mod 选曲画面「预计」同一份算法",
                    buttons = "8 个键心在 R=310 mm 圆上，45° 等分，1 号键右上顺时针；点对点走弦长",
                    slides = "沿按键环的弧长，跨 d 格 = d × 45°",
                    touches = "34 个触摸区中心点（TouchPannelSVG.txt 换算）",
                    hands = "两只手按「离上一手位最近」贪心分配",
                },
                difficultyNames = HasUtage(charts)
                    ? new[] { "BASIC", "ADVANCED", "EXPERT", "MASTER", "Re:MASTER", "UTAGE" }
                    : DiffNames,
                caveat = "alpha 是未标定的经验系数（默认 1.0e-3），里程可信但 kcal 绝对误差约 ±30–50%。" +
                         "宴谱面原本不在 mod 的选曲预估值里（mod 的缓存键放不下 _L/_R），这里一并导出。",
            };

            // ---- 6. 写文件
            Directory.CreateDirectory(outDir);

            string chartsPath = Path.Combine(outDir, "charts.json");
            File.WriteAllText(chartsPath,
                JsonSerializer.Serialize(new { meta = meta, charts = charts }, JsonOpts) + Environment.NewLine,
                Utf8NoBom);

            var summary = BuildSummary(meta, charts, keep, skipped, duplicates, xmlErrors.ToList(),
                                       parseMs, tokenCounts);
            string summaryPath = Path.Combine(outDir, "summary.json");
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, JsonOpts) + Environment.NewLine, Utf8NoBom);

            // ---- 7. 控制台小结
            var kcals = charts.Select(c => c.Kcal).OrderBy(x => x).ToList();
            int walkOk = keep.Count(r => r.Walk.Total == r.Stat.Notes && r.Walk.Touch == r.Stat.Touches);
            int unknown = tokenCounts.Keys.Count(t => Categorize(t) == "unknown");

            Console.WriteLine();
            Console.WriteLine("[KcalExport] 谱面 " + charts.Count.ToString("N0") +
                              " 张 / 曲目 " + charts.Select(c => c.MusicId).Distinct().Count().ToString("N0") + " 首");
            Console.WriteLine("[KcalExport] 交叉校验：独立计数与 Ma2 完全一致的谱面 " + walkOk.ToString("N0") +
                              " / " + charts.Count.ToString("N0") +
                              "；未能归类的记录名 " + unknown + " 种");
            Console.WriteLine("[KcalExport] 单谱热量：中位数 " + F(Pct(kcals, 0.50)) + " kcal，最大 " +
                              F(kcals.Count > 0 ? kcals[kcals.Count - 1] : 0) + " kcal");
            Console.WriteLine("[KcalExport] 全部谱面热量合计 " + F(Math.Round(charts.Sum(c => c.Kcal), 1)) + " kcal");
            Console.WriteLine("[KcalExport] 写出: " + chartsPath);
            Console.WriteLine("[KcalExport] 写出: " + summaryPath);
            Console.WriteLine("[KcalExport] 总用时 " + swAll.ElapsedMilliseconds.ToString("N0") + " ms");
            return 0;
        }

        // ------------------------------------------------------------------ 基准（与体重无关）

        /// <summary>
        /// 写 kcal-baseline.json：每张谱「每公斤体重消耗多少 kcal」= alpha × 手部里程。
        /// 体重完全不参与，所以乘任意体重都不会和这份文件打架。
        /// kcalPerKg 用完整双精度（不取整）：乘上体重再四舍五入到 3 位小数，
        /// weight = 70 时与 charts.json 的 kcal 逐张相同（tools\KcalWeight 的自检就是这么比的）。
        /// </summary>
        static int WriteBaseline(string outDir, string streamRoot, string server, int filesScanned,
                                 double alpha, string alphaSrc, List<ChartDto> charts, long elapsedMs)
        {
            var rows = new List<object>(charts.Count);
            foreach (ChartDto c in charts)
            {
                rows.Add(new
                {
                    musicId = c.MusicId,
                    difficulty = c.Difficulty,
                    difficultyName = c.DifficultyName,
                    isUtage = c.IsUtage,
                    utageName = c.UtageName,
                    variant = c.Variant,
                    enabled = c.Enabled,
                    title = c.Title,
                    artist = c.Artist,
                    genre = c.Genre,
                    version = c.Version,
                    bpm = c.Bpm,
                    level = c.Level,
                    levelDecimal = c.LevelDecimal,
                    levelText = c.LevelText,
                    designer = c.Designer,
                    notes = c.Notes,
                    touchNotes = c.TouchNotes,
                    gameNotes = c.GameNotes,
                    meters = c.Meters,
                    kcalPerKg = c.KcalPerKg,
                    file = c.File,
                });
            }

            var kgs = charts.Select(c => c.KcalPerKg).OrderBy(x => x).ToList();

            var meta = new
            {
                kind = BaselineKind,
                schemaVersion = 1,
                generatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                tool = ToolName + " v" + ToolVersion + " (baseline)",
                source = new
                {
                    streamingAssets = streamRoot,
                    server = string.IsNullOrEmpty(server) ? null : server,
                    ma2FilesScanned = filesScanned,
                },
                model = new
                {
                    formula = "kcal = kcalPerKg × weightKg；kcalPerKg = alpha × meters",
                    kcalPerKg = "每公斤体重的热量（kcal/kg），已经把 alpha 折进去了；乘上体重就是这张谱的预计消耗",
                    alpha = alpha,
                    alphaSource = alphaSrc,
                },
                counts = new
                {
                    charts = charts.Count,
                    songs = charts.Select(c => c.MusicId).Distinct().Count(),
                    songsExcludingUtage = charts.Where(c => !c.IsUtage).Select(c => c.MusicId).Distinct().Count(),
                    utageCharts = charts.Count(c => c.IsUtage),
                },
                fields = new
                {
                    kcalPerKg = "alpha × meters，保留完整双精度（不取整），乘完体重再四舍五入到 3 位小数",
                    meters = "手部里程（米），与 mod 选曲画面「预计」同一份算法（src/Ma2.cs）",
                    notes = "解析到的音符事件数：TAP/HLD/BRK/EXTAP/BRTAP/BXTAP/XTP/XHO + 滑条体 + 触摸（一条滑条算 1 次，起手不重复算）",
                    touchNotes = "触摸音符数（TTP + THO）",
                    gameNotes = "游戏口径的音符数（Music.xml 的 maxNotes）。新格式谱面游戏内部按判定单元计数，和 notes 不等是正常的",
                    enabled = "Music.xml 的 isEnable。false = 游戏里不会出现的占位条目",
                    level = "Music.xml 的 level + levelDecimal（定数小数）",
                    levelText = "游戏卡面上显示的等级，按 musicLevelID 反推（1..6 无 +，7 起 7/7+/8/8+…）",
                },
                next = "按体重出 kcal：.\\export-weight.ps1 -Weight 65  →  dataout\\kcal\\65kg.json" +
                       "（也可以直接跑 dotnet run --project tools\\KcalWeight -- --baseline <基准> --weight 65）",
                caveat = "alpha 是未标定的经验系数（默认 1.0e-3），里程可信但 kcal 绝对误差约 ±30–50%；" +
                         "换 alpha 要重新出基准（export-baseline.ps1 -Alpha …），别在乘体重那一层改。",
            };

            Directory.CreateDirectory(outDir);
            string path = Path.Combine(outDir, "kcal-baseline.json");
            File.WriteAllText(path,
                JsonSerializer.Serialize(new { meta = meta, charts = rows }, JsonOpts) + Environment.NewLine,
                Utf8NoBom);

            Console.WriteLine();
            Console.WriteLine("[KcalExport] 基准 " + charts.Count.ToString("N0") + " 张谱 / " +
                              meta.counts.songs.ToString("N0") + " 首（不含体重，只有 kcalPerKg）");
            Console.WriteLine("[KcalExport] kcalPerKg：中位数 " + F(Pct(kgs, 0.50)) + "，最大 " +
                              F(kgs.Count > 0 ? kgs[kgs.Count - 1] : 0) + " kcal/kg（= 70 kg 时中位数 " +
                              F(Math.Round(Pct(kgs, 0.50) * 70, 3)) + " kcal、最大 " +
                              F(Math.Round((kgs.Count > 0 ? kgs[kgs.Count - 1] : 0) * 70, 3)) + " kcal）");
            Console.WriteLine("[KcalExport] 写出: " + path);
            Console.WriteLine("[KcalExport] 总用时 " + elapsedMs.ToString("N0") + " ms");
            return 0;
        }

        // ------------------------------------------------------------------ 数据结构

        sealed class Raw
        {
            public string Path;
            public string Rel;
            public long Bytes;
            public int MusicId;
            public int Diff;
            public string Variant;
            public Ma2.Stat Stat;
            public Header Header;
            public Walk Walk;
            public SongMeta Song;
        }

        /// <summary>谱面自己写在末尾的统计块（T_REC_* / T_NUM_*），游戏自己数出来的数。</summary>
        sealed class Header
        {
            public bool Present;
            public int Tap, Brk, Xtp, Hld, Xho, Bxx, Bho, Bxh, Str, Bst, Xst, Xbs, Bsl, Ttp, Tho, Sld, NumAll;

            /// <summary>按钮类（含 break / ex / bx）应等于这些 T_REC 之和。</summary>
            public int ButtonEvents { get { return Tap + Xtp + Hld + Xho + Brk + Bxx + Bho + Bxh; } }
            public int TouchEvents { get { return Ttp + Tho; } }
            public int SlideRecords { get { return Sld + Bsl; } }
        }

        /// <summary>独立地按记录名数一遍（不看 Ma2 的结果），用于交叉校验。</summary>
        sealed class Walk
        {
            public int Button, Touch, SlideBody, SlideHead, Metadata;
            public int Total { get { return Button + Touch + SlideBody; } }
        }

        sealed class ChartText
        {
            public Header Header = new Header();
            public Walk Walk = new Walk();
        }

        sealed class NotesMeta
        {
            public string File;
            public int Index;
            public int Level;
            public int LevelDecimal;
            public int MusicLevelId;
            public int MaxNotes;
            public bool HasLevel;
            public bool HasLevelId;
            public bool HasMaxNotes;
            public bool IsEnabled;
            public bool HasEnabled;
            public string Designer;
        }

        sealed class SongMeta
        {
            public string DirName;
            public string Title;
            public string Artist;
            public string Genre;
            public string Version;
            public int UtagePlayStyle;
            public string UtageKanjiName;
            public int? Bpm;
            public readonly List<NotesMeta> Notes = new List<NotesMeta>();
            readonly Dictionary<string, NotesMeta> _byFile = new Dictionary<string, NotesMeta>(StringComparer.OrdinalIgnoreCase);

            public void Add(NotesMeta n)
            {
                if (string.IsNullOrEmpty(n.File)) return;
                string k = n.File.Trim();
                if (!_byFile.ContainsKey(k)) _byFile[k] = n;
                Notes.Add(n);
            }

            public NotesMeta NotesFor(string fileName)
            {
                NotesMeta n;
                if (_byFile.TryGetValue(fileName, out n)) return n;

                // 宴谱双人谱：Music.xml 里写的是 110281_00.ma2，盘上只有 110281_00_L / _R
                string stem = Path.GetFileNameWithoutExtension(fileName);
                int us = stem.LastIndexOf('_');
                if (us > 0)
                {
                    string tail = stem.Substring(us + 1);
                    if (tail.Length == 1 && (tail[0] == 'L' || tail[0] == 'R'))
                    {
                        if (_byFile.TryGetValue(stem.Substring(0, us) + ".ma2", out n)) return n;
                    }
                }
                return null;
            }
        }

        sealed class ChartDto
        {
            public int MusicId { get; set; }
            public int Difficulty { get; set; }
            public string DifficultyName { get; set; }
            public bool IsUtage { get; set; }
            public string UtageName { get; set; }
            public string Variant { get; set; }
            public bool? Enabled { get; set; }
            public string Title { get; set; }
            public string Artist { get; set; }
            public string Genre { get; set; }
            public string Version { get; set; }
            public int? Bpm { get; set; }
            public int? Level { get; set; }
            public int? LevelDecimal { get; set; }
            public string LevelText { get; set; }
            public string Designer { get; set; }
            public int Notes { get; set; }
            public int TouchNotes { get; set; }
            public int? GameNotes { get; set; }
            public double Meters { get; set; }

            /// <summary>
            /// 每公斤体重的热量（kcal/kg）= alpha × 未取整的里程。
            /// 只用在「基准」导出里；charts.json 不写这个字段（JsonIgnore），
            /// 这样同一个 DTO 能出两种文件，而且 charts.json 的格式不变。
            /// </summary>
            [JsonIgnore]
            public double KcalPerKg { get; set; }

            public double Kcal { get; set; }
            public string File { get; set; }
        }

        // ------------------------------------------------------------------ 原始文本扫描

        /// <summary>
        /// 一趟扫完：读 T_REC_/T_NUM_ 表头 + 按记录名独立数音符 + 累计全包记录名清单。
        /// 不用 Ma2 的解析结果，这样两边才能互相印证。
        /// </summary>
        static ChartText ScanChartText(string path, ConcurrentDictionary<string, int> tokenCounts)
        {
            var ct = new ChartText();
            string text;
            try { text = File.ReadAllText(path); }
            catch { return ct; }

            int n = text.Length, i = 0;
            while (i < n)
            {
                int eol = text.IndexOf('\n', i);
                if (eol < 0) eol = n;

                int tab = -1;
                for (int k = i; k < eol; k++) { if (text[k] == '\t') { tab = k; break; } }
                if (tab > i)
                {
                    string tok = text.Substring(i, tab - i);
                    tokenCounts.AddOrUpdate(tok, 1, (k, v) => v + 1);

                    switch (Categorize(tok))
                    {
                        case "button": ct.Walk.Button++; break;
                        case "touch": ct.Walk.Touch++; break;
                        case "slideBody": ct.Walk.SlideBody++; break;
                        case "slideHead": ct.Walk.SlideHead++; break;
                        default: ct.Walk.Metadata++; break;
                    }

                    if (tok.Length > 6 && tok[0] == 'T' && tok[1] == '_' && tok[2] == 'R' && tok[3] == 'E' && tok[4] == 'C' && tok[5] == '_')
                    {
                        ct.Header.Present = true;
                        int v = IntAt(text, tab + 1, eol);
                        switch (tok)
                        {
                            case "T_REC_TAP": ct.Header.Tap = v; break;
                            case "T_REC_BRK": ct.Header.Brk = v; break;
                            case "T_REC_XTP": ct.Header.Xtp = v; break;
                            case "T_REC_HLD": ct.Header.Hld = v; break;
                            case "T_REC_XHO": ct.Header.Xho = v; break;
                            case "T_REC_BXX": ct.Header.Bxx = v; break;
                            case "T_REC_BHO": ct.Header.Bho = v; break;
                            case "T_REC_BXH": ct.Header.Bxh = v; break;
                            case "T_REC_STR": ct.Header.Str = v; break;
                            case "T_REC_BST": ct.Header.Bst = v; break;
                            case "T_REC_XST": ct.Header.Xst = v; break;
                            case "T_REC_XBS": ct.Header.Xbs = v; break;
                            case "T_REC_BSL": ct.Header.Bsl = v; break;
                            case "T_REC_TTP": ct.Header.Ttp = v; break;
                            case "T_REC_THO": ct.Header.Tho = v; break;
                            case "T_REC_SLD": ct.Header.Sld = v; break;
                            case "T_NUM_ALL": ct.Header.NumAll = v; break;
                        }
                    }
                }
                i = eol + 1;
            }
            return ct;
        }

        /// <summary>
        /// 记录名归类。规则与 src\Ma2.cs 的 Classify 一致（先特判 BRK，再剥 NM/EX/BX/BR/CN 前缀、
        /// X 只在 XTP/XHO/XST/XBS 这类里剥），但这里是按名字数「出现了多少条」，不解析字段。
        /// </summary>
        static string Categorize(string tok)
        {
            if (tok == "BRK") return "button";                       // 旧版 break，没前缀没后缀 K
            if (tok.StartsWith("T_", StringComparison.Ordinal)) return "metadata";
            if (tok.StartsWith("TTM_", StringComparison.Ordinal)) return "metadata";

            switch (tok)
            {
                case "VERSION": case "FES_MODE": case "BPM": case "BPM_DEF":
                case "MET": case "MET_DEF": case "RESOLUTION": case "CLK": case "CLK_DEF":
                case "COMPATIBLE_CODE": case "SEF": case "GENERATED_BY":
                    return "metadata";
            }

            string s = tok;
            if (s.Length > 2 && (s.StartsWith("NM", StringComparison.Ordinal) || s.StartsWith("EX", StringComparison.Ordinal) ||
                                 s.StartsWith("BX", StringComparison.Ordinal) || s.StartsWith("BR", StringComparison.Ordinal) ||
                                 s.StartsWith("CN", StringComparison.Ordinal)))
                s = s.Substring(2);
            else if (s.Length > 1 && s[0] == 'X' && (s[1] == 'T' || s[1] == 'H' || s[1] == 'S' || s[1] == 'B'))
                s = s.Substring(1);
            else if (s == "BST") return "slideHead";                 // break 滑条起手

            if (s.StartsWith("TA", StringComparison.Ordinal) || s.StartsWith("TP", StringComparison.Ordinal) ||
                s.StartsWith("HL", StringComparison.Ordinal) || s.StartsWith("HO", StringComparison.Ordinal))
                return "button";
            if (s.StartsWith("TT", StringComparison.Ordinal) || s.StartsWith("TH", StringComparison.Ordinal))
                return "touch";
            if (s.StartsWith("ST", StringComparison.Ordinal))
                return "slideHead";
            if (s.Length > 0 && s[0] == 'S')
                return "slideBody";
            return "unknown";
        }

        // ------------------------------------------------------------------ Music.xml

        static SongMeta LoadSong(string musicDir, ConcurrentBag<object> errors)
        {
            var s = new SongMeta { DirName = Path.GetFileName(musicDir) };
            string xml = Path.Combine(musicDir, "Music.xml");
            if (!File.Exists(xml)) return s;

            try
            {
                XElement root = XDocument.Parse(ReadXml(musicDir)).Root;
                if (root == null) return s;

                s.Title = Str(root, "name");
                s.Artist = Str(root, "artistName");
                s.Genre = Str(root, "genreName");
                s.Version = Str(root, "AddVersion");
                s.UtageKanjiName = Clean(root.Element("utageKanjiName"));

                int v;
                if (int.TryParse(Clean(root.Element("utagePlayStyle")), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                    s.UtagePlayStyle = v;

                int bpm;
                if (int.TryParse(Clean(root.Element("bpm")), NumberStyles.Integer, CultureInfo.InvariantCulture, out bpm))
                    s.Bpm = bpm;

                XElement notesData = root.Element("notesData");
                if (notesData != null)
                {
                    int idx = 0;
                    foreach (XElement ne in notesData.Elements("Notes"))
                    {
                        var nm = new NotesMeta { Index = idx++ };
                        XElement file = ne.Element("file");
                        nm.File = file != null ? Clean(file.Element("path")) : null;
                        nm.Designer = Str(ne, "notesDesigner");

                        int level, dec, max, lid;
                        if (int.TryParse(Clean(ne.Element("level")), NumberStyles.Integer, CultureInfo.InvariantCulture, out level))
                        { nm.Level = level; nm.HasLevel = true; }
                        if (int.TryParse(Clean(ne.Element("levelDecimal")), NumberStyles.Integer, CultureInfo.InvariantCulture, out dec))
                            nm.LevelDecimal = dec;
                        if (int.TryParse(Clean(ne.Element("maxNotes")), NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
                        { nm.MaxNotes = max; nm.HasMaxNotes = true; }
                        if (int.TryParse(Clean(ne.Element("musicLevelID")), NumberStyles.Integer, CultureInfo.InvariantCulture, out lid))
                        { nm.MusicLevelId = lid; nm.HasLevelId = true; }

                        string enable = Clean(ne.Element("isEnable"));
                        if (!string.IsNullOrEmpty(enable))
                        {
                            nm.HasEnabled = true;
                            nm.IsEnabled = string.Equals(enable, "true", StringComparison.OrdinalIgnoreCase);
                        }

                        s.Add(nm);
                    }
                }
            }
            catch (Exception e)
            {
                errors.Add(new { file = xml, error = e.GetType().Name + ": " + e.Message });
            }
            return s;
        }

        /// <summary>取 &lt;k&gt;&lt;str&gt;…&lt;/str&gt;&lt;/k&gt; 里的 str。</summary>
        static string Str(XElement parent, string name)
        {
            XElement e = parent.Element(name);
            if (e == null) return null;
            XElement s = e.Element("str");
            return s != null ? Clean(s) : Clean(e);
        }

        static string Clean(XElement e)
        {
            if (e == null) return null;
            string v = e.Value;
            return v == null ? null : v.Trim();
        }

        /// <summary>Music.xml 一律 UTF-8（本机 1927 个全部能被严格 UTF-8 解出），带不带 BOM 都认。</summary>
        static string ReadXml(string musicDir)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(musicDir, "Music.xml"));
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return new UTF8Encoding(false).GetString(bytes);
        }

        // ------------------------------------------------------------------ 汇总

        static object BuildSummary(object meta, List<ChartDto> charts, List<Raw> raws,
                                   List<object> skipped, List<object> duplicates, List<object> xmlErrors,
                                   long parseMs, ConcurrentDictionary<string, int> tokenCounts)
        {
            var kcals = charts.Select(c => c.Kcal).OrderBy(x => x).ToList();
            var meters = charts.Select(c => c.Meters).OrderBy(x => x).ToList();

            var byDiff = new List<object>();
            foreach (string name in new[] { "BASIC", "ADVANCED", "EXPERT", "MASTER", "Re:MASTER", "UTAGE" })
            {
                var g = charts.Where(c => c.DifficultyName == name).ToList();
                if (g.Count == 0) continue;
                var k = g.Select(c => c.Kcal).OrderBy(x => x).ToList();
                var mm = g.Select(c => c.Meters).OrderBy(x => x).ToList();
                byDiff.Add(new
                {
                    difficulty = name,
                    charts = g.Count,
                    kcalMin = k[0],
                    kcalP50 = Pct(k, 0.50),
                    kcalP90 = Pct(k, 0.90),
                    kcalMax = k[k.Count - 1],
                    kcalSum = Math.Round(g.Sum(c => c.Kcal), 1),
                    metersMin = mm[0],
                    metersP50 = Pct(mm, 0.50),
                    metersP90 = Pct(mm, 0.90),
                    metersMax = mm[mm.Count - 1],
                });
            }

            var topCharts = charts
                .OrderByDescending(c => c.Kcal).Take(30)
                .Select(c => new
                {
                    musicId = c.MusicId,
                    difficulty = c.DifficultyName,
                    utageName = c.UtageName,
                    variant = c.Variant,
                    title = c.Title,
                    kcal = c.Kcal,
                    meters = c.Meters,
                    notes = c.Notes,
                    gameNotes = c.GameNotes,
                    level = c.LevelText,
                })
                .ToList();

            var topSongs = charts
                .Where(c => !c.IsUtage)
                .GroupBy(c => c.MusicId)
                .Select(g => new
                {
                    musicId = g.Key,
                    title = g.First().Title,
                    charts = g.Count(),
                    kcalSum = Round3(g.Sum(c => c.Kcal)),
                    kcalMax = Round3(g.Max(c => c.Kcal)),
                })
                .OrderByDescending(x => x.kcalSum).Take(25)
                .ToList();

            // ---- 交叉校验 1：独立按下记录名数出来的音符数 vs Ma2 解析结果
            int walkBothOk = 0, walkNotesOk = 0, walkTouchOk = 0;
            var walkBad = new List<object>();
            foreach (Raw r in raws)
            {
                bool notesOk = r.Walk.Total == r.Stat.Notes;
                bool touchOk = r.Walk.Touch == r.Stat.Touches;
                if (notesOk) walkNotesOk++;
                if (touchOk) walkTouchOk++;
                if (notesOk && touchOk) walkBothOk++;
                else if (walkBad.Count < 20)
                {
                    walkBad.Add(new
                    {
                        file = r.Rel,
                        parsedNotes = r.Stat.Notes,
                        countedNotes = r.Walk.Total,
                        parsedTouch = r.Stat.Touches,
                        countedTouch = r.Walk.Touch,
                        button = r.Walk.Button,
                        slideBody = r.Walk.SlideBody,
                        slideHead = r.Walk.SlideHead,
                    });
                }
            }

            // ---- 交叉校验 2：独立计数 / Ma2 结果 vs 谱面自己的 T_REC_* 表头
            int headerButtonOk = 0, headerTouchOk = 0, headerPresent = 0;
            var headerBad = new List<object>();
            foreach (Raw r in raws)
            {
                if (!r.Header.Present) continue;
                headerPresent++;
                bool btnOk = r.Walk.Button == r.Header.ButtonEvents;
                bool tchOk = r.Walk.Touch == r.Header.TouchEvents;
                if (btnOk) headerButtonOk++;
                if (tchOk) headerTouchOk++;
                if ((!btnOk || !tchOk) && headerBad.Count < 20)
                {
                    headerBad.Add(new
                    {
                        file = r.Rel,
                        countedButton = r.Walk.Button,
                        headerButton = r.Header.ButtonEvents,
                        countedTouch = r.Walk.Touch,
                        headerTouch = r.Header.TouchEvents,
                        headerSlideRecords = r.Header.SlideRecords,
                        slideBodyEvents = r.Walk.SlideBody,
                    });
                }
            }

            // ---- 交叉校验 3：等级文案。musicLevelID 是游戏自己的等级编号，
            //      与 level+levelDecimal 推出来的 n+ 规则应当一一对应（对不上说明游戏数据本身有偏差）
            //      注意：等级 1~6 没有 + 档（本机数据里 6.9 的谱面 level id 仍然是 6），所以 + 只在 7 级以上出现。
            int levelTextOk = 0, levelTextBadCount = 0;
            var levelBad = new List<object>();
            foreach (ChartDto c in charts)
            {
                if (!c.Level.HasValue) continue;
                string byDecimal = c.Level.Value.ToString(CultureInfo.InvariantCulture) +
                                   ((c.Level.Value >= 7 && c.LevelDecimal.GetValueOrDefault() >= 6) ? "+" : "");

                if (string.Equals(byDecimal, c.LevelText, StringComparison.Ordinal)) levelTextOk++;
                else
                {
                    levelTextBadCount++;
                    if (levelBad.Count < 20)
                        levelBad.Add(new { file = c.File, level = c.Level, levelDecimal = c.LevelDecimal, levelText = c.LevelText, byDecimal = byDecimal });
                }
            }

            var inventory = tokenCounts
                .Select(kv => new { token = kv.Key, category = Categorize(kv.Key), lines = kv.Value })
                .OrderByDescending(x => x.lines)
                .ToList();
            var unknownTokens = inventory.Where(x => x.category == "unknown").ToList();
            var categoryTotals = inventory
                .GroupBy(x => x.category)
                .Select(g => new { category = g.Key, kinds = g.Count(), lines = g.Sum(x => x.lines) })
                .OrderByDescending(x => x.lines)
                .ToList();

            var zeroNotes = charts.Where(c => c.Notes == 0).ToList();
            // 里程 0 不是 bug：包里有几张「全场都在同一个位置」的谱面（比如 854 全世界共通リズム感テスト、
            // 以及 [X] 那种占位谱），手确实没动
            var zeroMovement = charts
                .Where(c => c.Notes > 0 && c.Meters <= 0)
                .OrderBy(c => c.MusicId)
                .ToList();
            var disabled = charts.Where(c => c.Enabled == false).ToList();
            var noTitle = charts.Where(c => c.Title == null).ToList();

            // 里程口径的合理性：按难度看每音符的里程，顺手挑出极短的（不是错误，只是给个眼熟的边界）
            var shortest = charts
                .Where(c => c.Notes >= 300 && c.Meters < 80)
                .OrderBy(c => c.Meters).Take(25)
                .Select(c => new { file = c.File, notes = c.Notes, meters = c.Meters, kcal = c.Kcal })
                .ToList();
            var longest = charts
                .OrderByDescending(c => c.Meters).Take(10)
                .Select(c => new { file = c.File, notes = c.Notes, meters = c.Meters, kcal = c.Kcal })
                .ToList();

            return new
            {
                meta = meta,
                totals = new
                {
                    charts = charts.Count,
                    songs = charts.Select(c => c.MusicId).Distinct().Count(),
                    songsExcludingUtage = charts.Where(c => !c.IsUtage).Select(c => c.MusicId).Distinct().Count(),
                    utageCharts = charts.Count(c => c.IsUtage),
                    utageVariants = charts.Count(c => c.Variant != null),
                    kcalSum = Math.Round(charts.Sum(c => c.Kcal), 1),
                    metersSum = Math.Round(charts.Sum(c => c.Meters), 1),
                    kcalMin = kcals.Count > 0 ? kcals[0] : 0,
                    kcalP50 = Pct(kcals, 0.50),
                    kcalP90 = Pct(kcals, 0.90),
                    kcalP99 = Pct(kcals, 0.99),
                    kcalMax = kcals.Count > 0 ? kcals[kcals.Count - 1] : 0,
                    metersMin = meters.Count > 0 ? meters[0] : 0,
                    metersP50 = Pct(meters, 0.50),
                    metersMax = meters.Count > 0 ? meters[meters.Count - 1] : 0,
                    notesSum = charts.Sum(c => c.Notes),
                    touchNotesSum = charts.Sum(c => c.TouchNotes),
                },
                byDifficulty = byDiff,
                top = new { chartsByKcal = topCharts, songsByKcal = topSongs },
                validation = new
                {
                    explained = "三条独立的对账：① 从原始文本按记录名再数一遍，应当等于 Ma2 解析出的事件数；" +
                                "② 独立数出的按钮 / 触摸数应当等于谱面自带的 T_REC_* 表头；" +
                                "③ 等级文案（n / n+）应当与 Music.xml 的 musicLevelID 一致" +
                                "（卡面等级就是 MusicLevelIDEnum.GetLevelNum(musicLevelID) 出的）。",
                    independentCountMatchesMa2 = new
                    {
                        charts = raws.Count,
                        notesAndTouchBothMatch = walkBothOk,
                        notesMatch = walkNotesOk,
                        touchMatch = walkTouchOk,
                        mismatchSamples = walkBad,
                    },
                    independentCountMatchesChartHeader = new
                    {
                        chartsWithHeader = headerPresent,
                        buttonMatch = headerButtonOk,
                        touchMatch = headerTouchOk,
                        mismatchSamples = headerBad,
                    },
                    levelTextMatchesMusicLevelId = new
                    {
                        charts = charts.Count(c => c.Level.HasValue),
                        match = levelTextOk,
                        mismatch = levelTextBadCount,
                        mismatchSamples = levelBad,
                    },
                    recordTypes = new
                    {
                        distinct = inventory.Count,
                        categories = categoryTotals,
                        unknown = unknownTokens,
                        inventory = inventory,
                    },
                },
                quality = new
                {
                    chartsWithTitle = charts.Count - noTitle.Count,
                    chartsWithoutTitle = noTitle.Count,
                    chartsWithoutTitleNote = "曲名拿不到：music000000/000001 那两首目录里就没有 Music.xml，" +
                                             "11422 是 name/str 只有空格的占位曲目。谱面本身照常算。",
                    chartsWithoutTitleSamples = noTitle.Take(10)
                        .Select(c => new { file = c.File, musicId = c.MusicId, difficulty = c.DifficultyName, notes = c.Notes }).ToList(),
                    zeroNoteCharts = zeroNotes.Count,
                    zeroNoteChartsSamples = zeroNotes.Take(10).Select(c => c.File).ToList(),
                    zeroMovementCharts = zeroMovement.Count,
                    zeroMovementNote = "这些谱面的音符全落在同一个位置上（手真的不用动），里程 0 m 是对的，不是解析失败",
                    zeroMovementSamples = zeroMovement.Take(20)
                        .Select(c => new { file = c.File, title = c.Title, difficulty = c.DifficultyName, notes = c.Notes }).ToList(),
                    disabledCharts = disabled.Count,
                    disabledNote = "Music.xml 里 isEnable=false 的占位条目：游戏里不显示，但盘上有文件，这里照样算了一份",
                    disabledChartSamples = disabled.Take(10)
                        .Select(c => new { file = c.File, title = c.Title, difficulty = c.DifficultyName, notes = c.Notes, gameNotes = c.GameNotes }).ToList(),
                    shortestCharts = shortest,
                    longestCharts = longest,
                    duplicatesRemoved = duplicates.Count,
                    duplicates = duplicates,
                    skippedFiles = skipped,
                    musicXmlErrors = xmlErrors,
                },
                performance = new
                {
                    parseMs = parseMs,
                    notes = "解析 + 交叉校验 + 写盘的耗时；里程与 kcal 的口径见顶层 meta.model",
                },
            };
        }

        // ------------------------------------------------------------------ 小工具

        static bool HasUtage(List<ChartDto> charts)
        {
            for (int i = 0; i < charts.Count; i++) if (charts[i].IsUtage) return true;
            return false;
        }

        static string Key(int musicId, int diff, string variant)
        {
            return musicId.ToString(CultureInfo.InvariantCulture) + "/" +
                   diff.ToString(CultureInfo.InvariantCulture) + "/" + (variant ?? "-");
        }

        /// <summary>
        /// 游戏里显示的等级标签。卡面上那串「13」/「13+」是 `MusicLevelIDEnum.GetLevelNum(MusicLevelID)`
        /// 出的（见 MusicCardRender：`SetLevel(level, (MusicLevelID)LevelId, ...)`），所以这里按
        /// `musicLevelID` 反推，而不是自己拿 levelDecimal 猜：
        ///   1..6   → 等级 1..6（没有 + 档）
        ///   7 起   → 7 / 7+ / 8 / 8+ / … / 15 / 15+ 每档占一个编号
        ///          （7=7、8=7+、9=8、10=8+、17=12、18=12+、19=13、20=13+、23=15、24=15+）
        /// 反推：id+7 为奇数即带 +，等级 = (id+7-1)/2；也就是 n*2-7(+1) 的逆运算。
        /// 实测这套编号与本机 7449 张谱里 level/levelDecimal 的对应关系只有 16 张对不上
        /// （N100 里几首老曲 levelDecimal 没跟上，游戏显示的是 musicLevelID 那一档）。
        /// </summary>
        static string LevelText(int level, int dec, int levelId)
        {
            if (levelId >= 1 && levelId <= 24)
            {
                if (levelId <= 7) return levelId.ToString(CultureInfo.InvariantCulture);
                bool plus = ((levelId + 7) & 1) == 1;
                int lv = (levelId + 7 - (plus ? 1 : 0)) / 2;
                return lv.ToString(CultureInfo.InvariantCulture) + (plus ? "+" : "");
            }
            return level.ToString(CultureInfo.InvariantCulture) + (level >= 7 && dec >= 6 ? "+" : "");
        }

        static double Round3(double v) { return Math.Round(v, 3, MidpointRounding.AwayFromZero); }

        static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        static double Pct(List<double> sorted, double p)
        {
            if (sorted == null || sorted.Count == 0) return 0;
            if (sorted.Count == 1) return Round3(sorted[0]);
            double pos = (sorted.Count - 1) * p;
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            double v = lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
            return Round3(v);
        }

        /// <summary>只解析行首数字，遇到非数字即停（够读 T_REC_* 的值）。</summary>
        static int IntAt(string s, int a, int b)
        {
            int v = 0;
            bool any = false;
            for (int i = a; i < b && i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { v = v * 10 + (c - '0'); any = true; }
                else if (any || c == '\r') break;
                else if (c == ' ' || c == '+') continue;
                else break;
            }
            return any ? v : 0;
        }

        static string Next(string[] args, ref int i, string name)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(name + " 后面缺参数");
            return args[++i];
        }

        static IEnumerable<string> Enumerate(string root, string pattern)
        {
            try { return Directory.GetFiles(root, pattern, SearchOption.AllDirectories); }
            catch (Exception e)
            {
                Console.Error.WriteLine("枚举 " + pattern + " 失败: " + e.Message);
                return new string[0];
            }
        }

        /// <summary>允许传 游戏根 / Package / StreamingAssets 三种层级。</summary>
        static string ResolveStreamingAssets(string p)
        {
            string full;
            try { full = Path.GetFullPath(p); } catch { return null; }
            if (!Directory.Exists(full)) return null;

            if (string.Equals(Path.GetFileName(full.TrimEnd('\\', '/')), "StreamingAssets", StringComparison.OrdinalIgnoreCase))
                return full;

            var cands = new List<string>
            {
                Path.Combine(full, "StreamingAssets"),
                Path.Combine(full, "Sinmai_Data", "StreamingAssets"),
                Path.Combine(full, "Package", "Sinmai_Data", "StreamingAssets"),
            };
            foreach (string c in cands)
            {
                if (Directory.Exists(c) &&
                    (Directory.Exists(Path.Combine(c, "A000")) || Directory.Exists(Path.Combine(c, "A997"))))
                    return c;
            }
            return null;
        }

        static void ReadCfg(string path, out double alpha, out double weight, out bool hasAlpha, out bool hasWeight)
        {
            alpha = 1.0e-3;
            weight = 70.0;
            hasAlpha = hasWeight = false;
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    int hash = v.IndexOf('#');
                    if (hash >= 0) v = v.Substring(0, hash).Trim();
                    if (k == "alpha")
                    {
                        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out alpha)) hasAlpha = true;
                    }
                    else if (k == "weight")
                    {
                        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out weight)) hasWeight = true;
                    }
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("读 cfg 失败: " + path + " :: " + e.Message);
            }
        }

        static string Rel(string root, string path)
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        static long Length(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        static string Sha(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = File.OpenRead(path))
                {
                    byte[] h = sha.ComputeHash(fs);
                    var sb = new StringBuilder(h.Length * 2);
                    foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }

        static void Usage()
        {
            Console.WriteLine("KcalExport " + ToolVersion);
            Console.WriteLine("  --sa     <path>   StreamingAssets（也可以传游戏根 / Package 目录，自动往下找）");
            Console.WriteLine("  --out    <dir>    输出目录（写 charts.json + summary.json）");
            Console.WriteLine("  --cfg    <file>   maimaiKcalCalculator.cfg（读 alpha / weight，缺省用 1.0e-3 / 70）");
            Console.WriteLine("  --alpha  <num>    覆盖 alpha");
            Console.WriteLine("  --weight <num>    覆盖体重 kg（只影响 charts 模式；baseline 模式不写体重）");
            Console.WriteLine("  --server <name>   仅写进 meta（sdez / sdgb）");
            Console.WriteLine("  --mode   <name>   charts（默认，写 charts.json + summary.json）| baseline（写 kcal-baseline.json）");
        }
    }
}
