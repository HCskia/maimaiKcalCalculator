// 真实触摸/按键的轨迹采样与里程累计。
//
// 每帧采样 2 名玩家的 8 个按键（电平）+ 34 个触摸区（电平），只在「刚按下」的那一帧
// 记一次触点；触点按「离哪只手近」分配给两只手之一，累加手位到触点的距离。
// 按住不动不产生距离，所以长按不会虚增；漏打的音符依然会挥手，所以也会被算进去。
//
// 性能：每帧固定 2×(8+34) 次布尔读取 + 少量浮点运算，无分配、无 LINQ。
using System;
using System.Reflection;
using UnityEngine;

namespace MaimaiKcal
{
    public static class Tracker
    {
        public static bool Active;
        public static double TrackMeters;     // 当前这一曲
        public static double SessionMeters;   // 本轮（一次投币的所有 track）
        public static double LastTrackMeters; // 刚结束的那一曲
        public static int Tracks;

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
        static long _contacts;

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
                SessionMeters = 0.0;
                Tracks = 0;
                LastTrackMeters = 0.0;
                Diag.Msg("新的一轮（track=" + trackCount + "），本轮累计清零");
            }
            _lastTrackCount = trackCount;

            TrackMeters = 0.0;
            _contacts = 0;
            _trackStart = Time.realtimeSinceStartup;
            Active = true;
            for (int p = 0; p < Players; p++)
            {
                for (int b = 0; b < Buttons; b++) PrevBtn[p, b] = false;
                for (int z = 0; z < Zones; z++) PrevZone[p, z] = false;
                HandInit[p, 0] = HandInit[p, 1] = false;
            }
            EnsureArrays();
            Diag.Msg("开始一曲，里程清零（track=" + trackCount + "）");
        }

        public static void EndTrack()
        {
            if (!Active) return;
            Active = false;
            LastTrackMeters = TrackMeters;
            SessionMeters += TrackMeters;
            Tracks++;
            float dur = Time.realtimeSinceStartup - _trackStart;
            if (dur < 1f) dur = 1f;
            Diag.Msg(string.Format("本曲结束：{0:0.0} m → {1:0.0} kcal（触点 {2}，用时 {3:0} s，均速 {4:0.00} m/s）| 本轮累计 {5:0.0} m",
                                   TrackMeters, Energy.Kcal(TrackMeters), _contacts, dur, TrackMeters / dur, SessionMeters));
        }

        public static void ResetSession()
        {
            SessionMeters = 0.0;
            Tracks = 0;
            LastTrackMeters = 0.0;
        }

        // ---------------------------------------------------------------- 每帧

        public static void Tick()
        {
            if (!Active) return;
            if (!Config.Current.Enabled) return;

            for (int p = 0; p < Players; p++)
            {
                if (!PlayerOk[p]) continue;
                // 单人游玩时 2P 的触摸/按键数组会把同一块面板再报一遍，
                // 两边都累加会把里程算成两倍，默认只统计 1P。
                if (p == 1 && !Config.Current.TrackSecondPlayer) continue;
                try { SamplePlayer(p); }
                catch (Exception e) { PlayerOk[p] = false; Diag.ErrorOnce("player" + p, e); }
            }

            if (Config.Current.Debug)
            {
                _dbgTimer += Time.unscaledDeltaTime;
                if (_dbgTimer >= 5f)
                {
                    _dbgTimer = 0f;
                    Diag.Msg(string.Format("[debug] 本曲 {0:0.0} m / 触点 {1} | 本轮 {2:0.0} m",
                                           TrackMeters, _contacts, SessionMeters));
                }
            }
        }

        static void SamplePlayer(int p)
        {
            for (int b = 0; b < 8; b++)
            {
                bool cur = BtnState(p, b);
                if (cur && !PrevBtn[p, b]) Contact(p, Geo.BtnX[b], Geo.BtnZ[b]);
                PrevBtn[p, b] = cur;
            }

            for (int z = 0; z < Zones; z++)
            {
                bool cur = ZoneState(p, z);
                if (cur && !PrevZone[p, z]) Contact(p, Geo.ZoneX[z], Geo.ZoneZ[z]);
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
            if (HandInit[p, best]) TrackMeters += bd / 1000.0;
            HandX[p, best] = x;
            HandZ[p, best] = z;
            HandInit[p, best] = true;
            _contacts++;
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
