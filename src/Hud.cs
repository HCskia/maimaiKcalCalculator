// 常驻 HUD：挂到「上屏」的画布，显示本曲 / 本轮。
//
// 上屏在游戏里的层级是 RightMonitor/CommonProcess(Clone)/RearCanvas（1080×1920），
// 但不同版本/双人布局下名字可能不同，所以这里按「画布特征」挑：
//   1) 有独立相机且渲到 RenderTexture 的画布（上屏常这么画）
//   2) targetDisplay > 1 的画布（第二显示器）
//   3) 名字含 Rear/Front/Sub 的、纵向的、当前可见的
// cfg 里的 hudcanvas 可以直接指定画布名字（调试用）。
using System;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MaimaiKcal
{
    public static class Hud
    {
        static Overlay _ov;
        static Transform _host;
        static Canvas _ownCanvas;
        static string _last = "";
        static bool _dumped;

        public static void Tick()
        {
            try
            {
                if (!Config.Current.Enabled || !Config.Current.ShowHud) { Hide(); return; }
                if (Tracker.TrackMeters <= 0.0) { Hide(); return; }

                if (_ov == null || _host == null) Ensure();
                if (_ov == null) return;

                string s = Strings.Hud(
                    Energy.Kcal(Tracker.TrackMeters), Tracker.TrackMeters,
                    Energy.Kcal(Tracker.SessionMeters), Tracker.SessionMeters);

                if (s != _last) { _last = s; _ov.SetText(s); }
            }
            catch (Exception e) { Diag.ErrorOnce("hud", e); }
        }

        static void Ensure()
        {
            var all = UnityEngine.Object.FindObjectsOfType<Canvas>();
            if (!_dumped) { _dumped = true; Dump(all); }

            Canvas pick = Pick(all);
            if (pick != null)
            {
                _host = pick.transform;
                _ov = Overlay.Attach(pick.transform,
                                     new Vector2(1f, 0f), new Vector2(1f, 0f),
                                     new Vector2(-Config.Current.HudX, Config.Current.HudY),
                                     Config.Current.HudFont,
                                     TextAlignmentOptions.BottomRight, true);

                var rt = pick.transform as RectTransform;
                RaiseToTop(_ov, all);

                Diag.Msg("HUD 挂到: " + PathOf(pick.transform)
                         + (rt != null ? "  rect=" + rt.rect.width + "x" + rt.rect.height : "")
                         + "  display=" + pick.targetDisplay
                         + "  cam=" + (pick.worldCamera != null ? pick.worldCamera.name : "null")
                         + "  glayer=" + LayerMask.LayerToName(pick.gameObject.layer)
                         + "  sortlayer=" + SortLayerName(_ov)
                         + "  font=" + (_ov.Text != null && _ov.Text.font != null ? _ov.Text.font.name : "null"));
                return;
            }

            // 退路：自建一个屏幕空间 Canvas（会出现在主屏右下角）
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

            _host = go.transform;
            _ov = Overlay.Attach(go.transform,
                                 new Vector2(1f, 0f), new Vector2(1f, 0f),
                                 new Vector2(-Config.Current.HudX, Config.Current.HudY),
                                 Config.Current.HudFont,
                                 TextAlignmentOptions.BottomRight);
            Diag.Warn("没找到可用的上屏画布，HUD 退回主屏右下角");
        }

        static Canvas Pick(Canvas[] all)
        {            string forced = Config.Current.HudCanvas;
            if (!string.IsNullOrEmpty(forced))
            {
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    string p = PathOf(all[i].transform);
                    if (all[i].name == forced || p.EndsWith("/" + forced, StringComparison.OrdinalIgnoreCase))
                    {
                        Diag.Msg("HUD 画布由配置指定: " + p);
                        return all[i];
                    }
                }
                Diag.Warn("配置里的 hudcanvas=" + forced + " 没找到，改回自动挑选");
            }

            Canvas best = null;
            int bestScore = int.MinValue;
            for (int i = 0; i < all.Length; i++)
            {
                Canvas c = all[i];
                if (c == null || !c.gameObject.activeInHierarchy) continue;

                string path = PathOf(c.transform);

                int s = 0;
                // 上屏是「共享的」CommonProcess，不在 LeftMonitor/RightMonitor 里。
                // 那两个是 1P/2P 各自的主屏实例，挂上去会画到玩家看不到的那块屏。
                if (path.StartsWith("CommonProcess", StringComparison.OrdinalIgnoreCase)) s += 100;
                if (path.IndexOf("LeftMonitor/", StringComparison.OrdinalIgnoreCase) >= 0) s -= 150;
                if (path.IndexOf("RightMonitor/", StringComparison.OrdinalIgnoreCase) >= 0) s -= 150;

                if (c.worldCamera != null && c.worldCamera.targetTexture != null) s += 100;  // 渲到 RT 的
                if (c.targetDisplay > 1) s += 60;                                            // 第二显示器
                if (c.name == "RearCanvas") s += 40;
                else if (c.name.IndexOf("Front", StringComparison.OrdinalIgnoreCase) >= 0) s += 20;
                var rt = c.transform as RectTransform;
                if (rt != null && rt.rect.height > rt.rect.width * 1.2f) s += 10;            // 纵向画布

                if (s > bestScore) { bestScore = s; best = c; }
            }
            return best;
        }

        /// <summary>把场景里所有画布打一遍，方便定位上屏到底挂在哪。</summary>
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
                Diag.Msg(sb.ToString());
            }
            catch (Exception e) { Diag.Warn("dump canvas 失败: " + e.Message); }
        }

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

        static string PathOf(Transform t)
        {
            string s = t.name;
            Transform p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 6) { s = p.name + "/" + s; p = p.parent; }
            return s;
        }

        static void Hide()
        {
            if (_ov != null && _ov.gameObject.activeSelf) _ov.SetText(null);
            _last = "";
        }
    }
}
