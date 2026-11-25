// 每日记录文件的 JSON 结构 + 追加写。
//
// 这个文件刻意不依赖游戏程序集，方便用 tools\RecordFileTest 做宿主侧测试。
// 依赖游戏的收集逻辑在 Records.cs（RecordCollector）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace MaimaiKcal
{
    public class PlayRecord
    {
        public string Date = "";
        public string Time = "";
        public string PlayedAt = "";
        public int MusicId;
        public string Title = "";
        public int Difficulty;
        public string DifficultyName = "";
        public string LevelText = "";
        public double LevelValue;
        public double Achievement;
        public double ActualMeters;
        public double PredictedMeters;
        public int Player;
        public string Server = "unknown";
        public string ServerName = "未知";

        public string Key
        {
            get
            {
                return Date + "T" + Time + "|" + MusicId + "|" + Difficulty + "|" + Player;
            }
        }
    }

    /// <summary>
    /// 单个日期文件的追加写。文件是一个 JSON 对象：
    /// { "app": "...", "schema": 1, "date": "YYYY-MM-DD", "records": [ ... ] }
    /// 追加时只在 records 数组末尾插入，不解析已有记录；用 record.key 防重复。
    /// </summary>
    public static class DailyRecordFile
    {
        /// <summary>警告输出（游戏侧接到 Diag；宿主侧测试可替换）。</summary>
        public static Action<string> Warn;

        static void Log(string s)
        {
            Action<string> w = Warn;
            if (w != null) w(s);
        }

        static readonly Mutex FileMutex = CreateMutex();

        static Mutex CreateMutex()
        {
            try { return new Mutex(false, "MaimaiKcalDailyRecordFile"); }
            catch { return null; }
        }

        public static int Append(string path, string date, List<PlayRecord> records)
        {
            if (records == null || records.Count == 0) return 0;

            bool locked = false;
            try
            {
                if (FileMutex != null)
                {
                    try { locked = FileMutex.WaitOne(5000); }
                    catch (AbandonedMutexException) { locked = true; }
                    catch { locked = false; }
                }

                string existing = null;
                if (File.Exists(path))
                {
                    try { existing = File.ReadAllText(path, Encoding.UTF8); }
                    catch (Exception e) { Log("读已有记录失败：" + e.Message); }
                }

                string text;
                int added;
                if (string.IsNullOrEmpty(existing))
                {
                    text = BuildNew(date, records);
                    added = records.Count;
                }
                else if (!TryInsert(existing, records, out text, out added))
                {
                    // 文件被手改坏了：先留一份 .bad 备份，再新建（绝不静默覆盖）
                    string bad = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    try { File.Move(path, bad); } catch { }
                    Log("记录文件格式不对，已备份为 " + bad + "，重新创建");
                    text = BuildNew(date, records);
                    added = records.Count;
                }

                if (added <= 0) return 0;

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null); }
                    catch
                    {
                        File.Copy(tmp, path, true);
                        try { File.Delete(tmp); } catch { }
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }
                return added;
            }
            finally
            {
                if (locked)
                {
                    try { FileMutex.ReleaseMutex(); } catch { }
                }
            }
        }

        static string BuildNew(string date, List<PlayRecord> records)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"app\": \"maimaiKcalCalculator\",\n");
            sb.Append("  \"schema\": 1,\n");
            sb.Append("  \"date\": \"").Append(Json(date)).Append("\",\n");
            sb.Append("  \"records\": [\n");
            for (int i = 0; i < records.Count; i++)
            {
                if (i > 0) sb.Append(",\n");
                sb.Append(RecordJson(records[i], "    "));
            }
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        static bool TryInsert(string existing, List<PlayRecord> records, out string text, out int added)
        {
            text = null;
            added = 0;

            int keyPos = existing.IndexOf("\"records\"", StringComparison.Ordinal);
            if (keyPos < 0) return false;
            int open = existing.IndexOf('[', keyPos);
            if (open < 0) return false;
            int close = existing.LastIndexOf(']');
            if (close <= open) return false;

            string inside = existing.Substring(open + 1, close - open - 1);
            bool empty = inside.Trim().Length == 0;

            var sb = new StringBuilder();
            sb.Append(inside.TrimEnd());
            int n = 0;
            foreach (PlayRecord r in records)
            {
                string keyNeedle = "\"key\": \"" + Json(r.Key) + "\"";
                if (existing.IndexOf(keyNeedle, StringComparison.Ordinal) >= 0) continue;

                if (!empty || n > 0) sb.Append(',');
                sb.Append('\n').Append(RecordJson(r, "    "));
                n++;
            }
            if (n == 0) return true;

            sb.Append("\n  ");
            text = existing.Substring(0, open + 1) + sb.ToString() + existing.Substring(close);
            added = n;
            return true;
        }

        static string RecordJson(PlayRecord r, string indent)
        {
            var sb = new StringBuilder(512);
            sb.Append(indent).Append("{\n");
            sb.Append(indent).Append("  \"key\": \"").Append(Json(r.Key)).Append("\",\n");
            sb.Append(indent).Append("  \"playedAt\": \"").Append(Json(r.PlayedAt)).Append("\",\n");
            sb.Append(indent).Append("  \"date\": \"").Append(Json(r.Date)).Append("\",\n");
            sb.Append(indent).Append("  \"time\": \"").Append(Json(r.Time)).Append("\",\n");
            sb.Append(indent).Append("  \"musicId\": ").Append(r.MusicId).Append(",\n");
            sb.Append(indent).Append("  \"title\": \"").Append(Json(r.Title)).Append("\",\n");
            sb.Append(indent).Append("  \"difficulty\": ").Append(r.Difficulty).Append(",\n");
            sb.Append(indent).Append("  \"difficultyName\": \"").Append(Json(r.DifficultyName)).Append("\",\n");
            sb.Append(indent).Append("  \"levelText\": \"").Append(Json(r.LevelText)).Append("\",\n");
            sb.Append(indent).Append("  \"levelValue\": ").Append(r.LevelValue.ToString("0.0", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append(indent).Append("  \"achievement\": ").Append(r.Achievement.ToString("0.0000", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append(indent).Append("  \"actualMeters\": ").Append(r.ActualMeters.ToString("0.0", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append(indent).Append("  \"predictedMeters\": ").Append(r.PredictedMeters.ToString("0.0", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append(indent).Append("  \"player\": ").Append(r.Player).Append(",\n");
            sb.Append(indent).Append("  \"server\": \"").Append(Json(r.Server)).Append("\",\n");
            sb.Append(indent).Append("  \"serverName\": \"").Append(Json(r.ServerName)).Append("\"\n");
            sb.Append(indent).Append("}");
            return sb.ToString();
        }

        static string Json(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
