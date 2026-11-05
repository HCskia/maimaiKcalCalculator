// 真实触摸/按键的轨迹采样与里程累计。
//
// 每帧采样 2 名玩家的 8 个按键（电平）+ 34 个触摸区（电平），只在「刚按下」的那一帧
// 记一次触点；触点按「离哪只手近」分配给两只手之一，累加手位到触点的距离。
// 按住不动不产生距离，所以长按不会虚增；漏打的音符依然会挥手，所以也会被算进去。
//
// ★ 玩家构成按「入场状态」判断（1P / 2P / 1P+2P 三种机台配置）：
//   游戏自己判断某一位玩家在不在场，用的就是 Manager.UserDataManager.GetUserData(i).IsEntry
//   （Process.GameProcess.OnStart 里拿它去 MonitorBase.Initialize(i, isEntry)）。
//   不在场的玩家不采样 —— 单人/单侧机台里 2P 的按键与触摸数组会把同一块面板再报一遍，
//   两边都累加会让里程正好翻倍。
//
// 性能：每帧固定 2×(8+34) 次布尔读取 + 少量浮点运算，无分配、无 LINQ。
using System;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

namespace MaimaiKcal
{
    public static class Tracker
    {
        public static bool Active;
        public static readonly double[] PlayerTrack = new double[2];    // 本曲（按玩家）
        public static readonly double[] PlayerSession = new double[2];  // 本轮（按玩家）

        // 本次启动以来的总计（不按轮清零）：只累加「已结束的曲目」，
        // 正在打的这一曲由 TotalMeters 现算，所以结算前后不会跳。
        static double _totalMeters;

        /// <summary>刚结束的那一曲（两名玩家合计）。</summary>
        public static double LastTrackMeters;

        /// <summary>本轮已经打完的曲数。</summary>
        public static int Tracks;

        /// <summary>这一曲/这一轮有哪些玩家在场：bit0 = 1P，bit1 = 2P。</summary>
        public static int ModeMask = 1;

        public static double TrackMeters { get { return PlayerTrack[0] + PlayerTrack[1]; } }
        public static double SessionMeters { get { return PlayerSession[0] + PlayerSession[1]; } }

        /// <summary>
        /// 本次启动以来的总里程 = 已结束曲目的合计 + 正在打的这一曲。
        /// 和「本轮」不同：换一轮不清零，只有重启游戏（静态字段跟着进程重建）才归零。
        /// </summary>
        public static double TotalMeters
        {
            get { return _totalMeters + (Active ? TrackMeters : 0.0); }
        }

        const int Players = 2;
        const int Buttons = 9;                // 0..7 是 8 个键，8 = Select
        const int Zones = 34;

        static readonly bool[,] PrevBtn = new bool[Players, Buttons];
        static readonly bool[,] PrevZone = new bool[Players, Zones];
        static readonly double[,] HandX = new double[Players, 2];
        static readonly double[,] HandZ = new double[Players, 2];
        static readonly bool[,] HandInit = new bool[Players, 2];
        static readonly bool[] PlayerOk = { true, true };

        static bool[,] _btnArr;     // Manager.InputManager.PlayerButtonPush（电平，私有）
        static bool[,,] _btnGame;   // 兜底：GameButtonPush[monitor, player, button]（脉冲）
        static bool _arraysTried;
        static int _lastTrackCount = -1;
        static float _trackStart;
        static float _dbgTimer;
        static float _modeTimer;
        static long _contacts;

        // 反射缓存：玩家入场状态 / 监视器激活状态
        static Type _gameMonitorType;
        static PropertyInfo _monitorIndexProp;
        static MethodInfo _monitorIsActive;
        static bool _monitorTried;

        // ---------------------------------------------------------------- 生命周期

        /// <summary>
        /// 一曲开始。trackCount = GamePlayManager.GetScoreListCount()，
        /// 它是「本轮已经打了几个 track」：同一轮里 1→2→3 递增，换一轮会回落。
        /// 选曲画面在两轮之间也会出现，所以不能用「回到选曲」判新一轮。
        /// </summary>
        public static void BeginTrack(int trackCount)
        {
            if (Active) EndTrack();      // 防御：上一曲没收到 OnRelease 也不让数据串场

            // 一轮内的 track 数只会变大；变小（或回到 1）说明换了一轮。
            // 同时兼容 0 基 / 1 基两种计数：1 基时上一轮最后一曲若也是 1，用 (<=1 && last>=1) 兜住。
            bool newCredit = _lastTrackCount < 0
                             || trackCount < _lastTrackCount
                             || (trackCount <= 1 && _lastTrackCount >= 1);

            if (newCredit)
            {
                PlayerSession[0] = PlayerSession[1] = 0.0;
                Tracks = 0;
                LastTrackMeters = 0.0;
                Diag.Msg("新的一轮（track=" + trackCount + "），本轮累计清零");
            }
            _lastTrackCount = trackCount;

            PlayerTrack[0] = PlayerTrack[1] = 0.0;
            _contacts = 0;
            _trackStart = Time.realtimeSinceStartup;
            Active = true;
            for (int p = 0; p < Players; p++)
            {
                for (int b = 0; b < Buttons; b++) PrevBtn[p, b] = false;
                for (int z = 0; z < Zones; z++) PrevZone[p, z] = false;
                HandInit[p, 0] = HandInit[p, 1] = false;
            }
            RefreshMode(true);
            EnsureArrays();
            Diag.Msg("开始一曲，里程清零（track=" + trackCount + "，玩家 " + MaskName(SampleMask) + "）");
        }

        public static void EndTrack()
        {
            if (!Active) return;
            Active = false;
            LastTrackMeters = TrackMeters;
            _totalMeters += TrackMeters;               // 总计只在这里入账：一曲结束一次，永不按轮清零
            PlayerSession[0] += PlayerTrack[0];
            PlayerSession[1] += PlayerTrack[1];
            Tracks++;
            float dur = Time.realtimeSinceStartup - _trackStart;
            if (dur < 1f) dur = 1f;
            Diag.Msg(string.Format("本曲结束：{0:0.0} m → {1:0.0} kcal（1P {2:0.0} m / 2P {3:0.0} m，触点 {4}，用时 {5:0} s）| 本轮累计 {6:0.0} m | 启动以来总计 {7:0.0} m / {8:0.0} kcal",
                                   TrackMeters, Energy.Kcal(TrackMeters), PlayerTrack[0], PlayerTrack[1],
                                   _contacts, dur, SessionMeters, TotalMeters, Energy.Kcal(TotalMeters)));
        }

        public static void ResetSession()
        {
            PlayerSession[0] = PlayerSession[1] = 0.0;
            Tracks = 0;
            LastTrackMeters = 0.0;
        }

        // ---------------------------------------------------------------- 每帧

        public static void Tick()
        {
            if (!Active) return;
            if (!Config.Current.Enabled) return;

            // 入场状态每秒重读一次：两曲之间可能有人加入 / 离开（1P、2P、1P+2P 三种构成）
            _modeTimer += Time.unscaledDeltaTime;
            if (_modeTimer >= 1f)
            {
                _modeTimer = 0f;
                if (Config.Current.Players < 0) RefreshMode(false);
            }

            int mask = SampleMask;
            for (int p = 0; p < Players; p++)
            {
                if (!PlayerOk[p]) continue;
                bool count = (mask & (1 << p)) != 0;
                // 不在场的玩家也读一遍、只更新「上一帧状态」，避免他下一曲加入时把
                // 正按着的键当成新按下（那一帧的手位还没有初始化，本来也不会加距离）。
                try { SamplePlayer(p, count); }
                catch (Exception e) { PlayerOk[p] = false; Diag.ErrorOnce("player" + p, e); }
            }

            if (Config.Current.Debug)
            {
                _dbgTimer += Time.unscaledDeltaTime;
                if (_dbgTimer >= 5f)
                {
                    _dbgTimer = 0f;
                    Diag.Msg(string.Format("[debug] 本曲 {0:0.0} m（1P {1:0.0} / 2P {2:0.0}，触点 {3}）| 本轮 {4:0.0} m | 总计 {5:0.0} m | 玩家 {6}",
                                           TrackMeters, PlayerTrack[0], PlayerTrack[1], _contacts, SessionMeters,
                                           TotalMeters, MaskName(SampleMask)));
                }
            }
        }

        static void SamplePlayer(int p, bool count)
        {
            for (int b = 0; b < 8; b++)
            {
                bool cur = BtnState(p, b);
                if (count && cur && !PrevBtn[p, b]) Contact(p, Geo.BtnX[b], Geo.BtnZ[b]);
                PrevBtn[p, b] = cur;
            }

            for (int z = 0; z < Zones; z++)
            {
                bool cur = ZoneState(p, z);
                if (count && cur && !PrevZone[p, z]) Contact(p, Geo.ZoneX[z], Geo.ZoneZ[z]);
                PrevZone[p, z] = cur;
            }
        }

        static void Contact(int p, double x, double z)
        {
            int best = 0;
            double bd = double.MaxValue;
            for (int h = 0; h < 2; h++)
            {
                double d = HandInit[p, h] ? Geo.Dist(HandX[p, h], HandZ[p, h], x, z) : 0.0;
                if (d < bd) { bd = d; best = h; }
            }
            if (HandInit[p, best]) PlayerTrack[p] += bd / 1000.0;
            HandX[p, best] = x;
            HandZ[p, best] = z;
            HandInit[p, best] = true;
            _contacts++;
        }

        // ---------------------------------------------------------------- 玩家构成

        /// <summary>
        /// 这一帧统计哪些玩家。players=auto（默认）时用入场状态检测出的 ModeMask，
        /// 也可在 cfg 里用 players=1p / 2p / both / off 强制指定。
        /// </summary>
        public static int SampleMask
        {
            get
            {
                int forced = Config.Current.Players;
                if (forced >= 0) return forced;
                int m = ModeMask;
                return m == 0 ? 3 : m;   // 检测暂时为 0（还没入场/结算后）时先按两名玩家兜底
            }
        }

        /// <summary>
        /// 读玩家入场状态：bit0 = 1P，bit1 = 2P。
        /// 返回 -1 表示读不到（游戏还没初始化），0 表示没有玩家入场。
        /// </summary>
        public static int DetectModeMask()
        {
            int m = -1;
            try
            {
                Manager.UserDataManager udm = Manager.UserDataManager.Instance;
                if (udm != null)
                {
                    m = 0;
                    for (int i = 0; i < Players; i++)
                    {
                        Manager.UserData u = udm.GetUserData(i);
                        if (u != null && u.IsEntry) m |= 1 << i;
                    }
                }
            }
            catch (Exception e) { Diag.ErrorOnce("usermask", e); m = -1; }

            if (m > 0) return m;

            // 兜底：这一曲的 GameMonitor 就是按 IsEntry 初始化的，IsActive() 与入场状态等价
            int mm = DetectFromMonitors();
            if (mm >= 0) return mm;

            return m;
        }

        static int DetectFromMonitors()
        {
            try
            {
                if (!_monitorTried)
                {
                    _monitorTried = true;
                    _gameMonitorType = AccessTools.TypeByName("Monitor.GameMonitor");
                    if (_gameMonitorType != null)
                    {
                        _monitorIndexProp = _gameMonitorType.GetProperty("MonitorIndex");
                        _monitorIsActive = _gameMonitorType.GetMethod("IsActive", Type.EmptyTypes);
                    }
                }
                if (_gameMonitorType == null || _monitorIndexProp == null || _monitorIsActive == null) return -1;

                UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(_gameMonitorType);
                int m = 0;
                bool any = false;
                for (int i = 0; i < objs.Length; i++)
                {
                    MonoBehaviour mb = objs[i] as MonoBehaviour;
                    if (mb == null) continue;
                    GameObject go = mb.gameObject;
                    if (go == null || !go.scene.IsValid()) continue;
                    any = true;
                    if ((bool)_monitorIsActive.Invoke(mb, null))
                        m |= 1 << (int)_monitorIndexProp.GetValue(mb, null);
                }
                return any ? m : -1;
            }
            catch (Exception e) { Diag.ErrorOnce("monitormask", e); return -1; }
        }

        static void RefreshMode(bool atTrackStart)
        {
            int m = DetectModeMask();
            if (m > 0)
            {
                if (m != ModeMask) Diag.Msg("玩家构成：" + MaskName(m) + "（1P entry=" + B(m, 0) + "，2P entry=" + B(m, 1) + "）");
                ModeMask = m;
            }
            else if (atTrackStart)
            {
                if (m == 0) Diag.Warn("这一曲开始时有 0 名玩家 IsEntry（按 " + MaskName(SampleMask) + " 计）");
                else Diag.Warn("读不到玩家入场状态（按 " + MaskName(SampleMask) + " 计）");
            }
        }

        static string B(int mask, int bit) { return (mask & (1 << bit)) != 0 ? "1" : "0"; }

        public static string MaskName(int mask)
        {
            switch (mask & 3)
            {
                case 1: return "1P";
                case 2: return "2P";
                case 3: return "1P+2P";
                default: return "无";
            }
        }

        // ---------------------------------------------------------------- 输入读取

        static bool BtnState(int p, int b)
        {
            if (_btnArr != null)
            {
                try { return _btnArr[p, b]; } catch { return false; }
            }
            if (_btnGame != null)
            {
                try { return _btnGame[1, p, b]; } catch { return false; }
            }
            return false;
        }

        static bool ZoneState(int p, int z)
        {
            return Manager.InputManager.GetTouchPanelAreaPush(p, (Manager.InputManager.TouchPanelArea)z);
        }

        static void EnsureArrays()
        {
            if (_arraysTried) return;
            try
            {
                Type t = typeof(Manager.InputManager);
                const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

                FieldInfo f = t.GetField("PlayerButtonPush", F);
                if (f != null) _btnArr = f.GetValue(null) as bool[,];

                if (_btnArr == null)
                {
                    FieldInfo g = t.GetField("GameButtonPush", F);
                    if (g != null) _btnGame = g.GetValue(null) as bool[,,];
                }

                if (_btnArr == null && _btnGame == null) return;   // 还没初始化好，下次再试

                _arraysTried = true;
                if (_btnArr != null)
                    Diag.Msg("按键状态：PlayerButtonPush " + _btnArr.GetLength(0) + "x" + _btnArr.GetLength(1));
                else
                    Diag.Warn("按键状态：退回 GameButtonPush（脉冲）");
            }
            catch (Exception e) { Diag.Error("读取按键数组失败", e); _arraysTried = true; }
        }
    }
}
