#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Stage 9.2 — build Resources/AfkIsland/Settlement prefabs (hut / path / clearing).
    /// Polyeler-first palette (wood + terracotta). No Save / SettlementSimulation changes.
    /// </summary>
    public static class Stage92SettlementKitBuilder
    {
        private const string SettlementFolder = "Assets/_Project/Resources/AfkIsland/Settlement";
        private const string MatFolder = "Assets/_Project/Materials/Settlement";
        private static readonly string ResultPath =
            Path.Combine("Builds", "Android", "review", "stage9", "stage92_settlement_kit_result.txt");

        [MenuItem("AFK Island/Stage 9/Apply Settlement Kit (9.2)")]
        public static void ApplyMenu()
        {
            var code = ApplyInternal();
            if (code != 0)
            {
                throw new System.Exception("Stage 9.2 Settlement Kit failed with code " + code);
            }

            EditorUtility.DisplayDialog("AFK Island", "Stage 9.2 Settlement Kit applied.", "OK");
        }

        public static void ApplyBatch()
        {
            Debug.Log("[AFK Island] Stage 9.2 Settlement Kit START");
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
                Debug.LogError("[AFK Island] Failed to write Stage 9.2 result: " + e.Message);
            }

            EditorApplication.Exit(code);
        }

        public static void ApplyMenuSafe() => ApplyInternal();

        private static int ApplyInternal()
        {
            try
            {
                EnsureFolder("Assets/_Project/Resources", "AfkIsland");
                EnsureFolder("Assets/_Project/Resources/AfkIsland", "Settlement");
                EnsureFolder("Assets/_Project", "Materials");
                EnsureFolder("Assets/_Project/Materials", "Settlement");

                var sand = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Assets/_Project/Resources/AfkIsland/Terrain/SandColor.jpg");
                var soil = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Assets/_Project/Resources/AfkIsland/Terrain/SoilColor.jpg");

                var pathMat = MakeMat("Settle_Path", new Color(0.82f, 0.68f, 0.45f), sand, 0.08f);
                var pathRimMat = MakeMat("Settle_PathRim", new Color(0.70f, 0.58f, 0.38f), sand, 0.08f);
                var clearingMat = MakeMat("Settle_Clearing", new Color(0.48f, 0.38f, 0.26f), soil, 0.1f);
                var bodyMat = MakeMat("Settle_HutBody", new Color(0.62f, 0.42f, 0.26f), soil, 0.18f);
                var roofMat = MakeMat("Settle_HutRoof", new Color(0.62f, 0.28f, 0.18f), null, 0.22f);
                var doorMat = MakeMat("Settle_HutDoor", new Color(0.22f, 0.14f, 0.1f), null, 0.15f);
                var trimMat = MakeMat("Settle_HutTrim", new Color(0.38f, 0.26f, 0.16f), null, 0.12f);

                WritePrefab(SettlementFolder + "/Path_Tile.prefab", BuildPathTile(pathMat, pathRimMat));
                WritePrefab(SettlementFolder + "/Clearing_Pad.prefab", BuildClearingPad(clearingMat));
                WritePrefab(SettlementFolder + "/Hut_A.prefab",
                    BuildHut("Hut_A", bodyMat, roofMat, doorMat, trimMat, wide: false));
                WritePrefab(SettlementFolder + "/Hut_B.prefab",
                    BuildHut("Hut_B", bodyMat, roofMat, doorMat, trimMat, wide: true));

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                var required = new[]
                {
                    "AfkIsland/Settlement/Path_Tile",
                    "AfkIsland/Settlement/Clearing_Pad",
                    "AfkIsland/Settlement/Hut_A",
                    "AfkIsland/Settlement/Hut_B"
                };
                var missing = 0;
                for (var i = 0; i < required.Length; i++)
                {
                    if (Resources.Load<GameObject>(required[i]) == null)
                    {
                        Debug.LogError("[AFK Island] Resources.Load failed: " + required[i]);
                        missing++;
                    }
                }

                if (missing > 0)
                {
                    return 2;
                }

                Debug.Log("[AFK Island] Stage 9.2 Settlement Kit OK — Path/Clearing/Hut_A/Hut_B ready.");
                return 0;
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AFK Island] Stage 9.2 Settlement Kit exception: " + ex);
                return 1;
            }
        }

        private static GameObject BuildPathTile(Material mat, Material rimMat)
        {
            var root = new GameObject("Path_Tile");
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = "Pad";
            pad.transform.SetParent(root.transform, false);
            pad.transform.localPosition = new Vector3(0f, 0.04f, 0f);
            pad.transform.localScale = new Vector3(1.12f, 0.08f, 1.12f);
            ApplyMat(pad, mat);
            StripCollider(pad);
            // Soft edge lip so roads read against grass (rim must be a saved asset mat).
            var rim = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rim.name = "Rim";
            rim.transform.SetParent(root.transform, false);
            rim.transform.localPosition = new Vector3(0f, 0.015f, 0f);
            rim.transform.localScale = new Vector3(1.22f, 0.03f, 1.22f);
            ApplyMat(rim, rimMat);
            StripCollider(rim);
            return root;
        }

        private static GameObject BuildClearingPad(Material mat)
        {
            var root = new GameObject("Clearing_Pad");
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = "Pad";
            pad.transform.SetParent(root.transform, false);
            pad.transform.localPosition = new Vector3(0f, 0.02f, 0f);
            pad.transform.localScale = new Vector3(0.92f, 0.05f, 0.92f);
            ApplyMat(pad, mat);
            StripCollider(pad);
            return root;
        }

        private static GameObject BuildHut(
            string name,
            Material bodyMat,
            Material roofMat,
            Material doorMat,
            Material trimMat,
            bool wide)
        {
            var root = new GameObject(name);
            var bodyW = wide ? 1.55f : 1.35f;
            var bodyD = wide ? 1.25f : 1.15f;

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.62f, 0f);
            body.transform.localScale = new Vector3(bodyW, 1.2f, bodyD);
            ApplyMat(body, bodyMat);
            StripCollider(body);

            // Gable roof (two slabs) — readable silhouette vs cube diamond.
            var pitch = wide ? 28f : 34f;
            var roofY = 1.28f;
            var roofLen = bodyD + 0.28f;
            var roofW = bodyW * 0.62f;
            AddRoofSlab(root.transform, "Roof_L", new Vector3(-bodyW * 0.28f, roofY, 0f),
                new Vector3(0f, 0f, pitch), new Vector3(roofW, 0.1f, roofLen), roofMat);
            AddRoofSlab(root.transform, "Roof_R", new Vector3(bodyW * 0.28f, roofY, 0f),
                new Vector3(0f, 0f, -pitch), new Vector3(roofW, 0.1f, roofLen), roofMat);

            var ridge = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ridge.name = "Ridge";
            ridge.transform.SetParent(root.transform, false);
            ridge.transform.localPosition = new Vector3(0f, roofY + 0.22f, 0f);
            ridge.transform.localScale = new Vector3(0.12f, 0.1f, roofLen * 0.98f);
            ApplyMat(ridge, trimMat);
            StripCollider(ridge);

            var door = GameObject.CreatePrimitive(PrimitiveType.Cube);
            door.name = "Door";
            door.transform.SetParent(root.transform, false);
            door.transform.localPosition = new Vector3(0f, 0.42f, bodyD * 0.52f);
            door.transform.localScale = new Vector3(0.38f, 0.72f, 0.08f);
            ApplyMat(door, doorMat);
            StripCollider(door);

            // Small porch posts so huts feel less like crates.
            AddPost(root.transform, "Post_L", new Vector3(-0.28f, 0.28f, bodyD * 0.55f), trimMat);
            AddPost(root.transform, "Post_R", new Vector3(0.28f, 0.28f, bodyD * 0.55f), trimMat);
            return root;
        }

        private static void AddRoofSlab(
            Transform parent, string name, Vector3 pos, Vector3 euler, Vector3 scale, Material mat)
        {
            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
            roof.name = name;
            roof.transform.SetParent(parent, false);
            roof.transform.localPosition = pos;
            roof.transform.localRotation = Quaternion.Euler(euler);
            roof.transform.localScale = scale;
            ApplyMat(roof, mat);
            StripCollider(roof);
        }

        private static void AddPost(Transform parent, string name, Vector3 pos, Material mat)
        {
            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = name;
            post.transform.SetParent(parent, false);
            post.transform.localPosition = pos;
            post.transform.localScale = new Vector3(0.08f, 0.55f, 0.08f);
            ApplyMat(post, mat);
            StripCollider(post);
        }

        private static Material MakeMat(string name, Color color, Texture2D tex, float smoothness)
        {
            var path = MatFolder + "/" + name + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                         ?? Shader.Find("Standard");
            var mat = existing != null ? existing : new Material(shader);
            mat.name = name;
            mat.shader = shader;
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }

            if (mat.HasProperty("_Color"))
            {
                mat.color = color;
            }

            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap"))
                {
                    mat.SetTexture("_BaseMap", tex);
                }

                mat.mainTexture = tex;
            }

            if (mat.HasProperty("_Smoothness"))
            {
                mat.SetFloat("_Smoothness", smoothness);
            }

            if (existing == null)
            {
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                EditorUtility.SetDirty(mat);
            }

            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        private static void WritePrefab(string path, GameObject root)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
        }

        private static void ApplyMat(GameObject go, Material mat)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null || mat == null)
            {
                return;
            }

            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
        }

        private static void StripCollider(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                Object.DestroyImmediate(col);
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
