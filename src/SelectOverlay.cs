// 选曲画面：在中心 musiccard 上叠加「当前选中谱面的预计消耗」。
using System;
using TMPro;
using UnityEngine;

namespace MaimaiKcal
{
    public static class SelectOverlay
    {
        static Overlay _ov;
        static Transform _host;
        static int _lastId = -1, _lastDiff = -1;
        static string _lastText = "";

        public static void Refresh(Process.MusicSelectProcess proc)
        {
            try
            {
                if (!Config.Current.Enabled || !Config.Current.ShowSelect) { Hide(); return; }
                if (proc == null) { Hide(); return; }

                // 随机曲目卡是个占位项（游戏内部塞的是 musicId=8 / 难度 0），
                // 不能让它在卡片上显示一个假的预计值。
                if (IsRandomCard(proc)) { Hide(); return; }

                Process.MusicSelectProcess.MusicSelectData data = null;
                try { data = proc.GetMusic(0); } catch { }
                if (data == null || data.MusicData == null) { Hide(); return; }

                Manager.MaiStudio.MusicData music = data.MusicData;
                int id = music.GetID();

                int diff = -1;
                try { diff = proc.GetDifficulty(0, 0); } catch { }
                if (diff < 0) diff = data.Difficulty;
                if (diff < 0 || diff > 4) { Hide(); return; }       // 宴谱 / Strong 不处理

                if (!ChartStore.Ready) { Hide(); return; }

                ChartStore.Entry e;
                if (!ChartStore.TryGet(id, diff, out e)) { Hide(); return; }

                var mon = Monitor0(proc);

                if (id == _lastId && diff == _lastDiff && _ov != null)
                {
                    // 没换曲子：只保证它还挂在正确的位置上
                    if (mon != null && _host != null && _host == _ov.transform.parent) return;
                }

                // 只有换曲子/换难度才重新格式化，避免每帧产生垃圾
                if (id != _lastId || diff != _lastDiff)
                    _lastText = Strings.Select(Energy.Kcal(e.Meters), e.Meters);

                Transform card = FindCenterCard(mon);
                if (card == null) { Hide(); return; }

                if (_ov == null || _host != card)
                {
                    if (_ov != null) { try { UnityEngine.Object.Destroy(_ov.gameObject); } catch { } _ov = null; }
                    _ov = Overlay.Attach(card, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                                         new Vector2(Config.Current.SelectOffsetX, Config.Current.SelectOffsetY),
                                         Config.Current.SelectFont, TextAlignmentOptions.Center);
                    _host = card;
                    Diag.Msg("选曲叠加层挂到卡片: " + card.name);
                    if (Config.Current.Debug)
                    {
                        var rt = card as RectTransform;
                        if (rt != null) Diag.Msg(string.Format("卡片 rect = {0}x{1}, scale={2}",
                            rt.rect.width, rt.rect.height, card.localScale));
                    }
                }

                _ov.SetText(_lastText);
                if (id != _lastId || diff != _lastDiff)
                {
                    _lastId = id; _lastDiff = diff;
                    if (Config.Current.Debug)
                        Diag.Msg(string.Format("选曲: id={0} diff={1} 里程={2:0.0} m 预计={3:0.0} kcal",
                            id, diff, e.Meters, Energy.Kcal(e.Meters)));
                }
            }
            catch (Exception ex) { Diag.ErrorOnce("select", ex); }
        }

        /// <summary>
        /// 当前光标是不是「随机曲目」那张占位卡。
        /// 游戏在 AddRandomData 里造的这张卡：musicSelectData 里塞的是 musicId=8、难度 0，
        /// 只把 CombineMusicSelectData.isRandomScore 置 true 作为标记。
        /// </summary>
        static bool IsRandomCard(Process.MusicSelectProcess proc)
        {
            try
            {
                if (proc.RandomSelected) return true;

                var data = Refl.Get(proc, "_combineMusicDataList") as System.Collections.IList;
                if (data == null) return false;

                int cat = proc.CurrentCategorySelect;
                if (cat < 0 || cat >= data.Count) return false;

                var col = data[cat] as System.Collections.IList;
                if (col == null) return false;

                int idx = proc.CurrentMusicSelect;
                if (idx < 0 || idx >= col.Count) return false;

                object item = col[idx];
                if (item == null) return false;

                var f = Refl.Field(item.GetType(), "<isRandomScore>k__BackingField");
                return f != null && (bool)f.GetValue(item);
            }
            catch (Exception e)
            {
                Diag.ErrorOnce("randomcard", e);
                return false;
            }
        }

        static Monitor.MusicSelectMonitor Monitor0(Process.MusicSelectProcess proc)
        {
            try
            {
                var arr = proc.MonitorArray;
                if (arr != null && arr.Length > 0) return arr[0];
            }
            catch { }
            return null;
        }

        static void Hide()
        {
            if (_ov != null) _ov.SetText(null);
            _lastId = -1; _lastDiff = -1;
        }

        /// <summary>中心卡片：SpotArray[Center] → ChainObjectList[Center] → 缩放最大的那张卡。</summary>
        static Transform FindCenterCard(Monitor.MusicSelectMonitor mon)
        {
            if (mon == null) return null;
            try
            {
                object chain = Refl.Get(mon, "_musicChainList");
                if (chain == null) return null;

                int center = ToInt(Refl.Get(chain, "Center"), -1);
                object card = null;

                var spots = Refl.Get(chain, "SpotArray") as Array;
                if (spots != null && center >= 0 && center < spots.Length)
                    card = spots.GetValue(center);

                var list = Refl.Get(chain, "ChainObjectList") as System.Collections.IList;
                if (card == null && list != null && center >= 0 && center < list.Count)
                    card = list[center];

                if (card == null && list != null)
                {
                    float best = -1f;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var c = list[i] as Component;
                        if (c == null) continue;
                        float s = Math.Abs(c.transform.localScale.x) + Math.Abs(c.transform.localScale.y);
                        if (s > best) { best = s; card = c; }
                    }
                }

                var comp = card as Component;
                return comp != null ? comp.transform : null;
            }
            catch (Exception e)
            {
                Diag.ErrorOnce("card", e);
                return null;
            }
        }

        static int ToInt(object o, int dflt)
        {
            if (o == null) return dflt;
            try { return Convert.ToInt32(o); } catch { return dflt; }
        }
    }
}
