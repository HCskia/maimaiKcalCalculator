// 一块「黑色半透明底 + 白字」的叠加面板（样式对齐 RatingPreview）。
// 字体优先用宿主节点自带的 TMP 字体（含中日文字形），找不到再退回 LiberationSans。
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MaimaiKcal
{
    public class Overlay : MonoBehaviour
    {
        public TextMeshProUGUI Text;
        public Image Bg;

        RectTransform _rt;
        RectTransform _textRt;
        float _fontSize;
        float _padX;
        float _padY;

        static TMP_FontAsset _font;
        static bool _fontTried;

        public static Overlay Attach(Transform parent, Vector2 anchor, Vector2 pivot,
                                     Vector2 pos, float fontSize, TextAlignmentOptions align)
        {
            return Attach(parent, anchor, pivot, pos, fontSize, align, false);
        }

        public static Overlay Attach(Transform parent, Vector2 anchor, Vector2 pivot,
                                     Vector2 pos, float fontSize, TextAlignmentOptions align, bool topMost)
        {
            var go = new GameObject("MaimaiKcal_Overlay", typeof(RectTransform));
            // ★ 必须跟宿主同一个 Unity layer：监视器相机的 cullingMask 只认 UI 层，
            //   新建的 GameObject 默认是 Default 层，会被相机直接剔除（挂上了但什么都看不到）。
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.SetAsLastSibling();

            if (topMost)
            {
                // 独立子 Canvas + overrideSorting：保证画在宿主 Canvas 的所有内容之上
                var cv = go.AddComponent<Canvas>();
                var host = parent.GetComponent<Canvas>();
                cv.overrideSorting = true;
                if (host != null)
                {
                    cv.sortingLayerID = host.sortingLayerID;
                    cv.renderMode = host.renderMode;
                    cv.worldCamera = host.worldCamera;
                }
                cv.sortingOrder = 30000;
            }

            var ov = go.AddComponent<Overlay>();
            ov.Build(anchor, pivot, pos, fontSize, align);
            return ov;
        }

        void Build(Vector2 anchor, Vector2 pivot, Vector2 pos, float fontSize, TextAlignmentOptions align)
        {
            _fontSize = fontSize;
            _padX = Mathf.Max(10f, fontSize * 0.9f);
            _padY = Mathf.Max(6f, fontSize * 0.45f);

            _rt = gameObject.GetComponent<RectTransform>();
            _rt.anchorMin = anchor;
            _rt.anchorMax = anchor;
            _rt.pivot = pivot;
            _rt.anchoredPosition = pos;
            _rt.sizeDelta = new Vector2(400f, 48f);
            _rt.localScale = Vector3.one;

            Bg = gameObject.AddComponent<Image>();
            Bg.color = new Color(0f, 0f, 0f, 0.55f);
            Bg.raycastTarget = false;

            var tgo = new GameObject("Text", typeof(RectTransform));
            tgo.layer = gameObject.layer;              // 同上：层必须跟宿主一致
            tgo.transform.SetParent(transform, false);

            Text = tgo.AddComponent<TextMeshProUGUI>();
            _textRt = tgo.GetComponent<RectTransform>();
            _textRt.anchorMin = Vector2.zero;
            _textRt.anchorMax = Vector2.one;
            _textRt.offsetMin = new Vector2(_padX * 0.5f, _padY * 0.5f);
            _textRt.offsetMax = new Vector2(-_padX * 0.5f, -_padY * 0.5f);
            _textRt.localScale = Vector3.one;

            Text.font = Font(transform.parent);
            Text.fontSize = fontSize;
            Text.fontStyle = FontStyles.Bold;
            Text.color = Color.white;
            Text.alignment = align;
            Text.enableWordWrapping = false;
            Text.raycastTarget = false;
            Text.overflowMode = TextOverflowModes.Overflow;
            Text.text = "";
        }

        public void SetText(string s)
        {
            if (Text == null) return;
            if (string.IsNullOrEmpty(s))
            {
                if (gameObject.activeSelf) gameObject.SetActive(false);
                return;
            }
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            if (Text.text == s) return;

            Text.text = s;
            Text.fontSize = _fontSize;

            // 按文字实际尺寸收紧面板（TMP 的 preferred 值对两行文本同样有效）
            Vector2 pref;
            try { pref = Text.GetPreferredValues(s); }
            catch { pref = new Vector2(420f, _fontSize * 1.4f); }

            if (float.IsNaN(pref.x) || float.IsNaN(pref.y) || pref.x <= 0f)
                pref = new Vector2(420f, _fontSize * 1.4f);

            float w = Mathf.Clamp(pref.x + _padX, 60f, 1600f);
            float h = Mathf.Clamp(pref.y + _padY, 24f, 600f);
            if (_rt != null) _rt.sizeDelta = new Vector2(w, h);

            _textRt.offsetMin = new Vector2(_padX * 0.5f, _padY * 0.5f);
            _textRt.offsetMax = new Vector2(-_padX * 0.5f, -_padY * 0.5f);
        }

        public void ApplyLayout(Vector2 anchor, Vector2 pivot, Vector2 pos, float fontSize)
        {
            if (_rt == null) return;
            _rt.anchorMin = anchor;
            _rt.anchorMax = anchor;
            _rt.pivot = pivot;
            _rt.anchoredPosition = pos;
            _rt.localScale = Vector3.one;
            _fontSize = fontSize;
            _padX = Mathf.Max(10f, fontSize * 0.9f);
            _padY = Mathf.Max(6f, fontSize * 0.45f);
            if (Text != null && Math.Abs(Text.fontSize - fontSize) > 0.01f) Text.fontSize = fontSize;
        }

        static TMP_FontAsset Font(Transform parent)
        {
            if (_font != null || _fontTried) return _font;
            _fontTried = true;

            // 1) 宿主节点自带的 TMP 文本：字体最贴近当前界面/语言
            try
            {
                if (parent != null)
                {
                    var t = parent.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (t != null && t.font != null)
                    {
                        _font = t.font;
                        Diag.Msg("叠加层字体: " + _font.name + "（取自宿主）");
                        return _font;
                    }
                }
            }
            catch (Exception e) { Diag.Warn("找宿主字体失败: " + e.Message); }

            // 2) 场景里任意一个 TMP 文本
            try
            {
                var all = Resources.FindObjectsOfTypeAll(typeof(TextMeshProUGUI)) as TextMeshProUGUI[];
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] != null && all[i].font != null)
                        {
                            _font = all[i].font;
                            Diag.Msg("叠加层字体: " + _font.name + "（取自场景）");
                            return _font;
                        }
                    }
                }
            }
            catch (Exception e) { Diag.Warn("找场景字体失败: " + e.Message); }

            try
            {
                _font = TMP_Settings.defaultFontAsset;
                if (_font != null) { Diag.Msg("叠加层字体: " + _font.name + "（TMP 默认）"); return _font; }
            }
            catch { }

            try
            {
                _font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
                if (_font != null) { Diag.Warn("叠加层字体: LiberationSans（可能缺中文字形）"); return _font; }
            }
            catch { }

            Diag.Warn("找不到任何 TMP 字体，文字可能不显示");
            return null;
        }
    }
}
