// maimaiKcalCalculator —— 把「手部移动里程」换算成卡路里，并显示在选曲 / 结算画面。
//
// 两套口径同时跑：
//   1) 谱面口径：启动时（后台线程）把全部 .ma2 解析一遍，算出每张谱的手部里程，
//      结果缓存到 Mods\maimaiKcalCalculator\charts.cache；选曲画面显示「预计消耗」。
//   2) 真实口径：游玩时每帧采样 8 个按键 + 34 个触摸区，重建手的轨迹并累计里程，
//      结算画面显示「本曲」与「本轮累计」。
//
// 能量模型：kcal = alpha * 体重(kg) * 里程(m)，alpha 默认 1.0e-3 kcal/(kg·m)。
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(MaimaiKcal.Core), "maimaiKcalCalculator", MaimaiKcal.Core.ModVersion, "MaimaiKcal", "")]
[assembly: MelonGame("sega-interactive", "Sinmai")]

namespace MaimaiKcal
{
    public class Core : MelonMod
    {
        public const string ModVersion = "0.1.0";
        public static Core Instance;

        public override void OnInitializeMelon()
        {
            Instance = this;
            Diag.Msg("v" + ModVersion + " 载入");
            Paths.Init();
            Config.Load();
            ChartStore.Init();                 // 后台线程：读缓存 / 解析全部谱面
            try { Patches.Install(new HarmonyLib.Harmony("maimaiKcalCalculator")); }
            catch (Exception e) { Diag.Error("挂钩子失败", e); }
        }

        public override void OnUpdate()
        {
            try { Tracker.Tick(); }
            catch (Exception e) { Diag.ErrorOnce("tracker", e); }
            try { Hud.Tick(); }
            catch (Exception e) { Diag.ErrorOnce("hud", e); }
        }

        public override void OnApplicationQuit()
        {
            Diag.Msg("游戏退出（本轮累计 " + Tracker.SessionMeters.ToString("0.0") + " m）");
        }
    }

    // ------------------------------------------------------------------ 日志

    public static class Diag
    {
        const string Prefix = "[MaimaiKcal] ";
        static readonly HashSet<string> Once = new HashSet<string>();

        public static void Msg(string s) { MelonLogger.Msg(Prefix + s); }
        public static void Warn(string s) { MelonLogger.Warning(Prefix + s); }
        public static void Error(string s) { MelonLogger.Error(Prefix + s); }
        public static void Error(string s, Exception e)
        {
            MelonLogger.Error(Prefix + s + " :: " + e.GetType().Name + ": " + e.Message);
            MelonLogger.Error(e.StackTrace);
        }

        /// <summary>同一个 key 只报一次，避免每帧刷屏。</summary>
        public static void ErrorOnce(string key, Exception e)
        {
            if (!Once.Add(key)) return;
            Error("异常（后续同类不再打印）", e);
        }
    }

    // ------------------------------------------------------------------ 路径

    public static class Paths
    {
        public static string PackageDir = "";
        public static string ConfigFile = "";
        public static string CacheDir = "";
        public static string CacheFile = "";
        public static string StreamingAssets = "";

        public static void Init()
        {
            try
            {
                // Application.dataPath = <Package>\Sinmai_Data
                string data = Application.dataPath;
                PackageDir = System.IO.Path.GetDirectoryName(data);
                StreamingAssets = System.IO.Path.Combine(data, "StreamingAssets");
            }
            catch (Exception e) { Diag.Error("定位游戏目录失败", e); }

            ConfigFile = System.IO.Path.Combine(PackageDir, "maimaiKcalCalculator.cfg");
            CacheDir = System.IO.Path.Combine(PackageDir, "Mods", "maimaiKcalCalculator");
            CacheFile = System.IO.Path.Combine(CacheDir, "charts.cache");
            try { System.IO.Directory.CreateDirectory(CacheDir); } catch { }
        }
    }

    // ------------------------------------------------------------------ 配置

    public class Cfg
    {
        public bool Enabled = true;

        public double WeightKg = 70.0;
        public double Alpha = 1.0e-3;

        public bool ShowSelect = true;
        public float SelectOffsetX = 0f;
        public float SelectOffsetY = -204f;
        public float SelectFont = 18f;

        public bool ShowHud = true;
        public float HudX = 40f;
        public float HudY = 24f;
        public float HudFont = 20f;
        public string HudCanvas = "";

        public bool TrackSecondPlayer = false;

        public bool Debug = false;
        public bool Ascii = false;
    }

    public static class Config
    {
        public static Cfg Current = new Cfg();

        public static void Load()
        {
            var c = new Cfg();
            try
            {
                if (System.IO.File.Exists(Paths.ConfigFile))
                {
                    foreach (string raw in System.IO.File.ReadAllLines(Paths.ConfigFile))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        Parse(c, k, v);
                    }
                    Diag.Msg("配置: " + Paths.ConfigFile);
                }
                else Diag.Msg("没有配置文件（用默认值）: " + Paths.ConfigFile);
            }
            catch (Exception e) { Diag.Error("读配置失败", e); }

            Current = c;
            Diag.Msg(string.Format("enabled={0} weight={1}kg alpha={2} select={3} hud={4} debug={5}",
                B(c.Enabled), c.WeightKg, c.Alpha, B(c.ShowSelect), B(c.ShowHud), B(c.Debug)));
        }

        static void Parse(Cfg c, string k, string v)
        {
            try
            {
                switch (k)
                {
                    case "enabled": c.Enabled = Bool(v, true); break;
                    case "weight": c.WeightKg = Double(v, c.WeightKg); break;
                    case "alpha": c.Alpha = Double(v, c.Alpha); break;
                    case "showselect": c.ShowSelect = Bool(v, true); break;
                    case "selectx": c.SelectOffsetX = (float)Double(v, c.SelectOffsetX); break;
                    case "selecty": c.SelectOffsetY = (float)Double(v, c.SelectOffsetY); break;
                    case "selectfont": c.SelectFont = (float)Double(v, c.SelectFont); break;
                    case "showresult": c.ShowHud = Bool(v, true); break;
                    case "resultx": c.HudX = (float)Double(v, c.HudX); break;
                    case "resulty": c.HudY = (float)Double(v, c.HudY); break;
                    case "resultfont": c.HudFont = (float)Double(v, c.HudFont); break;
                    case "showhud": c.ShowHud = Bool(v, true); break;
                    case "hudx": c.HudX = (float)Double(v, c.HudX); break;
                    case "hudy": c.HudY = (float)Double(v, c.HudY); break;
                    case "hudfont": c.HudFont = (float)Double(v, c.HudFont); break;
                    case "hudcanvas": c.HudCanvas = v; break;
                    case "track2p": c.TrackSecondPlayer = Bool(v, false); break;
                    case "debug": c.Debug = Bool(v, false); break;
                    case "ascii": c.Ascii = Bool(v, false); break;
                }
            }
            catch (Exception e) { Diag.Warn("配置项解析失败 " + k + "=" + v + " : " + e.Message); }
        }

        static bool Bool(string v, bool dflt)
        {
            if (string.IsNullOrEmpty(v)) return dflt;
            v = v.ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return dflt;
        }

        static double Double(string v, double dflt)
        {
            double d;
            if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
            return dflt;
        }

        static string B(bool b) { return b ? "1" : "0"; }
    }

    // ------------------------------------------------------------------ 反射小工具

    public static class Refl
    {
        static readonly Dictionary<string, FieldInfo> FieldCache = new Dictionary<string, FieldInfo>();
        static readonly Dictionary<string, MethodInfo> MethodCache = new Dictionary<string, MethodInfo>();

        public static FieldInfo Field(Type t, string name)
        {
            string key = t.FullName + "::" + name;
            FieldInfo f;
            if (FieldCache.TryGetValue(key, out f)) return f;
            f = AccessTools.Field(t, name);
            if (f == null)
                f = t.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            FieldCache[key] = f;
            if (f == null) Diag.Warn("找不到字段 " + key);
            return f;
        }

        public static object Get(object obj, string name)
        {
            if (obj == null) return null;
            FieldInfo f = Field(obj.GetType(), name);
            return f == null ? null : f.GetValue(obj);
        }

        public static MethodInfo Method(Type t, string name, Type[] args)
        {
            string key = t.FullName + "::" + name + "(" + (args == null ? "" : args.Length.ToString()) + ")";
            MethodInfo m;
            if (MethodCache.TryGetValue(key, out m)) return m;
            try { m = AccessTools.Method(t, name, args); } catch { m = null; }
            if (m == null)
            {
                try
                {
                    m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                                    null, args, null);
                }
                catch { m = null; }
            }
            MethodCache[key] = m;
            if (m == null) Diag.Warn("找不到方法 " + key);
            return m;
        }
    }
}
