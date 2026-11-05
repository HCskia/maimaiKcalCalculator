// 常驻 HUD：挂到「上屏」的 RearCanvas，显示本曲 / 本轮。
//
// 机台的玩家构成有三种（1P / 2P / 1P+2P），游戏自己是用
// Manager.UserDataManager.GetUserData(i).IsEntry（= Process.GameProcess.OnStart 里
// MonitorBase.Initialize(i, isEntry) 的入参）来判断某一位玩家在不在场的。
// 这里照抄这个判据，把 HUD 画到「在场玩家那一侧的屏」上：
//   只有 1P → LeftMonitor 那半屏（monitorIndex 0）
//   只有 2P → RightMonitor 那半屏（monitorIndex 1）
//   1P+2P  → 两边都画（数字是本曲 / 本轮的合计）
// 一块屏都没识别出来时才退回老逻辑（按特征挑画布），再不行自建一块屏幕空间画布。
// cfg 里的 hudmonitor 可以强制指定画哪几块屏，hudcanvas 可以强制指定画布（调试用）。
//
// 宿主画布不按名字猜：先找游戏自己的 Monitor.CommonMonitor（monitorIndex 0=1P/Left、
// 1=2P/Right），它的 transform 就是 <Monitor>/<CommonProcess(Clone)>，往下取 RearCanvas。
// 启动阶段（CommonProcess 还没创建）只会有 LeftMonitor/MouseTouchPanel 这种调试画布，
// 所以「兜底挑画布」要等几秒，而且要排除 MouseTouchPanel，不能一直占着坑。
using System;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;

namespace MaimaiKcal
{
    public static class Hud
    {
        const int Screens = 2;
        const float GraceSeconds = 20f;   // 启动后等多久才开始用「兜底画布」

        static readonly Overlay[] Ov = new Overlay[Screens];
        static readonly Transform[] Host = new Transform[Screens];
        static readonly int[] Owner = { 0, 1 };   // 这块 overlay 服务哪位玩家：0/1；-1 = 谁有数据都画
        static readonly string[] Last = { "", "" };
        static readonly bool[] HostVisible = { true, true };   // 这块宿主是否真的落在相机视口里

        static Canvas _forcedCv;
        static Overlay _forcedOv;
        static string _forcedLast = "";

        static Canvas _fallbackCv;     // 兜底 A：按特征挑到的画布
        static Overlay _fallbackOv;
        static string _fallbackLast = "";

        static Canvas _ownCanvas;      // 兜底 B：自建屏幕空间 Canvas
        static Overlay _ownOv;
        static string _ownLast = "";

        static int _dumpStage;
        static float _bootTime = -1f;
        static float _nextScan;
        static int _loggedMask = int.MinValue;

        public static void Tick()
        {
            try
            {
                if (!Config.Current.Enabled || !Config.Current.ShowHud) { HideAll(); return; }
                if (_bootTime < 0f) _bootTime = Time.unscaledTime;

                // debug=1 时从启动就每秒扫一次：不用真打一首，日志里就能看到两块屏的宿主与玩家构成
                if (Config.Current.Debug && Time.unscaledTime >= _nextScan)
                {
                    _nextScan = Time.unscaledTime + 1f;
                    Ensure();
                }

                // debug=1 且还没开始打时也画一个 0 值面板，方便在机台上直接对位（hudx/hudy）
                // 总计是跨轮累计的，所以「这一曲刚开打、本曲还是 0」时也要继续显示，
                // 不能让面板随每曲开始闪一下。
                if (Tracker.TrackMeters <= 0.0 && Tracker.TotalMeters <= 0.0 && !Config.Current.Debug) { HideAll(); return; }

                if (Time.unscaledTime >= _nextScan) { _nextScan = Time.unscaledTime + 1f; Ensure(); }

                int mask = HudMask;
                if (mask != _loggedMask)
                {
                    _loggedMask = mask;
                    Diag.Msg("HUD 显示模式：" + Tracker.MaskName(mask) + "（monitor 0 = 1P/Left，1 = 2P/Right）");
                }

                string s = Strings.Hud(
                    Energy.Kcal(Tracker.TrackMeters), Tracker.TrackMeters,
                    Energy.Kcal(Tracker.SessionMeters), Tracker.SessionMeters,
                    Energy.Kcal(Tracker.TotalMeters), Tracker.TotalMeters);

                if (_forcedOv != null)
                {
                    if (_forcedLast != s) { _forcedLast = s; _forcedOv.SetText(s); }
                    return;
                }

                if (Ov[0] != null || Ov[1] != null)
                {
                    // 相机只看得到其中一块时（单侧屏 / 窗口只覆盖半边），另一块画了也看不见，
                    // 所以先按相机视口过滤：能看到的那块永远优先，看不到的不画。
                    bool anyVisible = false;
                    for (int i = 0; i < Screens; i++)
                        if (Ov[i] != null && HostVisible[i]) anyVisible = true;

                    bool anyWanted = false;
                    for (int i = 0; i < Screens; i++)
                        if (Ov[i] != null && HostVisible[i] && Serves(Owner[i], mask)) anyWanted = true;

                    for (int i = 0; i < Screens; i++)
                    {
                        bool vis = HostVisible[i] || !anyVisible;   // 一块都测不出可见时退回「都画」
                        // 只找到一块屏时（单屏机台）不管玩家是哪一位都画在它上面
                        bool show = Ov[i] != null && vis && (Serves(Owner[i], mask) || !anyWanted);
                        if (!show) { HideSlot(i); continue; }
                        if (Last[i] != s) { Last[i] = s; Ov[i].SetText(s); }
                    }
                    return;
                }

                if (_fallbackOv != null)
                {
                    if (_fallbackLast != s) { _fallbackLast = s; _fallbackOv.SetText(s); }
                    return;
                }

                if (_ownOv != null)
                {
                    if (_ownLast != s) { _ownLast = s; _ownOv.SetText(s); }
                }
            }
            catch (Exception e) { Diag.ErrorOnce("hud", e); }
        }

        /// <summary>HUD 画在哪几块屏上。</summary>
        public static int HudMask
        {
            get
            {
                int forced = Config.Current.HudMonitor;
                if (forced >= 0) return forced;
                int m = Tracker.ModeMask;
                return m == 0 ? 3 : m;
            }
        }

        static bool Serves(int owner, int mask)
        {
            if (owner < 0) return mask != 0;
            return (mask & (1 << owner)) != 0;
        }

        // ---------------------------------------------------------------- 挂载

        static void Ensure()
        {
            var all = UnityEngine.Object.FindObjectsOfType<Canvas>();
            if (_dumpStage == 0) { _dumpStage = 1; Dump(all); }

            // 1) cfg 强制指定画布：只画那一块
            string forced = Config.Current.HudCanvas;
            if (!string.IsNullOrEmpty(forced))
            {
                if (_forcedCv == null)
                {
                    Canvas cv = FindByName(all, forced);
                    if (cv != null)
                    {
                        _forcedCv = cv;
                        _forcedOv = Overlay.Attach(cv.transform,
                                                   new Vector2(1f, 0f), new Vector2(1f, 0f),
                                                   new Vector2(-Config.Current.HudX, Config.Current.HudY),
                                                   Config.Current.HudFont,
                                                   TextAlignmentOptions.BottomRight, true);
                        RaiseToTop(_forcedOv, all);
                        Diag.Msg("HUD 画布由配置指定: " + PathOf(cv.transform));
                        ClearAuto();
                    }
                    else Diag.Warn("配置里的 hudcanvas=" + forced + " 没找到，改回自动挑选");
                }
                if (_forcedCv != null) return;
            }

            // 2) 按「玩家 → 那一侧的屏」挂
            for (int i = 0; i < Screens; i++)
            {
                if (Ov[i] != null && Host[i] != null) { HostVisible[i] = ComputeVisible(Host[i]); continue; }

                Transform host = FindMonitorHost(i, all);
                if (host == null)
                {
                    if (Ov[i] != null) { HideSlot(i); Ov[i] = null; }
                    Host[i] = null;
                    continue;
                }

                Host[i] = host;
                Owner[i] = i;
                HostVisible[i] = ComputeVisible(host);
                Ov[i] = Overlay.Attach(host,
                                       new Vector2(1f, 0f), new Vector2(1f, 0f),
                                       new Vector2(-Config.Current.HudX, Config.Current.HudY),
                                       Config.Current.HudFont,
                                       TextAlignmentOptions.BottomRight, true);
                RaiseToTop(Ov[i], all);

                var rt = host as RectTransform;
                Diag.Msg("HUD 挂到 " + (i == 0 ? "1P(LeftMonitor)" : "2P(RightMonitor)") + ": " + PathOf(host)
                         + (rt != null ? "  rect=" + rt.rect.width + "x" + rt.rect.height : "")
                         + "  layer=" + LayerMask.LayerToName(host.gameObject.layer)
                         + "  sort=" + SortLayerName(Ov[i])
                         + "  visible=" + HostVisible[i]
                         + "  font=" + (Ov[i].Text != null && Ov[i].Text.font != null ? Ov[i].Text.font.name : "null"));
            }

            if (_dumpStage == 1 && (Ov[0] != null || Ov[1] != null)) { _dumpStage = 2; Dump(all); }

            if (Ov[0] != null || Ov[1] != null) { ClearFallback(); DestroyOwn(); return; }

            // 3) 一块上屏画布都还没找到：先等游戏把 CommonProcess 实例化出来
            if (Time.unscaledTime - _bootTime < GraceSeconds) return;

            // 4) 兜底 A：按特征挑一块（排除 MouseTouchPanel 这类调试画布）
            if (_fallbackCv == null || _fallbackCv.gameObject == null)
            {
                Transform pick = Pick(all);
                if (pick != null)
                {
                    _fallbackCv = pick.GetComponentInParent<Canvas>();
                    _fallbackOv = Overlay.Attach(pick,
                                                 new Vector2(1f, 0f), new Vector2(1f, 0f),
                                                 new Vector2(-Config.Current.HudX, Config.Current.HudY),
                                                 Config.Current.HudFont,
                                                 TextAlignmentOptions.BottomRight, true);
                    RaiseToTop(_fallbackOv, all);
                    Diag.Warn("没识别出 1P/2P 的屏，HUD 退回按特征挑的画布: " + PathOf(pick));
                    DestroyOwn();
                    return;
                }
            }
            else return;

            // 5) 兜底 B：自建屏幕空间 Canvas（会出现在主屏右下角）
            if (_ownCanvas == null)
            {
                var go = new GameObject("MaimaiKcal_HudCanvas", typeof(Canvas), typeof(CanvasScaler));
                UnityEngine.Object.DontDestroyOnLoad(go);

                _ownCanvas = go.GetComponent<Canvas>();
                _ownCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _ownCanvas.sortingOrder = 30000;

                var sc = go.GetComponent<CanvasScaler>();
                sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                sc.referenceResolution = new Vector2(1920f, 1080f);
                sc.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                sc.matchWidthOrHeight = 0.5f;

                _ownOv = Overlay.Attach(go.transform,
                                        new Vector2(1f, 0f), new Vector2(1f, 0f),
                                        new Vector2(-Config.Current.HudX, Config.Current.HudY),
                                        Config.Current.HudFont,
                                        TextAlignmentOptions.BottomRight);
                Diag.Warn("没找到可用的上屏画布，HUD 退回主屏右下角");
            }
        }

        static void ClearAuto()
        {
            for (int i = 0; i < Screens; i++) { HideSlot(i); Ov[i] = null; Host[i] = null; }
            ClearFallback();
            DestroyOwn();
        }

        static void ClearFallback()
        {
            if (_fallbackOv != null && _fallbackOv.gameObject != null)
                _fallbackOv.SetText(null);
            _fallbackOv = null;
            _fallbackCv = null;
            _fallbackLast = "";
        }

        static void DestroyOwn()
        {
            if (_ownCanvas != null && _ownCanvas.gameObject != null)
                UnityEngine.Object.Destroy(_ownCanvas.gameObject);
            _ownCanvas = null; _ownOv = null; _ownLast = "";
        }

        /// <summary>
        /// 找第 idx 位玩家那一侧的宿主画布（idx=0 → 1P / LeftMonitor，1 → 2P / RightMonitor）。
        /// </summary>
        static Transform FindMonitorHost(int idx, Canvas[] all)
        {
            // 1) 游戏自己的监视器组件：monitorIndex 与玩家号一致，transform 就是 CommonProcess(Clone)
            try
            {
                Type t = AccessTools.TypeByName("Monitor.CommonMonitor");
                if (t != null)
                {
                    PropertyInfo pi = t.GetProperty("MonitorIndex");
                    UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(t);
                    for (int i = 0; i < objs.Length; i++)
                    {
                        MonoBehaviour mb = objs[i] as MonoBehaviour;
                        if (mb == null || pi == null) continue;
                        GameObject go = mb.gameObject;
                        if (go == null || !go.scene.IsValid()) continue;
                        if ((int)pi.GetValue(mb, null) != idx) continue;

                        Transform rear = FindChildCanvas(go.transform, "RearCanvas");
                        if (rear != null) return UpperScreen(rear);

                        Transform anyCv = AnyChildCanvas(go.transform);
                        if (anyCv != null) return anyCv;
                    }
                }
            }
            catch (Exception e) { Diag.ErrorOnce("findmonitor", e); }

            // 2) 退路：按层级名字找 LeftMonitor / RightMonitor 下面的 RearCanvas
            return FindHostByPath(idx, all);
        }

        static Transform FindHostByPath(int idx, Canvas[] all)
        {
            string side = idx == 0 ? "LeftMonitor" : "RightMonitor";
            Canvas best = null;
            int bestScore = -1;
            for (int i = 0; i < all.Length; i++)
            {
                Canvas c = all[i];
                if (c == null || c.name != "RearCanvas") continue;

                string p = PathOf(c.transform, 10);
                if (p.IndexOf(side, StringComparison.OrdinalIgnoreCase) < 0) continue;

                int s = 0;
                if (p.IndexOf("CommonProcess", StringComparison.OrdinalIgnoreCase) >= 0) s += 20;
                if (DescendantOfMonitor(c.transform, side)) s += 10;
                var rt = c.transform as RectTransform;
                if (rt != null && rt.rect.height > rt.rect.width * 1.2f) s += 5;
                if (s > bestScore) { bestScore = s; best = c; }
            }
            return best != null ? UpperScreen(best.transform) : null;
        }

        /// <summary>
        /// RearCanvas 是整块 1080×1920 面板，其中只有顶部 1080×450 的 Sub 是「上屏」，
        /// 下面是主屏的圆形游玩区（四角被机台挡住，画在整块画布右下角等于画到看不见的地方）。
        /// HUD 要挂到 Sub 的右下角 —— 也就是信息栏/玩家装饰底板那块。
        /// </summary>
        static Transform UpperScreen(Transform rear)
        {
            if (rear == null) return null;

            Transform sub = rear.Find("Sub");
            if (sub != null) return sub;

            // 退路：按名字找，Sub 可能不在第一层
            Transform[] kids = rear.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < kids.Length; i++)
                if (kids[i] != null && kids[i].name == "Sub") return kids[i];

            return rear;
        }

        static bool DescendantOfMonitor(Transform t, string side)
        {
            Transform p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 10) { if (p.name == side) return true; p = p.parent; }
            return false;
        }

        static Transform FindChildCanvas(Transform root, string name)
        {
            if (root == null) return null;
            Transform direct = root.Find(name);
            if (direct != null) { var cv = direct.GetComponent<Canvas>(); if (cv != null) return direct; }

            Canvas[] cvs = root.GetComponentsInChildren<Canvas>(true);
            for (int i = 0; i < cvs.Length; i++)
                if (cvs[i] != null && cvs[i].name == name) return cvs[i].transform;
            return null;
        }

        static Transform AnyChildCanvas(Transform root)
        {
            Canvas[] cvs = root.GetComponentsInChildren<Canvas>(true);
            for (int i = 0; i < cvs.Length; i++)
                if (cvs[i] != null && cvs[i].transform != root) return cvs[i].transform;
            return null;
        }

        static Canvas FindByName(Canvas[] all, string name)
        {
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                string p = PathOf(all[i].transform);
                if (all[i].name == name || p.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
                    return all[i];
            }
            return null;
        }

        static Camera _cam;

        /// <summary>
        /// 这块宿主是否真的落在主相机视口里。上屏两块画布在世界坐标里并排摆着
        /// （LeftMonitor x=-540 / RightMonitor x=+540），相机在哪一半上面由机台/窗口决定，
        /// 画在相机看不到的那一块等于没画。
        /// </summary>
        static bool ComputeVisible(Transform host)
        {
            try
            {
                var rt = host as RectTransform;
                if (rt == null) return true;

                Camera cam = _cam;
                if (cam == null)
                {
                    cam = Camera.main;
                    if (cam == null)
                    {
                        Camera[] cs = UnityEngine.Object.FindObjectsOfType<Camera>();
                        if (cs.Length > 0) cam = cs[0];
                    }
                    _cam = cam;
                }
                if (cam == null) return true;

                Vector2 rc = rt.rect.center;
                Vector3 world = rt.TransformPoint(new Vector3(rc.x, rc.y, 0f));
                Vector3 v = cam.WorldToViewportPoint(world);
                if (v.z <= 0f) return false;
                return v.x > -0.05f && v.x < 1.05f && v.y > -0.05f && v.y < 1.05f;
            }
            catch { return true; }
        }

        /// <summary>
        /// 兜底：挑一块最像上屏的画布。启动阶段的 LeftMonitor/MouseTouchPanel 这类
        /// 调试画布要排除；挑不到像样的就返回 null（改用自建画布）。
        /// </summary>
        static Transform Pick(Canvas[] all)
        {
            Canvas best = null;
            int bestScore = -1;
            for (int i = 0; i < all.Length; i++)
            {
                Canvas c = all[i];
                if (c == null || !c.gameObject.activeInHierarchy) continue;

                string path = PathOf(c.transform);
                if (path.IndexOf("MouseTouchPanel", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                var rt = c.transform as RectTransform;
                if (rt != null && (rt.rect.width < 400f || rt.rect.height < 400f)) continue;

                int s = 0;
                if (c.name == "RearCanvas") s += 40;
                if (path.IndexOf("CommonProcess", StringComparison.OrdinalIgnoreCase) >= 0) s += 30;
                if (rt != null && rt.rect.height > rt.rect.width * 1.2f) s += 15;
                if (c.worldCamera != null && c.worldCamera.targetTexture != null) s += 10;

                if (s > bestScore) { bestScore = s; best = c; }
            }
            return bestScore >= 40 ? UpperScreen(best.transform) : null;
        }

        // ---------------------------------------------------------------- 日志

        /// <summary>把场景里的画布与监视器打一遍，方便对不上时排查。</summary>
        static void Dump(Canvas[] all)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("=== Canvas 列表（").Append(all.Length).Append("）===");
                for (int i = 0; i < all.Length; i++)
                {
                    Canvas c = all[i];
                    if (c == null) continue;
                    var rt = c.transform as RectTransform;
                    Camera cam = c.worldCamera;
                    string camInfo = cam == null ? "null"
                        : (cam.name + (cam.targetTexture != null ? "[RT:" + cam.targetTexture.width + "x" + cam.targetTexture.height + "]" : ""));
                    sb.Append('\n').Append("  ").Append(PathOf(c.transform))
                      .Append(" | mode=").Append(c.renderMode)
                      .Append(" | disp=").Append(c.targetDisplay)
                      .Append(" | cam=").Append(camInfo)
                      .Append(" | layer=").Append(c.sortingLayerName).Append('(').Append(c.sortingLayerID).Append(')')
                      .Append(" | order=").Append(c.sortingOrder)
                      .Append(" | rect=").Append(rt != null ? rt.rect.width + "x" + rt.rect.height : "?")
                      .Append(" | active=").Append(c.gameObject.activeInHierarchy);
                }

                sb.Append("\n=== 监视器 / 玩家 ===");
                AppendMonitors(sb, "Monitor.CommonMonitor");
                AppendMonitors(sb, "Monitor.GameMonitor");
                AppendCameras(sb);
                sb.Append("\n  入场状态: ").Append(Tracker.MaskName(Tracker.DetectModeMask()))
                  .Append("（cfg players=").Append(Config.Current.Players < 0 ? "auto" : Tracker.MaskName(Config.Current.Players))
                  .Append("，hudmonitor=").Append(Config.Current.HudMonitor < 0 ? "auto" : Tracker.MaskName(Config.Current.HudMonitor)).Append("）");

                Diag.Msg(sb.ToString());
            }
            catch (Exception e) { Diag.Warn("dump canvas 失败: " + e.Message); }
        }

        static void AppendCameras(StringBuilder sb)
        {
            try
            {
                sb.Append("\n  Screen=").Append(Screen.width).Append('x').Append(Screen.height)
                  .Append(" displays=").Append(Display.displays.Length);
                Camera[] cams = UnityEngine.Object.FindObjectsOfType<Camera>();
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera c = cams[i];
                    if (c == null) continue;
                    sb.Append("\n   cam ").Append(PathOf(c.transform, 4))
                      .Append(" disp=").Append(c.targetDisplay)
                      .Append(" rt=").Append(c.targetTexture != null ? c.targetTexture.width + "x" + c.targetTexture.height : "null")
                      .Append(" rect=").Append(c.rect)
                      .Append(" depth=").Append(c.depth)
                      .Append(" enabled=").Append(c.enabled)
                      .Append(" culling=0x").Append(c.cullingMask.ToString("X8"));
                }

                // 两块上屏的 RearCanvas 投影到屏幕上的位置：看它们是不是并排/重叠/在屏外
                Camera main = Camera.main;
                if (main == null && cams.Length > 0) main = cams[0];
                if (main != null)
                    sb.Append("\n   main ").Append(main.transform.position)
                      .Append(" ortho=").Append(main.orthographic).Append('/').Append(main.orthographicSize);
                for (int i = 0; i < Screens; i++)
                {
                    if (Host[i] == null) { sb.Append("\n   host").Append(i).Append("=null"); continue; }
                    var rt = Host[i] as RectTransform;
                    if (rt == null || main == null) { sb.Append("\n   host").Append(i).Append('=').Append(PathOf(Host[i])); continue; }
                    Vector3[] c = new Vector3[4];
                    rt.GetWorldCorners(c);
                    Vector3 a = main.WorldToScreenPoint(c[0]);
                    Vector3 b = main.WorldToScreenPoint(c[2]);
                    sb.Append("\n   host").Append(i).Append(" screen=")
                      .Append(a.x.ToString("0")).Append(',').Append(a.y.ToString("0"))
                      .Append(" .. ").Append(b.x.ToString("0")).Append(',').Append(b.y.ToString("0"))
                      .Append(" visible=").Append(ComputeVisible(Host[i]));
                }
            }
            catch (Exception e) { sb.Append("\n  cam: ").Append(e.Message); }
        }

        static void AppendMonitors(StringBuilder sb, string typeName)
        {
            try
            {
                Type t = AccessTools.TypeByName(typeName);
                if (t == null) { sb.Append("\n  ").Append(typeName).Append(": 找不到类型"); return; }
                PropertyInfo pi = t.GetProperty("MonitorIndex");
                MethodInfo act = t.GetMethod("IsActive", Type.EmptyTypes);
                UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(t);
                int n = 0;
                for (int i = 0; i < objs.Length; i++)
                {
                    MonoBehaviour mb = objs[i] as MonoBehaviour;
                    if (mb == null) continue;
                    GameObject go = mb.gameObject;
                    if (go == null || !go.scene.IsValid()) continue;
                    n++;
                    sb.Append("\n  ").Append(typeName).Append(" #").Append(n).Append(" idx=")
                      .Append(pi != null ? pi.GetValue(mb, null) : "?")
                      .Append(" active=").Append(act != null ? act.Invoke(mb, null) : "?")
                      .Append(" path=").Append(PathOf(mb.transform))
                      .Append(" scene=").Append(go.activeInHierarchy);
                }
                if (n == 0) sb.Append("\n  ").Append(typeName).Append(": 场景里没有");
            }
            catch (Exception e) { sb.Append("\n  ").Append(typeName).Append(": ").Append(e.Message); }
        }

        // ---------------------------------------------------------------- 工具

        /// <summary>
        /// 把自己的子 Canvas 抬到「场景里用到的最高 sorting layer + 32760」，
        /// 这样能压过 FAST/LATE、结算特效这些后画的图层。
        /// </summary>
        static void RaiseToTop(Overlay ov, Canvas[] all)
        {
            try
            {
                var cv = ov.GetComponent<Canvas>();
                if (cv == null) return;

                var layers = SortingLayer.layers;
                int best = -1;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    int idx = LayerIndex(layers, all[i].sortingLayerID);
                    if (idx > best) best = idx;
                }
                if (best >= 0 && best < layers.Length) cv.sortingLayerID = layers[best].id;
                cv.sortingOrder = 32760;
            }
            catch (Exception e) { Diag.Warn("抬高 HUD 排序失败: " + e.Message); }
        }

        static int LayerIndex(SortingLayer[] layers, int id)
        {
            for (int i = 0; i < layers.Length; i++) if (layers[i].id == id) return i;
            return -1;
        }

        static string SortLayerName(Overlay ov)
        {
            var cv = ov != null ? ov.GetComponent<Canvas>() : null;
            return cv != null ? (cv.sortingLayerName + "(" + cv.sortingOrder + ")") : "?";
        }

        static string PathOf(Transform t) { return PathOf(t, 6); }

        static string PathOf(Transform t, int maxParents)
        {
            string s = t.name;
            Transform p = t.parent;
            int guard = 0;
            while (p != null && guard++ < maxParents) { s = p.name + "/" + s; p = p.parent; }
            return s;
        }

        // ---------------------------------------------------------------- 显隐

        static void HideSlot(int i)
        {
            if (Ov[i] != null && Ov[i].gameObject.activeSelf) Ov[i].SetText(null);
            Last[i] = "";
        }

        static void HideAll()
        {
            for (int i = 0; i < Screens; i++) HideSlot(i);
            if (_forcedOv != null && _forcedOv.gameObject.activeSelf) _forcedOv.SetText(null);
            _forcedLast = "";
            if (_fallbackOv != null && _fallbackOv.gameObject.activeSelf) _fallbackOv.SetText(null);
            _fallbackLast = "";
            if (_ownOv != null && _ownOv.gameObject.activeSelf) _ownOv.SetText(null);
            _ownLast = "";
            _loggedMask = int.MinValue;
        }
    }
}
