#if UNITY_EDITOR
using System;
using AfkIsland.Input;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Wheel source: a real mouse delta wins, otherwise the Android axis value.
    /// </summary>
    public static class WheelScrollSmoke
    {
        [MenuItem("AFK Island/Smoke Wheel Scroll")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Wheel scroll smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Wheel scroll smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            if (!Mathf.Approximately(AndroidMouseScroll.Prefer(1.2f, 5f), 1.2f))
            {
                Debug.LogError("Wheel smoke: mouse delta was dropped");
                return 2;
            }

            if (!Mathf.Approximately(AndroidMouseScroll.Prefer(0f, -1f), -1f))
            {
                Debug.LogError("Wheel smoke: Android axis was dropped");
                return 3;
            }

            if (!Mathf.Approximately(AndroidMouseScroll.Prefer(0.001f, 2f), 2f))
            {
                Debug.LogError("Wheel smoke: a tiny mouse delta blocked the Android axis");
                return 4;
            }

            AndroidMouseScroll.AddNotches(1.5f);
            AndroidMouseScroll.AddNotches(-0.5f);
            var consumed = AndroidMouseScroll.ConsumeY();
            if (!Mathf.Approximately(consumed, 1f))
            {
                Debug.LogError("Wheel smoke: accumulated axis " + consumed);
                return 5;
            }

            if (Mathf.Abs(AndroidMouseScroll.ConsumeY()) > 0.0001f)
            {
                Debug.LogError("Wheel smoke: axis was not consumed");
                return 6;
            }

            Debug.Log("[AFK Island] Wheel scroll smoke OK.");
            return 0;
        }
    }
}
#endif
