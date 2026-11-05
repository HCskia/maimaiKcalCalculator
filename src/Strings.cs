// 所有对外文案集中在这里；ascii=1 时退回纯 ASCII（日服字体缺简体字形时用）。
namespace MaimaiKcal
{
    public static class Strings
    {
        public static string Select(double kcal, double meters)
        {
            if (Config.Current.Ascii)
                return string.Format("Est. {0:0.0} kcal - {1:0} m", kcal, meters);
            return string.Format("预计 {0:0.0} kcal · 手部 {1:0} m", kcal, meters);
        }

        public static string ResultTrack(double kcal, double meters)
        {
            if (Config.Current.Ascii)
                return string.Format("This track {0:0.0} kcal - {1:0} m", kcal, meters);
            return string.Format("本曲 {0:0.0} kcal · 手部 {1:0} m", kcal, meters);
        }

        public static string ResultSession(double kcal, double meters)
        {
            if (Config.Current.Ascii)
                return string.Format("Session {0:0.0} kcal - {1:0} m", kcal, meters);
            return string.Format("本轮累计 {0:0.0} kcal · 手部 {1:0} m", kcal, meters);
        }

        /// <summary>右下角常驻 HUD：三行，本曲 / 本轮 / 总计（本次启动以来，换轮不归零）。</summary>
        public static string Hud(double trackKcal, double trackMeters,
                                 double sessionKcal, double sessionMeters,
                                 double totalKcal, double totalMeters)
        {
            if (Config.Current.Ascii)
                return string.Format("This track {0:0.0} kcal - {1:0} m\nSession {2:0.0} kcal - {3:0} m\nTotal {4:0.0} kcal - {5:0} m",
                                     trackKcal, trackMeters, sessionKcal, sessionMeters, totalKcal, totalMeters);
            return string.Format("本曲 {0:0.0} kcal · {1:0} m\n本轮 {2:0.0} kcal · {3:0} m\n总计 {4:0.0} kcal · {5:0} m",
                                 trackKcal, trackMeters, sessionKcal, sessionMeters, totalKcal, totalMeters);
        }
    }
}
