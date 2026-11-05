// Harmony 挂点：
//   Process.MusicSelectProcess.OnUpdate   —— 每帧刷新选曲画面（同时负责「新一轮清零」）
//   Process.GameProcess.OnStart/OnRelease —— 一曲开始/结束（真实触摸里程的边界）
//   Monitor.ResultMonitor.Initialize      —— 结算画面出现（显示本曲 + 本轮累计）
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
    }
}
