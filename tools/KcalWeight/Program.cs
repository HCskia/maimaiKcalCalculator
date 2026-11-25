// maimaiKcalCalculator 按体重出热量：读「基准」JSON（与体重无关的 kcalPerKg），乘上体重，
// 写出 dataout\kcal\<kg>kg.json。
//
// 基准来自 tools\KcalExport --mode baseline（export-baseline.ps1），里面的
//     kcalPerKg = alpha × 手部里程
// 与选曲画面同一套算法。这里只做一次乘法：
//     kcal = kcalPerKg × weightKg        （再四舍五入到 3 位小数，与 charts.json 同一精度）
//
// 为什么不把体重塞进导出工具：基准只跟「谱面 + alpha」有关，出一次就够；
// 换体重（试试 60 / 80 kg、帮别人算）只重跑这一层，1 秒出结果，不碰游戏包。
// 自检：weight = 70 时，每张谱的 kcal 都与 dataout\charts.json 逐张相同。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KcalWeight
{
    internal static class Program
    {
        const string ToolName = "maimaiKcalCalculator/export-kcal-weight";
        const string ToolVersion = "1.0.0";
        const string BaselineKind = "maimaiKcalCalculator/kcal-baseline";
        const string OutputKind = "maimaiKcalCalculator/kcal-by-weight";

        static readonly JsonWriterOptions WriterOpts = new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        sealed class Row
        {
            public JsonElement Chart;
            public double Kcal;
            public int MusicId;
            public string Variant;
            public string Title;
            public bool IsUtage;
        }

        sealed class Song
        {
            public int MusicId;
            public string Variant;
            public string Title;
            public bool IsUtage;
            public readonly List<Row> Charts = new List<Row>();
            public double Sum;
            public double Max;
        }

        // ------------------------------------------------------------------ 入口

        static int Main(string[] args)
        {
            string baselineArg = null, outArg = null, weightArg = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--baseline": baselineArg = Next(args, ref i, "--baseline"); break;
                    case "--weight": weightArg = Next(args, ref i, "--weight"); break;
                    case "--out": outArg = Next(args, ref i, "--out"); break;
                    case "-h": case "--help": Usage(); return 0;
                    default:
                        Console.Error.WriteLine("未知参数: " + args[i]);
                        Usage();
                        return 2;
                }
            }

            if (weightArg == null) { Console.Error.WriteLine("缺少 --weight <kg>"); return 2; }
            double weight;
            if (!double.TryParse(weightArg, NumberStyles.Float, CultureInfo.InvariantCulture, out weight) ||
                double.IsNaN(weight) || double.IsInfinity(weight) || weight <= 0)
            {
                Console.Error.WriteLine("--weight 不是正数: " + weightArg);
                return 2;
            }

            string baselinePath = string.IsNullOrEmpty(baselineArg)
                ? Path.Combine(Directory.GetCurrentDirectory(), "dataout", "kcal-baseline.json")
                : baselineArg;
            baselinePath = Path.GetFullPath(baselinePath);
            if (!File.Exists(baselinePath))
            {
                Console.Error.WriteLine("找不到基准文件: " + baselinePath);
                Console.Error.WriteLine("先跑 .\\export-baseline.ps1 生成 dataout\\kcal-baseline.json");
                return 2;
            }

            // 基准文件一律 UTF-8（我们自己写的无 BOM），用 ReadAllText 顺带兼容被人存成带 BOM 的情况
            JsonDocument doc;
            try
            {
                byte[] bytes = File.ReadAllBytes(baselinePath);
                doc = JsonDocument.Parse(DecodeUtf8(bytes));
            }
            catch (JsonException e)
            {
                Console.Error.WriteLine("基准文件不是合法 JSON: " + baselinePath + " :: " + e.Message);
                return 2;
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                JsonElement meta, chartsEl;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("meta", out meta) || meta.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("charts", out chartsEl) || chartsEl.ValueKind != JsonValueKind.Array)
                {
                    Console.Error.WriteLine("基准文件结构不对（要顶层 meta + charts 数组）: " + baselinePath);
                    return 2;
                }

                string kind = Str(meta, "kind");
                if (!string.Equals(kind, BaselineKind, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("这不是基准文件（meta.kind = " + (kind ?? "<缺失>") + "，要 " + BaselineKind + "）");
                    Console.Error.WriteLine("拿已经乘过体重的产物再乘一次会变成「kg²」——请重跑 .\\export-baseline.ps1。");
                    return 2;
                }

                // ---- 1. 逐张谱乘体重
                var rows = new List<Row>(chartsEl.GetArrayLength());
                int idx = 0;
                foreach (JsonElement ch in chartsEl.EnumerateArray())
                {
                    double perKg;
                    if (!TryNum(ch, "kcalPerKg", out perKg))
                    {
                        Console.Error.WriteLine("基准第 " + idx + " 条（musicId " + Num(ch, "musicId", 0) +
                                                "）没有 kcalPerKg，重跑 .\\export-baseline.ps1");
                        return 2;
                    }
                    rows.Add(new Row
                    {
                        Chart = ch,
                        Kcal = Round3(perKg * weight),
                        MusicId = (int)Num(ch, "musicId", 0),
                        Variant = Str(ch, "variant"),
                        Title = Str(ch, "title"),
                        IsUtage = Bool(ch, "isUtage"),
                    });
                    idx++;
                }

                var kcals = new List<double>(rows.Count);
                double sum = 0, max = 0, metersSum = 0;
                foreach (Row r in rows)
                {
                    kcals.Add(r.Kcal);
                    sum += r.Kcal;
                    if (r.Kcal > max) max = r.Kcal;
                    metersSum += Num(r.Chart, "meters", 0);
                }
                var sorted = new List<double>(kcals);
                sorted.Sort();

                // ---- 2. 曲目视图：同一首（含宴谱 L/R 变体）聚一行，列出各难度
                var songs = new List<Song>();
                var songIndex = new Dictionary<string, Song>(StringComparer.Ordinal);
                var songIds = new HashSet<int>();
                foreach (Row r in rows)
                {
                    songIds.Add(r.MusicId);
                    string key = r.MusicId.ToString(CultureInfo.InvariantCulture) + "|" + (r.Variant ?? "-");
                    Song s;
                    if (!songIndex.TryGetValue(key, out s))
                    {
                        s = new Song { MusicId = r.MusicId, Variant = r.Variant, Title = r.Title, IsUtage = r.IsUtage };
                        songIndex[key] = s;
                        songs.Add(s);
                    }
                    s.Charts.Add(r);
                    s.Sum += r.Kcal;
                    if (r.Kcal > s.Max) s.Max = r.Kcal;
                }

                // ---- 3. 输出路径：默认 <基准目录>\kcal\<kg>kg.json
                string outPath = string.IsNullOrEmpty(outArg) ? null : outArg;
                if (string.IsNullOrEmpty(outPath))
                {
                    string dir = Path.Combine(Path.GetDirectoryName(baselinePath) ?? ".", "kcal");
                    outPath = Path.Combine(dir, WeightTag(weight) + "kg.json");
                }
                outPath = Path.GetFullPath(outPath);
                if (string.Equals(outPath, baselinePath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("输出路径和基准文件是同一个（" + outPath + "），拒绝覆盖基准");
                    return 2;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));

                string baseAt = Str(meta, "generatedAt");

                // ---- 4. 写文件
                using (FileStream fs = File.Create(outPath))
                using (var w = new Utf8JsonWriter(fs, WriterOpts))
                {
                    w.WriteStartObject();

                    w.WriteStartObject("meta");
                    w.WriteString("kind", OutputKind);
                    w.WriteNumber("schemaVersion", 1);
                    w.WriteString("generatedAt", DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
                    w.WriteString("tool", ToolName + " v" + ToolVersion);
                    w.WriteNumber("weightKg", weight);

                    w.WriteStartObject("baseline");
                    w.WriteString("file", baselinePath);
                    if (baseAt != null) w.WriteString("generatedAt", baseAt);
                    JsonElement model0, src0, alpha0, srv0;
                    if (meta.TryGetProperty("model", out model0) && model0.ValueKind == JsonValueKind.Object &&
                        model0.TryGetProperty("alpha", out alpha0) && alpha0.ValueKind == JsonValueKind.Number)
                        w.WriteNumber("alpha", alpha0.GetDouble());
                    if (meta.TryGetProperty("source", out src0) && src0.ValueKind == JsonValueKind.Object &&
                        src0.TryGetProperty("server", out srv0) && srv0.ValueKind == JsonValueKind.String)
                        w.WriteString("server", srv0.GetString());
                    w.WriteNumber("charts", rows.Count);
                    w.WriteEndObject();

                    w.WriteStartObject("model");
                    w.WriteString("formula", "kcal = kcalPerKg × weightKg");
                    w.WriteString("rounding", "每张谱先乘体重、再四舍五入到 3 位小数（与 charts.json 的 kcal 同一精度）");
                    w.WriteString("kcalPerKg", "该谱每公斤体重的热量，来自基准文件（= alpha × 手部里程）；想知道 kcal 怎么来的就看这个字段");
                    w.WriteEndObject();

                    w.WriteStartObject("totals");
                    w.WriteNumber("charts", rows.Count);
                    w.WriteNumber("songs", songIds.Count);
                    w.WriteNumber("songEntries", songs.Count);
                    w.WriteNumber("kcalSum", Round3(sum));
                    w.WriteNumber("kcalMedian", Pct(sorted, 0.50));
                    w.WriteNumber("kcalMax", Round3(max));
                    w.WriteNumber("metersSum", Round3(metersSum));
                    w.WriteEndObject();

                    w.WriteStartObject("fields");
                    w.WriteString("kcal", "这张谱在这个体重下的预计消耗 = kcalPerKg × weightKg");
                    w.WriteString("kcalPerKg", "每公斤体重的热量（kcal/kg），来自基准；换体重时它不变");
                    w.WriteString("meters", "手部里程（米），与 mod 选曲画面「预计」同一份算法");
                    w.WriteString("notes", "解析到的音符事件数（一条滑条算 1 次，起手不重复算）");
                    w.WriteString("songs", "songs[] 是曲目视图：一条对应一个 musicId（宴谱双人谱拆成 L/R 各一条），" +
                                           "列出该曲各难度在这个体重下的 kcal；同一首的条数看 totals.songEntries");
                    w.WriteEndObject();

                    string caveat = Str(meta, "caveat");
                    w.WriteString("caveat", caveat ?? "kcal 的绝对误差约 ±30–50%，全部来自 alpha（未标定）；里程那一半是可信的。");

                    w.WriteEndObject();

                    // charts：原样搬运基准的字段，末尾追加 kcal（顺序：… meters, kcalPerKg, file, kcal）
                    w.WriteStartArray("charts");
                    foreach (Row r in rows)
                    {
                        w.WriteStartObject();
                        foreach (JsonProperty p in r.Chart.EnumerateObject()) p.WriteTo(w);
                        w.WriteNumber("kcal", r.Kcal);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();

                    w.WriteStartArray("songs");
                    foreach (Song s in songs)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("musicId", s.MusicId);
                        if (s.Variant != null) w.WriteString("variant", s.Variant);
                        if (s.Title != null) w.WriteString("title", s.Title);
                        w.WriteBoolean("isUtage", s.IsUtage);
                        w.WriteNumber("charts", s.Charts.Count);
                        w.WriteStartArray("difficulties");
                        foreach (Row r in s.Charts)
                        {
                            w.WriteStartObject();
                            w.WriteNumber("difficulty", (int)Num(r.Chart, "difficulty", 0));
                            string dn = Str(r.Chart, "difficultyName");
                            if (dn != null) w.WriteString("difficultyName", dn);
                            string lv = Str(r.Chart, "levelText");
                            if (lv != null) w.WriteString("levelText", lv);
                            w.WriteNumber("meters", Num(r.Chart, "meters", 0));
                            w.WriteNumber("kcal", r.Kcal);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteNumber("kcalMax", Round3(s.Max));
                        w.WriteNumber("kcalSum", Round3(s.Sum));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();

                    w.WriteEndObject();
                }

                // ---- 5. 控制台小结
                long bytes = 0;
                try { bytes = new FileInfo(outPath).Length; } catch { }

                Console.WriteLine("[KcalWeight] 基准: " + baselinePath);
                Console.WriteLine("[KcalWeight] 基准 " + rows.Count.ToString("N0") + " 张谱 / " + songIds.Count.ToString("N0") +
                                  " 首（songs[] " + songs.Count.ToString("N0") + " 条，宴谱 L/R 各一条）" +
                                  (baseAt != null ? "，生成于 " + baseAt : ""));
                Console.WriteLine("[KcalWeight] 体重 " + WeightTag(weight) + " kg → 单谱中位数 " + F(Pct(sorted, 0.50)) +
                                  " kcal，最大 " + F(Round3(max)) + " kcal，全部谱面合计 " + F(Round3(sum)) + " kcal");
                Console.WriteLine("[KcalWeight] 写出: " + outPath + "  （" + bytes.ToString("N0") + " bytes）");
                return 0;
            }
        }

        // ------------------------------------------------------------------ 小工具

        static string DecodeUtf8(byte[] bytes)
        {
            int offset = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) offset = 3;
            return new System.Text.UTF8Encoding(false).GetString(bytes, offset, bytes.Length - offset);
        }

        static string Str(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        static bool Bool(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out v)) return false;
            return v.ValueKind == JsonValueKind.True;
        }

        static double Num(JsonElement e, string name, double fallback)
        {
            double v;
            return TryNum(e, name, out v) ? v : fallback;
        }

        static bool TryNum(JsonElement e, string name, out double v)
        {
            v = 0;
            JsonElement x;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out x)) return false;
            if (x.ValueKind == JsonValueKind.Number) { v = x.GetDouble(); return true; }
            if (x.ValueKind == JsonValueKind.String) return double.TryParse(x.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return false;
        }

        static double Round3(double v) { return Math.Round(v, 3, MidpointRounding.AwayFromZero); }

        static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        static string WeightTag(double w) { return w.ToString("0.###", CultureInfo.InvariantCulture); }

        static double Pct(List<double> sorted, double p)
        {
            if (sorted == null || sorted.Count == 0) return 0;
            if (sorted.Count == 1) return Round3(sorted[0]);
            double pos = (sorted.Count - 1) * p;
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            double v = lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
            return Round3(v);
        }

        static string Next(string[] args, ref int i, string name)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(name + " 后面缺参数");
            return args[++i];
        }

        static void Usage()
        {
            Console.WriteLine("KcalWeight " + ToolVersion + " —— 把「基准」乘成某个体重下的热量表");
            Console.WriteLine("  --weight   <kg>   体重（必填）");
            Console.WriteLine("  --baseline <file> 基准文件，默认 .\\dataout\\kcal-baseline.json");
            Console.WriteLine("  --out      <file> 输出文件，默认 <基准目录>\\kcal\\<kg>kg.json");
        }
    }
}
