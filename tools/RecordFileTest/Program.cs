// 每日记录 JSON 的宿主侧测试：直接链接 src\DailyRecord.cs，不复制逻辑。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MaimaiKcal;

public static class Program
{
    static int _failed;

    public static int Main()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "record-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "maimai-2026-11-25.json");
            var batch1 = new List<PlayRecord>
            {
                Rec(1, "群青", 3, 99.8731, 250.0, 300.0, "18:03"),
                Rec(2, "FLΛME/FRΦST", 4, 100.5, 333.3, 340.0, "18:07"),
            };
            Check("first append returns 2", DailyRecordFile.Append(path, "2026-11-25", batch1) == 2);
            Check("file exists", File.Exists(path));

            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var root = doc.RootElement;
                Check("app", root.GetProperty("app").GetString() == "maimaiKcalCalculator");
                Check("schema", root.GetProperty("schema").GetInt32() == 1);
                Check("date", root.GetProperty("date").GetString() == "2026-11-25");
                var records = root.GetProperty("records");
                Check("records count = 2", records.GetArrayLength() == 2);
                var r0 = records[0];
                Check("musicId", r0.GetProperty("musicId").GetInt32() == 1);
                Check("title", r0.GetProperty("title").GetString() == "群青");
                Check("achievement", Math.Abs(r0.GetProperty("achievement").GetDouble() - 99.8731) < 1e-9);
                Check("actual", Math.Abs(r0.GetProperty("actualMeters").GetDouble() - 250.0) < 1e-9);
                Check("predicted", Math.Abs(r0.GetProperty("predictedMeters").GetDouble() - 300.0) < 1e-9);
                Check("server", r0.GetProperty("server").GetString() == "cn");
            }

            // 同一批再写一次：按 key 去重，不重复追加
            Check("duplicate append returns 0", DailyRecordFile.Append(path, "2026-11-25", batch1) == 0);

            // 新一批追加：数组结构 / 逗号正确
            var batch2 = new List<PlayRecord>
            {
                Rec(3, "title with \"quote\", \\ slash\nand newline", 2, 97.0, 210.0, 200.0, "18:11"),
            };
            Check("second append returns 1", DailyRecordFile.Append(path, "2026-11-25", batch2) == 1);
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var records = doc.RootElement.GetProperty("records");
                Check("records count = 3", records.GetArrayLength() == 3);
                Check("special title round-trip",
                    records[2].GetProperty("title").GetString() == "title with \"quote\", \\ slash\nand newline");
            }

            // 目录里没有文件时也能直接创建
            string path2 = Path.Combine(dir, "maimai-2026-11-26.json");
            Check("create new file", DailyRecordFile.Append(path2, "2026-11-26", batch2) == 1);
            using (var doc = JsonDocument.Parse(File.ReadAllText(path2)))
            {
                Check("new file count", doc.RootElement.GetProperty("records").GetArrayLength() == 1);
            }

            // 坏文件：先备份成 .bad-*，再重建（不静默覆盖）
            string path3 = Path.Combine(dir, "maimai-2026-11-27.json");
            File.WriteAllText(path3, "{ this is not json");
            Check("corrupt file append", DailyRecordFile.Append(path3, "2026-11-27", batch2) == 1);
            Check("corrupt backup exists", Directory.GetFiles(dir, "maimai-2026-11-27.json.bad-*").Length == 1);
            using (var doc = JsonDocument.Parse(File.ReadAllText(path3)))
            {
                Check("corrupt rebuilt count", doc.RootElement.GetProperty("records").GetArrayLength() == 1);
            }

            // 空 records 数组：插入时不能多一个逗号
            string path4 = Path.Combine(dir, "maimai-2026-11-28.json");
            File.WriteAllText(path4, "{\n  \"app\": \"maimaiKcalCalculator\",\n  \"schema\": 1,\n  \"date\": \"2026-11-28\",\n  \"records\": []\n}\n");
            Check("empty array append", DailyRecordFile.Append(path4, "2026-11-28", batch2) == 1);
            using (var doc = JsonDocument.Parse(File.ReadAllText(path4)))
            {
                Check("empty array count", doc.RootElement.GetProperty("records").GetArrayLength() == 1);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine(_failed == 0 ? "ALL PASS" : ("FAILED: " + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static PlayRecord Rec(int id, string title, int diff, double ach, double actual, double predicted, string time)
    {
        return new PlayRecord
        {
            Date = "2026-11-25",
            Time = time,
            PlayedAt = "2026-11-25T" + time + ":00+08:00",
            MusicId = id,
            Title = title,
            Difficulty = diff,
            DifficultyName = diff == 3 ? "MASTER" : "Re:MASTER",
            LevelText = "14+",
            LevelValue = 14.2,
            Achievement = ach,
            ActualMeters = actual,
            PredictedMeters = predicted,
            Player = 1,
            Server = "cn",
            ServerName = "国服",
        };
    }

    static void Check(string name, bool ok)
    {
        if (!ok)
        {
            _failed++;
            Console.WriteLine("FAIL  " + name);
        }
    }
}
