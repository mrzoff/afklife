using System.Threading;
using UnityEngine;

namespace AfkIsland.Input
{
    /// <summary>
    /// BlueStacks delivers the wheel as Android AXIS_VSCROLL. The Input System mouse
    /// never sees that axis, and the legacy Input manager is off. This listener is the
    /// one scroll source for that axis. Editor and a real mouse still use Mouse.scroll.
    /// </summary>
    public static class AndroidMouseScroll
    {
        private const int AxisVscroll = 9;
        private const int AxisHscroll = 10;
        private static int _milli;
        private static bool _installed;
        private static AndroidJavaProxy _listener;
        private static AndroidJavaProxy _installer;

        public static void Install()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_installed)
            {
                return;
            }

            _installed = true;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                _installer = new Installer();
                activity.Call("runOnUiThread", _installer);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[AFK Island] Wheel listener was not installed: " + ex.Message);
            }
#endif
        }

        public static void AddNotches(float y)
        {
            if (Mathf.Abs(y) < 0.01f)
            {
                return;
            }

            Interlocked.Add(ref _milli, Mathf.RoundToInt(y * 1000f));
        }

        public static float ConsumeY()
        {
            return Interlocked.Exchange(ref _milli, 0) / 1000f;
        }

        /// <summary>Mouse wins when it actually moved. Otherwise the Android axis.</summary>
        public static float Prefer(float mouseY, float deviceY)
        {
            return Mathf.Abs(mouseY) >= 0.01f ? mouseY : deviceY;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private sealed class Installer : AndroidJavaProxy
        {
            public Installer()
                : base("java.lang.Runnable")
            {
            }

            public void run()
            {
                try
                {
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    using var window = activity.Call<AndroidJavaObject>("getWindow");
                    using var decor = window.Call<AndroidJavaObject>("getDecorView");
                    // android.R.id.content
                    using var content = decor.Call<AndroidJavaObject>("findViewById", 0x01020002);
                    var view = content != null ? content.Call<AndroidJavaObject>("getChildAt", 0) : null;
                    var target = view ?? decor;
                    _listener = new MotionListener();
                    target.Call("setOnGenericMotionListener", _listener);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning("[AFK Island] Wheel listener failed: " + ex.Message);
                }
            }
        }

        private sealed class MotionListener : AndroidJavaProxy
        {
            public MotionListener()
                : base("android.view.View$OnGenericMotionListener")
            {
            }

            public bool onGenericMotion(AndroidJavaObject view, AndroidJavaObject motion)
            {
                try
                {
                    var y = motion.Call<float>("getAxisValue", AxisVscroll);
                    if (Mathf.Abs(y) < 0.01f)
                    {
                        y = motion.Call<float>("getAxisValue", AxisHscroll);
                    }

                    AddNotches(y);
                }
                catch (System.Exception)
                {
                    // A non-scroll motion should not break the view.
                }

                return false;
            }
        }
#endif
    }
}
