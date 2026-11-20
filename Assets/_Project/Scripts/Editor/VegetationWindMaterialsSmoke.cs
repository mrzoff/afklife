#if UNITY_EDITOR
using System;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Trunk and crown slots on one renderer both receive VegetationWind.
    /// </summary>
    public static class VegetationWindMaterialsSmoke
    {
        [MenuItem("AFK Island/Smoke Vegetation Wind Materials")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Vegetation wind materials smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Vegetation wind materials smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            GameObject root = null;
            try
            {
                var lit = Shader.Find("Universal Render Pipeline/Lit");
                var wind = Shader.Find("AFK Island/VegetationWind");
                if (lit == null || wind == null)
                {
                    Debug.LogError("Wind material smoke: shader missing");
                    return 2;
                }

                root = new GameObject("WindSmokeTree");
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(root.transform, false);
                var renderer = cube.GetComponent<MeshRenderer>();
                var wood = new Material(lit) { name = "SmokeOak_wood" };
                var leaves = new Material(lit) { name = "SmokeOak_leaves" };
                if (leaves.HasProperty("_AlphaClip"))
                {
                    leaves.SetFloat("_AlphaClip", 1f);
                }

                renderer.sharedMaterials = new[] { wood, leaves };
                VegetationWindApplicator.ApplyToInstance(root, isRock: false, styleName: "SawtoothOak");

                var applied = renderer.sharedMaterials;
                if (applied == null || applied.Length != 2)
                {
                    Debug.LogError("Wind material smoke: expected 2 slots, got " + (applied?.Length ?? 0));
                    return 3;
                }

                if (applied[0] == null || applied[0].shader != wind)
                {
                    Debug.LogError("Wind material smoke: trunk slot shader " + applied[0]?.shader);
                    return 4;
                }

                if (applied[1] == null || applied[1].shader != wind)
                {
                    Debug.LogError("Wind material smoke: crown slot shader " + applied[1]?.shader);
                    return 5;
                }

                if (!applied[1].IsKeywordEnabled("_ALPHATEST_ON"))
                {
                    Debug.LogError("Wind material smoke: crown lost alpha clip");
                    return 6;
                }

                if (applied[0].IsKeywordEnabled("_ALPHATEST_ON"))
                {
                    Debug.LogError("Wind material smoke: trunk enabled alpha clip");
                    return 7;
                }

                Debug.Log("[AFK Island] Vegetation wind materials smoke OK.");
                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return 1;
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
        }
    }
}
#endif
