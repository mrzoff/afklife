#if UNITY_EDITOR
using System;
using System.IO;
using AfkIsland.Input;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>CIF-B1: tap-vs-drag + UI helper invariants.</summary>
    public static class WorldPointerGestureSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "world_pointer_gesture_smoke_result.txt");

        [MenuItem("AFK Island/Smoke CIF-B1 World Pointer Gesture")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("CIF-B1 WorldPointerGesture smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] CIF-B1 WorldPointerGesture smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            try
            {
                var state = new WorldPointerGesture.PressState();
                WorldPointerGesture.BeginPress(ref state, Vector2.zero, -1);
                if (!state.Active)
                {
                    // No EventSystem → BeginPress may still activate (IsPointerOverUi false).
                    // If it failed for another reason, treat as error only when EventSystem blocks.
                }

                state = default;
                WorldPointerGesture.BeginPress(ref state, new Vector2(10f, 10f), -1);
                if (!state.Active)
                {
                    Debug.LogError("B1: BeginPress should activate without EventSystem UI block.");
                    WriteResult(2);
                    return 2;
                }

                WorldPointerGesture.TrackMove(ref state, new Vector2(10f + WorldPointerGesture.DefaultMaxTapMovePixels + 5f, 10f));
                if (!state.CancelledAsDrag)
                {
                    Debug.LogError("B1: TrackMove beyond threshold must cancel as drag.");
                    WriteResult(3);
                    return 3;
                }

                if (WorldPointerGesture.TryCompleteTap(ref state, new Vector2(50f, 10f), out _))
                {
                    Debug.LogError("B1: drag press must not complete as tap.");
                    WriteResult(4);
                    return 4;
                }

                state = default;
                WorldPointerGesture.BeginPress(ref state, new Vector2(100f, 100f), -1);
                if (!WorldPointerGesture.TryCompleteTap(
                        ref state,
                        new Vector2(105f, 102f),
                        out var tapPos,
                        WorldPointerGesture.DefaultMaxTapMovePixels,
                        WorldPointerGesture.DefaultMaxTapSeconds))
                {
                    Debug.LogError("B1: short small move must complete as tap.");
                    WriteResult(5);
                    return 5;
                }

                if (Vector2.Distance(tapPos, new Vector2(105f, 102f)) > 0.01f)
                {
                    Debug.LogError("B1: tap screen pos mismatch.");
                    WriteResult(6);
                    return 6;
                }

                WriteResult(0);
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] WorldPointerGesture smoke exception: " + e);
                WriteResult(1);
                return 1;
            }
        }

        private static void WriteResult(int code)
        {
            try
            {
                var dir = Path.GetDirectoryName(ResultPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(ResultPath, code == 0 ? "PASS\n" : $"FAIL {code}\n");
            }
            catch
            {
                // ignore IO in smoke
            }
        }
    }
}
#endif
