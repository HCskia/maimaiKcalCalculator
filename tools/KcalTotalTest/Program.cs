// 用桩驱动真实的 Tracker.cs / Strings.cs，验证「总计」的累计语义：
//   1) 开局为 0
//   2) 打的过程中实时计入本曲
//   3) 一曲结束不重复入账（EndTrack 幂等）
//   4) 换轮只清「本轮」，不清「总计」
//   5) 上一曲没收到 OnRelease 直接被 BeginTrack 收尾时，只入账一次
//   6) HUD 文案确实有三行、第三行是总计
//
// 跑法：dotnet run --project tools\KcalTotalTest -c Release
// 只跑宿主侧，不需要游戏；Unity / Manager / Melon 的接口都在 Stubs.cs 里。
using System;
using MaimaiKcal;

static class Program
{
    static int _fail;
    static double _lastTotal;
    static int _mono;
    static int _step;

    static void Step(string name)
    {
        _step++;
        Console.WriteLine("-- " + _step + ". " + name);
    }

    static void Check(string what, double actual, double expect, double tol = 1e-9)
    {
        bool ok = Math.Abs(actual - expect) <= tol;
        if (!ok) _fail++;
        Console.WriteLine(string.Format("   [{0}] {1}: actual={2} expect={3}",
                                        ok ? "PASS" : "FAIL", what, actual, expect));
    }

    static void TotalIs(string what, double expect)
    {
        double t = Tracker.TotalMeters;
        Check("TotalMeters " + what, t, expect);
        if (t < _lastTotal - 1e-9)
        {
            _fail++;
            Console.WriteLine(string.Format("   [FAIL] total went backwards: {0} -> {1}", _lastTotal, t));
        }
        _lastTotal = t;
        _mono++;
    }

    static void Main()
    {
        Manager.UserDataManager.Instance = new Manager.UserDataManager();
        Manager.UserDataManager.Instance.GetUserData(0).IsEntry = true;

        Step("fresh process");
        TotalIs("at launch", 0.0);

        Step("first track begins (trackCount=0, new credit)");
        Tracker.BeginTrack(0);
        Check("Active", Tracker.Active ? 1 : 0, 1);
        Check("TrackMeters", Tracker.TrackMeters, 0.0);
        TotalIs("after BeginTrack", 0.0);

        Step("play: 120 m (1P) + 30 m (2P) in the current track");
        Tracker.PlayerTrack[0] = 120.0;
        Tracker.PlayerTrack[1] = 30.0;
        TotalIs("live mid-track", 150.0);
        Check("SessionMeters (not yet ended)", Tracker.SessionMeters, 0.0);

        Step("track ends");
        Tracker.EndTrack();
        Check("Active", Tracker.Active ? 1 : 0, 0);
        Check("SessionMeters", Tracker.SessionMeters, 150.0);
        Check("LastTrackMeters", Tracker.LastTrackMeters, 150.0);
        TotalIs("after EndTrack", 150.0);

        Step("a second EndTrack (game fires OnRelease twice) must not double count");
        Tracker.EndTrack();
        TotalIs("after duplicate EndTrack", 150.0);
        Check("SessionMeters", Tracker.SessionMeters, 150.0);

        Step("next track in the SAME credit (trackCount=1) — total keeps going");
        Tracker.BeginTrack(1);
        Check("SessionMeters (same credit, not cleared)", Tracker.SessionMeters, 150.0);
        TotalIs("new track before any input", 150.0);
        Tracker.PlayerTrack[0] = 80.0;
        TotalIs("live in 2nd track", 230.0);
        Tracker.EndTrack();
        TotalIs("after 2nd track", 230.0);
        Check("SessionMeters", Tracker.SessionMeters, 230.0);

        Step("NEW credit (trackCount falls back to 0) — session clears, total must NOT");
        Tracker.BeginTrack(0);
        Check("SessionMeters (new credit cleared)", Tracker.SessionMeters, 0.0);
        Check("Tracks", Tracker.Tracks, 0);
        TotalIs("after new-credit reset", 230.0);
        Tracker.PlayerTrack[0] = 10.0;
        TotalIs("live in new credit", 240.0);

        Step("BeginTrack without OnRelease (defensive EndTrack inside BeginTrack)");
        Tracker.BeginTrack(1);
        TotalIs("after defensive EndTrack", 240.0);
        Check("TrackMeters (new track)", Tracker.TrackMeters, 0.0);
        Tracker.EndTrack();
        TotalIs("after that track ends", 240.0);

        Step("kcal follows the same conversion as everything else");
        Check("Energy.Kcal(TotalMeters)", Energy.Kcal(Tracker.TotalMeters), 1.0e-3 * 70.0 * 240.0);
        Check("Energy.Kcal(TrackMeters) is separate", Energy.Kcal(Tracker.TrackMeters), 0.0);

        Step("HUD text");
        string hud = Strings.Hud(16.1, 229.3, 32.2, 458.6, 48.3, 688.0);
        Console.WriteLine(hud.Replace("\n", " | "));
        Check("line count", hud.Split('\n').Length, 3);
        Check("has total label", hud.Contains("总计") ? 1 : 0, 1);
        Check("has total kcal", hud.Contains("总计 48.3 kcal") ? 1 : 0, 1);
        Check("order: track, session, total",
              hud.IndexOf("本曲") < hud.IndexOf("本轮") && hud.IndexOf("本轮") < hud.IndexOf("总计") ? 1 : 0, 1);

        Config.Current.Ascii = true;
        string asc = Strings.Hud(1, 2, 3, 4, 5, 6);
        Console.WriteLine(asc.Replace("\n", " | "));
        Check("ascii line count", asc.Split('\n').Length, 3);
        Check("ascii has Total", asc.Contains("Total") ? 1 : 0, 1);

        Console.WriteLine();
        Console.WriteLine("monotonic checks: " + _mono);
        Console.WriteLine(_fail == 0 ? "ALL PASS" : (_fail + " FAILED"));
        Environment.Exit(_fail == 0 ? 0 : 1);
    }
}
