#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AfkIsland.EditorTools
{
    public static class RealisticIslandDresser
    {
        private const string ScenePath = "Assets/_Project/Scenes/MainIsland.unity";

        [MenuItem("AFK Island/Dress Island With Poly Haven")]
        public static void DressMenu()
        {
            DressInternal();
        }

        public static void DressBatch()
        {
            try
            {
                DressInternal();
                EditorApplication.Exit(0);
            }
            catch (System.Exception ex)
            {
                Debug.LogError(ex);
                EditorApplication.Exit(1);
            }
        }

        private static void DressInternal()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var world = GameObject.Find("World");
            if (world == null)
            {
                throw new System.InvalidOperationException("World root not found.");
            }

            var island = world.transform.Find("Island");
            if (island == null)
            {
                throw new System.InvalidOperationException("Island not found.");
            }

            ApplyGroundMaterials(island);
            HidePrototypeProps(island);

            var props = island.Find("RealisticProps");
            if (props != null)
            {
                Object.DestroyImmediate(props.gameObject);
            }

            var root = new GameObject("RealisticProps");
            root.transform.SetParent(island, false);

            Place("Assets/ThirdParty/PolyHaven/tree_small_02/tree_small_02_1k.fbx", root.transform,
                new Vector3(-1.2f, 1.7f, 0.6f), new Vector3(0.35f, 0.35f, 0.35f), 20f);
            Place("Assets/ThirdParty/PolyHaven/boulder_01/boulder_01_1k.fbx", root.transform,
                new Vector3(3.2f, 1.55f, 1.4f), Vector3.one * 0.55f, 15f);
            Place("Assets/ThirdParty/PolyHaven/boulder_01/boulder_01_1k.fbx", root.transform,
                new Vector3(-3.6f, 1.45f, -1.2f), Vector3.one * 0.4f, 80f);
            Place("Assets/ThirdParty/PolyHaven/sand_rocks_small_01/sand_rocks_small_01_1k.fbx", root.transform,
                new Vector3(0.4f, 0.55f, 6.2f), Vector3.one * 0.25f, -10f);

            TryApplySky("Assets/ThirdParty/PolyHaven/HDRI/sky_1k.hdr");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[AFK Island] Realistic props placed on MainIsland.");
        }

        private static void Place(string assetPath, Transform parent, Vector3 localPos, Vector3 scale, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                Debug.LogWarning($"Missing model: {assetPath}");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = prefab.name;
            instance.transform.SetParent(parent, false);
            instance.transform.localPosition = localPos;
            instance.transform.localScale = scale;
            instance.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            EnsureUrpMaterials(instance);
        }

        private static void EnsureUrpMaterials(GameObject instance)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                return;
            }

            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    if (src != null && src.shader != null && src.shader.name.Contains("Universal Render Pipeline"))
                    {
                        continue;
                    }

                    var mat = new Material(shader);
                    if (src != null)
                    {
                        if (src.HasProperty("_MainTex") && src.GetTexture("_MainTex") != null)
                        {
                            mat.SetTexture("_BaseMap", src.GetTexture("_MainTex"));
                        }
                        else if (src.mainTexture != null)
                        {
                            mat.SetTexture("_BaseMap", src.mainTexture);
                        }
                    }

                    mats[i] = mat;
                }

                renderer.sharedMaterials = mats;
            }
        }

        private static void ApplyGroundMaterials(Transform island)
        {
            var grass = MakeLit(
                "Assets/ThirdParty/ambientCG/Grass004_1K-JPG/Grass004_1K-JPG_Color.jpg",
                "Assets/_Project/Art/Materials/Mat_Grass.mat",
                new Vector2(4f, 4f));
            var ground = MakeLit(
                "Assets/ThirdParty/ambientCG/Ground037_1K-JPG/Ground037_1K-JPG_Color.jpg",
                "Assets/_Project/Art/Materials/Mat_Ground.mat",
                new Vector2(3f, 3f));

            Assign(island, "SettlementYard", grass);
            Assign(island, "CentralMeadow", grass); // legacy scenes
            Assign(island, "MidPlateau", grass);
            Assign(island, "BaseRock", ground);
            Assign(island, "ShoreSand_N", ground);
            Assign(island, "ShoreSand_S", ground);
        }

        private static void Assign(Transform island, string childName, Material material)
        {
            if (material == null)
            {
                return;
            }

            var child = island.Find(childName);
            if (child == null)
            {
                return;
            }

            var renderer = child.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
            }
        }

        private static Material MakeLit(string texturePath, string materialPath, Vector2 tiling)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (tex == null)
            {
                Debug.LogWarning($"Missing texture: {texturePath}");
                return null;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, materialPath);
            }

            mat.shader = shader;
            mat.SetTexture("_BaseMap", tex);
            mat.SetTextureScale("_BaseMap", tiling);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void HidePrototypeProps(Transform island)
        {
            foreach (Transform child in island)
            {
                var name = child.name;
                if (name.StartsWith("Grass_") || name.StartsWith("Rock_") || name == "StarterTree")
                {
                    child.gameObject.SetActive(false);
                }

                // Keep Settlement authored props; dresser only swaps ground materials.
            }
        }

        private static void TryApplySky(string hdrPath)
        {
            var hdr = AssetDatabase.LoadAssetAtPath<Texture>(hdrPath);
            if (hdr == null)
            {
                return;
            }

            RenderSettings.skybox = null;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
            Debug.Log($"HDRI imported: {hdrPath}. Assign it in a Skybox material if the default sky stays flat.");
        }
    }
}
#endif
