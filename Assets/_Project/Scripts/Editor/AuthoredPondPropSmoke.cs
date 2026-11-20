#if UNITY_EDITOR
using System;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// The authored pine used to stand at (2.4, -1.5), inside the home pond.
    /// PushTrunkOutOfWater must move that point out and leave the yard cedar alone.
    /// </summary>
    public static class AuthoredPondPropSmoke
    {
        [MenuItem("AFK Island/Smoke Authored Pond Props")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Authored pond prop smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Authored pond prop smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            const float radius = 0.35f;
            if (!WaterSourceLayout.BlocksTrunk(2.4f, -1.5f, radius))
            {
                Debug.LogError("Authored pond smoke: pine fixture is not inside the water.");
                return 2;
            }

            var pushed = WaterSourceLayout.PushTrunkOutOfWater(2.4f, -1.5f, radius);
            if (WaterSourceLayout.BlocksTrunk(pushed.x, pushed.y, radius))
            {
                Debug.LogError(
                    $"Authored pond smoke: pushed pine still in the water at ({pushed.x:0.00},{pushed.y:0.00}).");
                return 3;
            }

            var bank = WaterSourceLayout.PushTrunkOutOfWater(3.4f, -1.5f, radius);
            if (Mathf.Abs(bank.x - 3.4f) > 0.001f || Mathf.Abs(bank.y - (-1.5f)) > 0.001f)
            {
                Debug.LogError("Authored pond smoke: bank pine was moved.");
                return 4;
            }

            var yard = WaterSourceLayout.PushTrunkOutOfWater(-2.4f, -0.4f, WaterSourceLayout.TrunkRadius(0.55f));
            if (Mathf.Abs(yard.x - (-2.4f)) > 0.001f || Mathf.Abs(yard.y - (-0.4f)) > 0.001f)
            {
                Debug.LogError("Authored pond smoke: yard cedar was moved.");
                return 5;
            }

            Debug.Log($"[AFK Island] Authored pine pushed to ({pushed.x:0.00},{pushed.y:0.00}).");
            return 0;
        }
    }
}
#endif
