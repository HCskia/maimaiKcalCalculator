// 里程（米）→ 卡路里。
// 模型刻意保持简单：kcal = alpha × 体重(kg) × 里程(m)。
namespace MaimaiKcal
{
    public static class Energy
    {
        public static double Kcal(double meters)
        {
            return Config.Current.Alpha * Config.Current.WeightKg * meters;
        }
    }
}
