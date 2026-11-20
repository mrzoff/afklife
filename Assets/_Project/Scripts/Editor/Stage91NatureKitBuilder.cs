#if UNITY_EDITOR
using System.IO;
using AfkIsland.Plants;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Stage 9.1 — copy Polyeler Essential Nature Pack into Resources/AfkIsland/Decor
    /// and rewire PlantData growth stage prefabs. No Save v8 / SettlementSimulation changes.
    /// </summary>
    public static class Stage91NatureKitBuilder
    {
        private const string PolyelerPrefabs = "Assets/Polyeler/EssentialNaturePack/Prefabs";
        private const string DecorFolder = "Assets/_Project/Resources/AfkIsland/Decor";
        private const string PlantFolder = "Assets/_Project/Prefabs/Plants";
        private const string PlantDataPath = "Assets/_Project/ScriptableObjects/Plants/PlantData_StarterTree.asset";
        private static readonly string ResultPath =
            Path.Combine("Builds", "Android", "review", "stage9", "stage91_nature_kit_result.txt");

        private static readonly string[] DecorNames =
        {
            "Rock_A", "Rock_B", "Rock_C", "Rock_D",
            "Mugwort", "ViolaGrypoceras", "JapaneseEurya",
            "JapaneseLaurel", "LittleBamboo", "Bamboo",
            "JapaneseCedar", "Pine", "SawtoothOak", "JapaneseCypress"
        };

        [MenuItem("AFK Island/Stage 9/Apply Nature Kit (9.1)")]
        public static void ApplyMenu()
        {
            var code = ApplyInternal();
            if (code != 0)
            {
                throw new System.Exception("Stage 9.1 Nature Kit failed with code " + code);
            }

            EditorUtility.DisplayDialog("AFK Island", "Stage 9.1 Nature Kit applied.", "OK");
        }

        /// <summary>Silent apply for other setup menus (no dialog).</summary>
        public static void ApplyMenuSafe()
        {
            ApplyInternal();
        }

        public static void ApplyBatch()
        {
            Debug.Log("[AFK Island] Stage 9.1 Nature Kit START");
            var code = ApplyInternal();
            try
            {
                var dir = Path.GetDirectoryName(ResultPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(ResultPath, code == 0 ? "OK" : ("FAIL:" + code));
            }
            catch (System.Exception e)
            {
                Debug.LogError("[AFK Island] Failed to write Stage 9.1 result: " + e.Message);
            }

            EditorApplication.Exit(code);
        }

        private static int ApplyInternal()
        {
            try
            {
                EnsureFolder("Assets/_Project/Resources", "AfkIsland");
                EnsureFolder("Assets/_Project/Resources/AfkIsland", "Decor");
                EnsureFolder("Assets/_Project/Prefabs", "Plants");

                var copied = 0;
                for (var i = 0; i < DecorNames.Length; i++)
                {
                    if (CopyDecor(DecorNames[i]))
                    {
                        copied++;
                    }
                }

                if (copied == 0)
                {
                    Debug.LogError("[AFK Island] No decor prefabs copied — Polyeler pack missing?");
                    return 2;
                }

                var seed = WriteScaledPlantPrefab(
                    PlantFolder + "/Stage_Seed.prefab",
                    PolyelerPrefabs + "/ViolaGrypoceras.prefab",
                    0.14f);
                var sprout = WriteScaledPlantPrefab(
                    PlantFolder + "/Stage_Sprout.prefab",
                    PolyelerPrefabs + "/Mugwort.prefab",
                    0.22f);
                var young = WriteScaledPlantPrefab(
                    PlantFolder + "/Stage_Young.prefab",
                    PolyelerPrefabs + "/JapaneseCedar.prefab",
                    0.28f);
                var adult = WriteScaledPlantPrefab(
                    PlantFolder + "/Stage_Adult.prefab",
                    PolyelerPrefabs + "/SawtoothOak.prefab",
                    0.48f);

                if (seed == null || sprout == null || young == null || adult == null)
                {
                    Debug.LogError("[AFK Island] Plant stage prefab rebuild incomplete.");
                    return 3;
                }

                var plantData = AssetDatabase.LoadAssetAtPath<PlantData>(PlantDataPath);
                if (plantData == null)
                {
                    Debug.LogError("[AFK Island] PlantData_StarterTree missing at " + PlantDataPath);
                    return 4;
                }

                plantData.growthStagePrefabs = new[] { seed, sprout, young, adult };
                EditorUtility.SetDirty(plantData);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // Verify Resources.Load paths used at runtime.
                var missing = 0;
                for (var i = 0; i < DecorNames.Length; i++)
                {
                    var path = "AfkIsland/Decor/" + DecorNames[i];
                    if (Resources.Load<GameObject>(path) == null)
                    {
                        Debug.LogError("[AFK Island] Resources.Load failed: " + path);
                        missing++;
                    }
                }

                if (missing > 0)
                {
                    return 5;
                }

                Debug.Log(
                    $"[AFK Island] Stage 9.1 Nature Kit OK — decor={copied}, plants rewired (Seed/Sprout/Young/Adult).");
                return 0;
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AFK Island] Stage 9.1 Nature Kit exception: " + ex);
                return 1;
            }
        }

        private static bool CopyDecor(string name)
        {
            var src = $"{PolyelerPrefabs}/{name}.prefab";
            var dst = $"{DecorFolder}/{name}.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(src) == null)
            {
                Debug.LogWarning("[AFK Island] Polyeler prefab missing: " + src);
                return false;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(dst) != null)
            {
                AssetDatabase.DeleteAsset(dst);
            }

            if (!AssetDatabase.CopyAsset(src, dst))
            {
                Debug.LogError("[AFK Island] CopyAsset failed: " + src + " -> " + dst);
                return false;
            }

            return true;
        }

        private static GameObject WriteScaledPlantPrefab(string path, string sourcePrefabPath, float scale)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
            if (source == null)
            {
                Debug.LogError("[AFK Island] Plant source missing: " + sourcePrefabPath);
                return null;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            instance.name = Path.GetFileNameWithoutExtension(path);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one * scale;
            StripColliders(instance);
            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
            Object.DestroyImmediate(instance);
            return prefab;
        }

        private static void StripColliders(GameObject root)
        {
            var cols = root.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < cols.Length; i++)
            {
                Object.DestroyImmediate(cols[i]);
            }
        }

        private static void EnsureFolder(string parent, string child)
        {
            var path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }
    }
}
#endif
