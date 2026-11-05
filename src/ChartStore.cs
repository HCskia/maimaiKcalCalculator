// 全谱面手部里程的离线计算与缓存。
//
// 启动时在后台线程跑一次：优先读 Mods\maimaiKcalCalculator\charts.cache；
// 缓存失效（谱面数量/总字节数变了，例如游戏更新）才重新解析全部 .ma2。
// 解析在后台线程进行，不占主线程，选曲画面在准备好之前不显示。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace MaimaiKcal
{
    public static class ChartStore
    {
        public struct Entry
        {
            public double Meters;
            public int Notes;
            public int Touches;
        }

        const string CacheVersion = "v3";

        static Dictionary<long, Entry> _map = new Dictionary<long, Entry>();

        public static volatile bool Ready;

        public static int Count { get { return _map.Count; } }

        public static void Init()
        {
            var t = new Thread(Run);
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "MaimaiKcal-Charts";
            t.Start();
        }

        public static bool TryGet(int musicId, int difficulty, out Entry e)
        {
            e = default(Entry);
            if (musicId <= 0 || difficulty < 0 || difficulty > 7) return false;
            return _map.TryGetValue((long)musicId * 16L + difficulty, out e);
        }

        static void Run()
        {
            try
            {
                string sa = Paths.StreamingAssets;
                if (string.IsNullOrEmpty(sa) || !Directory.Exists(sa))
                {
                    Diag.Warn("找不到 StreamingAssets: " + sa);
                    Ready = true;
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string[] files = Directory.GetFiles(sa, "*.ma2", SearchOption.AllDirectories);
                long bytes = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    try { bytes += new FileInfo(files[i]).Length; } catch { }
                }
                string header = "# maimaiKcal " + CacheVersion + " files=" + files.Length + " bytes=" + bytes;

                if (TryLoadCache(header))
                {
                    Diag.Msg(string.Format("谱面缓存命中：{0} 张，用时 {1} ms", _map.Count, sw.ElapsedMilliseconds));
                    Ready = true;
                    return;
                }

                Diag.Msg(string.Format("开始解析 {0} 张谱面（{1:N0} 字节）…", files.Length, bytes));

                int nt = Environment.ProcessorCount;
                if (nt < 1) nt = 1;
                if (nt > 8) nt = 8;

                var maps = new Dictionary<long, Entry>[nt];
                var threads = new Thread[nt];
                for (int t = 0; t < nt; t++)
                {
                    int slot = t;
                    threads[t] = new Thread(delegate()
                    {
                        var local = new Dictionary<long, Entry>(files.Length / nt + 16);
                        for (int i = slot; i < files.Length; i += nt)
                        {
                            long key;
                            if (!TryKeyOf(files[i], out key)) continue;
                            Ma2.Stat st = Ma2.Parse(files[i]);
                            if (st.Notes == 0) continue;
                            local[key] = new Entry { Meters = st.Meters, Notes = st.Notes, Touches = st.Touches };
                        }
                        maps[slot] = local;
                    });
                    threads[t].IsBackground = true;
                    threads[t].Priority = ThreadPriority.BelowNormal;
                    threads[t].Name = "MaimaiKcal-Parse" + t;
                    threads[t].Start();
                }
                for (int t = 0; t < nt; t++) threads[t].Join();

                var map = new Dictionary<long, Entry>(files.Length);
                for (int t = 0; t < nt; t++)
                {
                    if (maps[t] == null) continue;
                    foreach (var kv in maps[t]) map[kv.Key] = kv.Value;
                }

                _map = map;
                Diag.Msg(string.Format("谱面解析完成：{0} 张，用时 {1} ms", map.Count, sw.ElapsedMilliseconds));

                WriteCache(header);
                Ready = true;
            }
            catch (Exception e)
            {
                Diag.Error("谱面预计算失败", e);
                Ready = true;
            }
        }

        /// <summary>文件名形如 000008_03.ma2 → (musicId=8, diff=3)。</summary>
        static bool TryKeyOf(string path, out long key)
        {
            key = 0;
            string name = Path.GetFileNameWithoutExtension(path);
            int us = name.IndexOf('_');
            if (us <= 0 || us + 1 >= name.Length) return false;
            int id, diff;
            if (!int.TryParse(name.Substring(0, us), out id)) return false;
            if (!int.TryParse(name.Substring(us + 1), out diff)) return false;
            if (id <= 0 || diff < 0 || diff > 7) return false;
            key = (long)id * 16L + diff;
            return true;
        }

        static bool TryLoadCache(string header)
        {
            try
            {
                if (!File.Exists(Paths.CacheFile)) return false;
                using (var sr = new StreamReader(Paths.CacheFile, Encoding.UTF8))
                {
                    string first = sr.ReadLine();
                    if (first == null || first.Trim() != header) return false;

                    var map = new Dictionary<long, Entry>(6000);
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        string[] f = line.Split('\t');
                        if (f.Length < 4) continue;
                        long key;
                        double m;
                        int notes, touches;
                        if (!long.TryParse(f[0], out key)) continue;
                        if (!double.TryParse(f[1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out m)) continue;
                        if (!int.TryParse(f[2], out notes)) continue;
                        if (!int.TryParse(f[3], out touches)) continue;
                        map[key] = new Entry { Meters = m, Notes = notes, Touches = touches };
                    }

                    if (map.Count == 0) return false;
                    _map = map;
                    return true;
                }
            }
            catch (Exception e)
            {
                Diag.Warn("读缓存失败，重算：" + e.Message);
                return false;
            }
        }

        static void WriteCache(string header)
        {
            try
            {
                Directory.CreateDirectory(Paths.CacheDir);
                var sb = new StringBuilder(1024 * 1024);
                sb.Append(header).Append('\n');
                foreach (var kv in _map)
                {
                    Entry e = kv.Value;
                    sb.Append(kv.Key).Append('\t')
                      .Append(e.Meters.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                      .Append(e.Notes).Append('\t')
                      .Append(e.Touches).Append('\n');
                }
                File.WriteAllText(Paths.CacheFile, sb.ToString(), Encoding.UTF8);
                Diag.Msg("已写入缓存: " + Paths.CacheFile);
            }
            catch (Exception e) { Diag.Warn("写缓存失败: " + e.Message); }
        }
    }
}
