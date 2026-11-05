// 平面几何：8 个按键（半径 310 mm 的圆，45° 等分，1 号在右上）与 34 个触摸区中心。
// 坐标单位 mm；x 向右，z 向下（朝玩家）。
using System;

namespace MaimaiKcal
{
    public static class Geo
    {
        /// <summary>按键圆心所在圆的半径（实测按键环直径 62 cm）。</summary>
        public const double ButtonRadiusMm = 310.0;

        /// <summary>触摸面板半径（实测触屏圆直径 52.5 cm），只用于解释区坐标的来源。</summary>
        public const double PanelRadiusMm = 262.5;

        public static readonly double[] BtnX = new double[8];
        public static readonly double[] BtnZ = new double[8];

        // 34 个触摸区中心（mm），顺序与 Manager.InputManager/TouchPanelArea 枚举一致：
        // A1..A8, B1..B8, C1, C2, D1..D8, E1..E8
        // 由 TouchPannelSVG.txt 的图形按「面板圆 52.5 cm / 1064 单位」换算而来。
        public static readonly double[] ZoneX =
        {
             81.87,  197.67,  197.70,   81.90,  -81.93, -197.80, -197.77,  -81.90,  // A1..A8
             41.19,   99.68,   99.54,   41.06,  -41.35,  -99.58,  -99.72,  -41.48,  // B1..B8
            -23.32,   23.06,                                                          // C1, C2
             -0.02,  159.21,  225.21,  159.33,   24.46, -159.40, -225.40, -159.28,  // D1..D8
              0.25,  107.32,  151.73,  107.32,    0.25, -107.32, -151.73, -107.32,  // E1..E8
        };

        public static readonly double[] ZoneZ =
        {
           -197.67,  -81.87,   81.90,  197.77,  197.80,   81.93,  -81.90, -197.70,
            -99.59,  -41.02,   41.56,   99.71,   99.66,   41.53,  -40.97,  -99.54,
              0.00,    0.00,
           -225.10, -159.21,   -0.03,  159.28,  222.80,  159.40,   -0.03, -159.33,
           -151.73, -106.83,    0.25,  107.81,  151.73,  107.81,    0.25, -106.83,
        };

        static Geo()
        {
            // 1 号键在右上：从「正上方」顺时针转 22.5° 起步，每键 45°
            for (int k = 0; k < 8; k++)
            {
                double th = (22.5 + 45.0 * k) * Math.PI / 180.0;
                BtnX[k] = ButtonRadiusMm * Math.Sin(th);
                BtnZ[k] = -ButtonRadiusMm * Math.Cos(th);
            }
        }

        public static double Dist(double x1, double z1, double x2, double z2)
        {
            double dx = x1 - x2, dz = z1 - z2;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>两键之间的直线距离（弦长）。</summary>
        public static double ButtonDist(int i, int j)
        {
            int d = Math.Abs(i - j) % 8;
            if (d > 4) d = 8 - d;
            return 2.0 * ButtonRadiusMm * Math.Sin(Math.PI * d / 8.0);
        }

        /// <summary>滑条沿按键环走的弧长（跨 d 格 = d × 45°）。</summary>
        public static double SlideArc(int i, int j)
        {
            int d = Math.Abs(i - j) % 8;
            if (d > 4) d = 8 - d;
            return ButtonRadiusMm * Math.PI * d / 4.0;
        }
    }
}
