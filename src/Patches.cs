// Harmony 挂点：
//   Process.MusicSelectProcess.OnUpdate   —— 每帧刷新选曲画面（同时负责「新一轮清零」）
//   Process.GameProcess.OnStart/OnRelease —— 一曲开始/结束（真实触摸里程的边界）
// 结算画面没有单独挂钩：试过 Monitor.ResultMonitor.Initialize，挂上了但画不出来，
// 最后改成上屏常驻 HUD（见 Hud.cs）。
using System;
using System.Reflection;
using HarmonyLib;

namespace MaimaiKcal
{
    public static class Patches
    {
        public static void Install(HarmonyLib.Harmony h)
        {
            Hook(h, "Process.MusicSelectProcess", "OnUpdate", "Select_Update");
            Hook(h, "Process.GameProcess", "OnStart", "Game_Start");
            Hook(h, "Process.GameProcess", "OnRelease", "Game_Release");
            // 一轮（续关流程）结束后的存盘窗口：把本轮收集的每曲记录追加到当日 JSON
            Hook(h, "Process.DataSaveProcess", "OnStart", "DataSave_Start");
            Hook(h, "Process.DataSaveProcess", "OnRelease", "DataSave_Release");
        }

        static void Hook(HarmonyLib.Harmony h, string typeName, string methodName, string postfixName)
        {
            try
            {
                Type t = AccessTools.TypeByName(typeName);
                if (t == null) { Diag.Error("找不到类型 " + typeName); return; }

                MethodInfo m = AccessTools.Method(t, methodName);
                if (m == null) { Diag.Error("找不到方法 " + typeName + "." + methodName); return; }

                MethodInfo pm = typeof(Patches).GetMethod(postfixName,
                    BindingFlags.Public | BindingFlags.Static);
                if (pm == null) { Diag.Error("找不到 postfix " + postfixName); return; }

                h.Patch(m, postfix: new HarmonyMethod(pm));
                Diag.Msg("已挂钩 " + typeName + "." + methodName);
            }
            catch (Exception e) { Diag.Error("挂钩 " + typeName + "." + methodName + " 失败", e); }
        }

        // ---------------------------------------------------------------- postfix

        public static void Select_Update(Process.MusicSelectProcess __instance)
        {
            try { SelectOverlay.Refresh(__instance); }
            catch (Exception e) { Diag.ErrorOnce("select_hook", e); }
        }

        public static void Game_Start(Process.GameProcess __instance)
        {
            try
            {
                int trackCount = 0;
                try { trackCount = Manager.GamePlayManager.Instance.GetScoreListCount(); }
                catch (Exception e) { Diag.Warn("读 track 数失败: " + e.Message); }
                Tracker.BeginTrack(trackCount);
            }
            catch (Exception e) { Diag.ErrorOnce("game_start", e); }
        }

        public static void Game_Release(Process.GameProcess __instance)
        {
            try { Tracker.EndTrack(); }
            catch (Exception e) { Diag.ErrorOnce("game_release", e); }
        }

        /// <summary>续关 / 结算后的 DataSaveProcess：本轮已经打完，统一追加到当日 JSON。</summary>
        public static void DataSave_Start()
        {
            try { RecordCollector.Flush("round-end"); }
            catch (Exception e) { Diag.ErrorOnce("record_datasave_start", e); }
        }

        /// <summary>兜底：OnStart 没赶上（或写失败后重试）再 flush 一次，已写过的按 key 去重。</summary>
        public static void DataSave_Release()
        {
            try { RecordCollector.Flush("round-end-release"); }
            catch (Exception e) { Diag.ErrorOnce("record_datasave_release", e); }
        }
    }
}
