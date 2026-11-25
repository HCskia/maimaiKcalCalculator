// 每日游戏记录：每首曲子一条，按「轮」结束时追加到 <recorddir>\maimai-YYYY-MM-DD.json。
//
// 记录内容（App 侧「舞萌热量」直接按这个格式导入，不依赖 App 的曲库）：
//   曲名 / 难度 / 等级 / 达成率 / 游玩时间 / 本次实际手部里程 / 谱面预计手部里程 /
//   玩家位（1P/2P）/ 服务器（cn=国服，jp=日服）
//
// 「实际手部里程」来自 Tracker 的真实输入采样，「预计里程」来自 ChartStore 的 .ma2 解析，
// 两者都不换算成 kcal —— App 自己按体重和 alpha 算。
//
// 写盘时机：用户选择「整轮结束才写」——每曲结束时只把记录收进内存，
// 由 Process.DataSaveProcess.OnStart（续关/结算后的存盘流程）一次性追加；
// 另外在换轮、退出游戏时兜底 flush，避免挂钩漏掉时丢数据。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace MaimaiKcal
{
    public static class RecordCollector
    {
        static RecordCollector() { DailyRecordFile.Warn = Diag.Warn; }

        static readonly object Gate = new object();
        static readonly List<PlayRecord> Pending = new List<PlayRecord>();
        static readonly HashSet<string> PendingKeys = new HashSet<string>();

        /// <summary>当前进程属于哪个服务器：cn / jp / unknown。</summary>
        public static string ServerCode
        {
            get
            {
                string forced = (Config.Current.RecordServer ?? "").Trim().ToLowerInvariant();
                if (forced == "cn" || forced == "sdgb" || forced == "国服") return "cn";
                if (forced == "jp" || forced == "sdez" || forced == "日服") return "jp";

                string dir = (Paths.PackageDir ?? "").ToUpperInvariant();
                if (dir.Contains("SDGB")) return "cn";
                if (dir.Contains("SDEZ")) return "jp";
                return "unknown";
            }
        }

        public static string ServerName
        {
            get
            {
                switch (ServerCode)
                {
                    case "cn": return "国服";
                    case "jp": return "日服";
                    default: return "未知";
                }
            }
        }

        /// <summary>把一首曲子的成绩收进本轮缓冲；真正的写盘在 Flush。</summary>
        public static void CaptureTrack(DateTime startedAt, int trackCount)
        {
            if (!Config.Current.Enabled || !Config.Current.RecordEnabled) return;
            if (startedAt == default(DateTime)) startedAt = DateTime.Now;

            int players = Tracker.SampleMask;
            for (int p = 0; p < 2; p++)
            {
                if ((players & (1 << p)) == 0) continue;
                double actual = Tracker.PlayerTrack[p];
                if (actual <= 0.05) continue;      // 完全没动 / 不在场，不写空记录

                PlayRecord rec;
                try { rec = Build(p, startedAt, actual); }
                catch (Exception e)
                {
                    Diag.ErrorOnce("record-build" + p, e);
                    continue;
                }
                if (rec == null) continue;

                lock (Gate)
                {
                    if (PendingKeys.Add(rec.Key)) Pending.Add(rec);
                }
                Diag.Msg(string.Format("记录入队：{0} [{1}] {2} {3} 实际 {4:0.0} m / 预计 {5:0.0} m（{6}，第 {7} 曲）",
                    rec.Title, rec.ServerName, rec.DifficultyName, rec.LevelText,
                    rec.ActualMeters, rec.PredictedMeters, rec.Player == 1 ? "1P" : "2P", trackCount));
            }
        }

        static PlayRecord Build(int playerIndex, DateTime startedAt, double actualMeters)
        {
            Manager.GamePlayManager gpm = Manager.GamePlayManager.Instance;
            if (gpm == null) return null;

            // trackNo = -1 → 游戏内部按 MusicTrackNumber-1 取「刚打完的这首」
            Manager.GameScoreList score = gpm.GetGameScore(playerIndex, -1);
            if (score == null || !score.IsEnable) return null;

            Game.SessionInfo info = score.SessionInfo;
            int musicId = info.musicId;
            int diff = info.difficulty;
            if (musicId <= 0 || diff < 0) return null;

            ChartStore.Entry chart;
            bool hasChart = ChartStore.TryGet(musicId, diff, out chart);

            Manager.MaiStudio.MusicData music = null;
            try
            {
                Manager.DataManager dm = Manager.DataManager.Instance;
                if (dm != null) music = dm.GetMusic(musicId);
            }
            catch (Exception e) { Diag.ErrorOnce("record-music", e); }

            string title = music != null && music.name != null && !string.IsNullOrEmpty(music.name.str)
                ? music.name.str
                : ("#" + musicId);

            Manager.MaiStudio.Notes note = null;
            try
            {
                if (music != null && music.notesData != null && diff >= 0 && diff < music.notesData.Count)
                    note = music.notesData[diff];
            }
            catch (Exception e) { Diag.ErrorOnce("record-notes", e); }

            double levelValue = 0;
            string levelText = "";
            if (note != null)
            {
                levelValue = note.level + note.levelDecimal / 10.0;
                try
                {
                    levelText = Manager.MusicLevelIDEnum.GetLevelNum((Manager.MusicLevelID)note.musicLevelID);
                }
                catch { }
                if (string.IsNullOrEmpty(levelText))
                {
                    levelText = note.level.ToString(CultureInfo.InvariantCulture)
                                + (note.level < 7 ? "" : (note.levelDecimal >= 6 ? "+" : ""));
                }
            }

            double achievement = 0;
            try { achievement = (double)score.GetAchivement(); }
            catch (Exception e) { Diag.ErrorOnce("record-ach", e); }

            return new PlayRecord
            {
                Date = startedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Time = startedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                PlayedAt = startedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                MusicId = musicId,
                Title = title,
                Difficulty = diff,
                DifficultyName = DifficultyName(diff),
                LevelText = levelText,
                LevelValue = levelValue,
                Achievement = achievement,
                ActualMeters = actualMeters,
                PredictedMeters = hasChart ? chart.Meters : 0.0,
                Player = playerIndex + 1,
                Server = ServerCode,
                ServerName = ServerName,
            };
        }

        static string DifficultyName(int diff)
        {
            switch (diff)
            {
                case 0: return "BASIC";
                case 1: return "ADVANCED";
                case 2: return "EXPERT";
                case 3: return "MASTER";
                case 4: return "Re:MASTER";
               	default: return diff == 10 ? "UTAGE" : ("DIFF" + diff);
            }
        }

        /// <summary>把缓冲区里的记录追加到各自日期的文件。可重复调用（按 key 去重）。</summary>
        public static void Flush(string reason)
        {
            if (!Config.Current.Enabled || !Config.Current.RecordEnabled) return;

            List<PlayRecord> batch;
            lock (Gate)
            {
                if (Pending.Count == 0) return;
                batch = new List<PlayRecord>(Pending);
                Pending.Clear();
                PendingKeys.Clear();
            }

            try
            {
                string dir = ResolveRecordDir();
                if (string.IsNullOrEmpty(dir))
                {
                    Requeue(batch);
                    Diag.Warn("记录目录无效，本轮 " + batch.Count + " 条没有落盘（下次再试）");
                    return;
                }
                Directory.CreateDirectory(dir);

                // 按日期分组（跨零点的轮次也能分别落到两天的文件）
                var byDate = new Dictionary<string, List<PlayRecord>>();
                foreach (PlayRecord r in batch)
                {
                    List<PlayRecord> list;
                    if (!byDate.TryGetValue(r.Date, out list))
                    {
                        list = new List<PlayRecord>();
                        byDate[r.Date] = list;
                    }
                    list.Add(r);
                }

                int written = 0;
                foreach (var kv in byDate)
                {
                    string path = Path.Combine(dir, "maimai-" + kv.Key + ".json");
                    written += DailyRecordFile.Append(path, kv.Key, kv.Value);
                    Diag.Msg(string.Format("已写入 {0} 条：{1}（{2}）", kv.Value.Count, path, reason));
                }
                Diag.Msg("本轮记录落盘完成：" + written + " 条（" + reason + "）");
            }
            catch (Exception e)
            {
                Requeue(batch);
                Diag.Error("写每日游戏记录失败（" + reason + "），" + batch.Count + " 条留在内存等下次", e);
            }
        }

        static void Requeue(List<PlayRecord> batch)
        {
            lock (Gate)
            {
                foreach (PlayRecord r in batch)
                {
                    if (PendingKeys.Add(r.Key)) Pending.Add(r);
                }
            }
        }

        public static string ResolveRecordDir()
        {
            string configured = Config.Current.RecordDir;
            try
            {
                if (string.IsNullOrEmpty(configured))
                    return Path.Combine(Paths.CacheDir, "records");

                configured = configured.Trim();
                if (configured.Length >= 2
                    && ((configured[0] == '"' && configured[configured.Length - 1] == '"')
                        || (configured[0] == '\'' && configured[configured.Length - 1] == '\'')))
                    configured = configured.Substring(1, configured.Length - 2);

                if (configured == "~" || configured.StartsWith("~\\") || configured.StartsWith("~/"))
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    configured = home + configured.Substring(1);
                }

                return Path.IsPathRooted(configured)
                    ? Path.GetFullPath(configured)
                    : Path.GetFullPath(Path.Combine(Paths.PackageDir, configured));
            }
            catch (Exception e)
            {
                Diag.Warn("记录目录解析失败：" + e.Message);
                return "";
            }
        }
    }
}
