// .ma2 谱面解析 + 手部里程计算。
//
// 里程口径（与选曲「预计消耗」一致）：
//   TAP/BRK/HLD/EX  → 按键点；距离 = 上一手位到该键的直线距离
//   滑条            → 起点到终点的弧长（沿按键环，跨 d 格 = d × 45°）
//   触摸            → 触摸区中心点
//   两只手按「离上一手位最近」贪心分配（与线下 Python 试算一致）
using System;
using System.Collections.Generic;
using System.IO;

namespace MaimaiKcal
{
    public static class Ma2
    {
        public struct Stat
        {
            public double Meters;
            public int Notes;     // TAP + 滑条 + 触摸
            public int Touches;
        }

        struct Ev
        {
            public long Key;
            public int Kind;      // 0=tap 1=slide 2=touch
            public int A;
            public int B;
        }

        /// <summary>解析一张谱，返回手部里程。</summary>
        public static Stat Parse(string path)
        {
            var st = new Stat();
            string text;
            try { text = File.ReadAllText(path); }
            catch { return st; }

            var ev = new List<Ev>(1024);
            int[] fs = new int[8];
            int n = text.Length, i = 0;

            while (i < n)
            {
                int eol = text.IndexOf('\n', i);
                if (eol < 0) eol = n;

                int kind = Classify(text, i);
                if (kind >= 0)
                {
                    int cnt = Fields(text, i, eol, fs);
                    if (cnt >= 4)
                    {
                        if (kind == 0) AddTap(text, fs, cnt, eol, ev, ref st);
                        else if (kind == 1) { if (cnt >= 5) AddSlide(text, fs, cnt, eol, ev, ref st); }
                        else AddTouch(text, fs, cnt, eol, ev, ref st, kind == 3);
                    }
                }
                i = eol + 1;
            }

            if (ev.Count == 0) return st;

            ev.Sort(CompareEv);

            double[] hx = new double[2], hz = new double[2];
            bool[] init = new bool[2];
            double total = 0.0;

            for (int k = 0; k < ev.Count; k++)
            {
                Ev e = ev[k];
                double sx, sz, ex, ez, extra;
                if (e.Kind == 0)
                {
                    sx = Geo.BtnX[e.A]; sz = Geo.BtnZ[e.A];
                    ex = sx; ez = sz; extra = 0.0;
                }
                else if (e.Kind == 1)
                {
                    sx = Geo.BtnX[e.A]; sz = Geo.BtnZ[e.A];
                    ex = Geo.BtnX[e.B]; ez = Geo.BtnZ[e.B];
                    extra = Geo.SlideArc(e.A, e.B);
                }
                else
                {
                    sx = Geo.ZoneX[e.A]; sz = Geo.ZoneZ[e.A];
                    ex = sx; ez = sz; extra = 0.0;
                }

                int best = 0;
                double bd = double.MaxValue;
                for (int h = 0; h < 2; h++)
                {
                    double d = init[h] ? Geo.Dist(hx[h], hz[h], sx, sz) : 0.0;
                    if (d < bd) { bd = d; best = h; }
                }
                total += bd + extra;
                hx[best] = ex; hz[best] = ez; init[best] = true;
            }

            st.Meters = total / 1000.0;
            return st;
        }

        static int CompareEv(Ev a, Ev b) { return a.Key.CompareTo(b.Key); }

        /// <summary>
        /// 记录类型归一化。谱面有两代写法：
        ///   旧：TAP / HLD / BRK / STR / SI_ / SCR / TTP / THO / XTP / XHO
        ///   新（1.04+）：NMTAP / NMHLD / NMSTR / NMSI_ / NMTTP / NMTHO / EXTAP / EXHLD / BRTAP / BXTAP
        /// 返回 0=按键类 1=滑条体 2=触摸 3=触摸长按，-1=不关心（含滑条起点、元数据）。
        /// </summary>
        static int Classify(string s, int i)
        {
            int p = i;
            int n = s.Length;

            if (p + 2 < n && s[p] == 'B' && s[p + 1] == 'R' && s[p + 2] == 'K') return 0;   // 旧版 BREAK

            if (p + 1 < n)
            {
                char a = s[p], b = s[p + 1];
                if (a == 'N' && b == 'M') p += 2;
                else if (a == 'E' && b == 'X') p += 2;
                else if (a == 'B' && b == 'X') p += 2;
                else if (a == 'B' && b == 'R') p += 2;        // BRTAP
                else if (a == 'C' && b == 'N') p += 2;        // CNSI_ 之类的连段滑条
                else if (a == 'X' && (b == 'T' || b == 'H')) p += 1;   // XTP / XHO
            }

            if (p + 1 >= n) return -1;
            char c0 = s[p], c1 = s[p + 1];

            if (c0 == 'T')
            {
                if (c1 == 'A') return 0;      // TAP / EXTAP / BRTAP / BXTAP
                if (c1 == 'P') return 0;      // XTP（X 已剥）
                if (c1 == 'T') return 2;      // TTP
                if (c1 == 'H') return 3;      // THO
                return -1;
            }
            if (c0 == 'H')
            {
                if (c1 == 'L') return 0;      // HLD
                if (c1 == 'O') return 0;      // XHO
                return -1;
            }
            if (c0 == 'S')
            {
                if (c1 == 'T') return -1;     // STR / EXSTR：滑条起点，体另有记录
                return 1;                     // SI_ / SCR / SCL / SV_ / SF_ / SUR / ...
            }
            return -1;
        }

        static void AddTap(string s, int[] fs, int cnt, int eol, List<Ev> ev, ref Stat st)
        {
            int pos = IntAt(s, fs[3], FieldEnd(fs, 3, cnt, eol));
            if (pos < 0 || pos > 7) return;
            ev.Add(new Ev { Key = Key(s, fs, cnt, eol), Kind = 0, A = pos, B = pos });
            st.Notes++;
        }

        static void AddSlide(string s, int[] fs, int cnt, int eol, List<Ev> ev, ref Stat st)
        {
            int a = IntAt(s, fs[3], FieldEnd(fs, 3, cnt, eol));
            int b = IntAt(s, fs[cnt - 1], FieldEnd(fs, cnt - 1, cnt, eol));
            if (a < 0 || a > 7 || b < 0 || b > 7) return;
            ev.Add(new Ev { Key = Key(s, fs, cnt, eol), Kind = 1, A = a, B = b });
            st.Notes++;
        }

        /// <summary>TTP: 类型 m t pos area digit sensor；THO 在 pos 后多一个时长。</summary>
        static void AddTouch(string s, int[] fs, int cnt, int eol, List<Ev> ev, ref Stat st, bool isHold)
        {
            int pos = IntAt(s, fs[3], FieldEnd(fs, 3, cnt, eol));
            int ai = isHold ? 5 : 4;
            int di = isHold ? 6 : 5;
            if (pos < 0 || pos > 8 || ai >= cnt) return;
            char area = s[fs[ai]];
            int digit = IntAt(s, fs[di], FieldEnd(fs, di, cnt, eol));
            int z = ZoneIndex(area, pos, digit);
            if (z < 0) return;
            ev.Add(new Ev { Key = Key(s, fs, cnt, eol), Kind = 2, A = z, B = z });
            st.Notes++;
            st.Touches++;
        }

        static int ZoneIndex(char area, int pos, int digit)
        {
            if (pos < 0 || pos > 7) return -1;
            switch (area)
            {
                case 'A': return 0 + pos;
                case 'B': return 8 + pos;
                case 'C': return 16 + (digit != 0 ? 1 : 0);
                case 'D': return 18 + pos;
                case 'E': return 26 + pos;
            }
            return -1;
        }

        static long Key(string s, int[] fs, int cnt, int eol)
        {
            int m = IntAt(s, fs[1], FieldEnd(fs, 1, cnt, eol));
            int t = IntAt(s, fs[2], FieldEnd(fs, 2, cnt, eol));
            return (long)m * 1000000L + t;
        }

        static int FieldEnd(int[] fs, int k, int cnt, int eol)
        {
            return (k + 1 < cnt) ? fs[k + 1] - 1 : eol;
        }

        static int Fields(string s, int start, int end, int[] starts)
        {
            int c = 0;
            starts[0] = start;
            for (int i = start; i < end; i++)
            {
                if (s[i] == '\t')
                {
                    c++;
                    if (c >= starts.Length) return c;
                    starts[c] = i + 1;
                }
            }
            return c + 1;
        }

        static int IntAt(string s, int a, int b)
        {
            int v = 0;
            bool any = false;
            for (int i = a; i < b && i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { v = v * 10 + (c - '0'); any = true; }
                else if (any) break;
                else if (c == ' ' || c == '+') continue;
                else break;
            }
            return any ? v : -1;
        }
    }
}
