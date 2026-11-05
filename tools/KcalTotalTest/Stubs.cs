// 只为在宿主机上跑 Tracker 的累计逻辑写的桩；不参与 mod 构建。
using System;
using System.Reflection;

namespace UnityEngine
{
    public static class Time
    {
        public static float realtimeSinceStartup;
        public static float unscaledDeltaTime;
        public static float unscaledTime;
    }

    public class Object { }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform;
    }

    public class Transform : Component { }

    public class GameObject : Object
    {
        public UnityEngine.SceneManagement.Scene scene;
    }

    public class MonoBehaviour : Component { }

    public static class Resources
    {
        public static Object[] FindObjectsOfTypeAll(Type t) { return new Object[0]; }
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public bool IsValid() { return false; }
    }
}

namespace Manager
{
    public class UserData
    {
        public bool IsEntry;
    }

    public class UserDataManager
    {
        public static UserDataManager Instance;
        readonly UserData[] _users = { new UserData(), new UserData() };
        public UserData GetUserData(int i) { return _users[i]; }
    }

    public static class InputManager
    {
        public enum TouchPanelArea { A1 }
        public static bool GetTouchPanelAreaPush(int player, TouchPanelArea area) { return false; }
    }
}

namespace HarmonyLib
{
    public static class AccessTools
    {
        public static Type TypeByName(string name) { return null; }
        public static MethodInfo Method(Type t, string name) { return null; }
    }
}

namespace MaimaiKcal
{
    public class Cfg
    {
        public bool Enabled = true;
        public bool Debug = false;
        public int Players = -1;
        public double Alpha = 1.0e-3;
        public double WeightKg = 70.0;
        public bool Ascii = false;
    }

    public static class Config { public static Cfg Current = new Cfg(); }

    public static class Energy
    {
        public static double Kcal(double meters) { return Config.Current.Alpha * Config.Current.WeightKg * meters; }
    }

    public static class Geo
    {
        public static readonly double[] BtnX = new double[8];
        public static readonly double[] BtnZ = new double[8];
        public static readonly double[] ZoneX = new double[34];
        public static readonly double[] ZoneZ = new double[34];
        public static double Dist(double x1, double z1, double x2, double z2) { return 0.0; }
    }

    public static class Diag
    {
        public static void Msg(string s) { }
        public static void Warn(string s) { }
        public static void Error(string s) { }
        public static void Error(string s, Exception e) { }
        public static void ErrorOnce(string key, Exception e) { }
    }
}
