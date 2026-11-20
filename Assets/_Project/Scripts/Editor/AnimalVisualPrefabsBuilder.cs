#if UNITY_EDITOR
using System.IO;
using AfkIsland.Animals;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Builds animal prefabs from External low-poly meshes and wires AnimalData.prefab.
    /// Does not change FaunaSimulation / save format.
    /// </summary>
    public static class AnimalVisualPrefabsBuilder
    {
        private const string ArtRoot = "Assets/_Project/Art/Animals";
        private const string PrefabFolder = ArtRoot + "/Prefabs";
        private const string MatFolder = ArtRoot + "/Materials";
        private const string ExternalRoot = ArtRoot + "/External";
        private const string ResourcesAnimals = "Assets/_Project/Resources/Animals";
        private const string SoAnimals = "Assets/_Project/ScriptableObjects/Animals";

        [MenuItem("AFK Island/Build Animal Visual Prefabs (External)")]
        public static void BuildMenu()
        {
            BuildAll();
            EditorUtility.DisplayDialog("AFK Island", "External animal prefabs built and AnimalData wired.", "OK");
        }

        public static void BuildBatch()
        {
            try
            {
                BuildAll();
                EditorApplication.Exit(0);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AFK Island] Animal visual prefab build failed: " + ex);
                EditorApplication.Exit(1);
            }
        }

        public static void BuildAll()
        {
            EnsureFolders();
            AssetDatabase.Refresh();
            WriteAttribution();

            var butterfly = BuildButterfly();
            var bird = BuildBird();
            var frog = BuildFrog();

            WireAnimalData("butterfly", butterfly, Vector3.one);
            WireAnimalData("bird", bird, Vector3.one);
            WireAnimalData("frog", frog, Vector3.one);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[AFK Island] External animal visual prefabs ready under " + PrefabFolder);
        }

        private static void EnsureFolders()
        {
            EnsureFolder("Assets/_Project", "Art");
            EnsureFolder("Assets/_Project/Art", "Animals");
            EnsureFolder(ArtRoot, "Prefabs");
            EnsureFolder(ArtRoot, "Materials");
            EnsureFolder(ArtRoot, "External");
        }

        private static void EnsureFolder(string parent, string child)
        {
            var path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }

        private static void WriteAttribution()
        {
            var path = ArtRoot + "/ATTRIBUTION.txt";
            var text =
                "AFK Island animal visuals — external models\n" +
                "===========================================\n" +
                "bird: Quaternius Animals Pack (CC0) — Bird_Quaternius.obj/.fbx\n" +
                "frog: Drummyfish 3D Frog via OpenGameArt (CC0) — Frog_Drummyfish.obj/.png\n" +
                "butterfly: Butterfly_AFK.obj project-authored low-poly (Body/Wing_L/Wing_R)\n" +
                "          + Butterfly_WingPlane_OGA.* Sara / OpenGameArt Butterfly (CC-BY)\n" +
                "Purpose: replace procedural P0 primitives. FaunaSimulation unchanged.\n";
            File.WriteAllText(Path.GetFullPath(path), text);
            AssetDatabase.ImportAsset(path);
        }

        private static GameObject BuildButterfly()
        {
            var root = new GameObject("Animal_Butterfly");
            var visual = new GameObject("Visual");
            visual.transform.SetParent(root.transform, false);

            var model = InstantiateExternalModel(
                ExternalRoot + "/butterfly/Butterfly_AFK.obj",
                visual.transform,
                Vector3.one * 0.85f,
                Quaternion.identity);

            TintRenderers(model, new Color(0.95f, 0.55f, 0.18f), preferName: "Wing");
            TintRenderers(model, new Color(0.25f, 0.18f, 0.12f), preferName: "Body");

            var wingL = FindDeep(visual.transform, "Wing_L");
            var wingR = FindDeep(visual.transform, "Wing_R");
            // If importer collapsed groups, synthesize wing pivots for flap motion.
            if (wingL == null || wingR == null)
            {
                wingL = CreateFlapWing(visual.transform, "Wing_L", new Vector3(-0.12f, 0.04f, 0f), true);
                wingR = CreateFlapWing(visual.transform, "Wing_R", new Vector3(0.12f, 0.04f, 0f), false);
            }

            var motion = root.AddComponent<AnimalVisualMotion>();
            motion.Configure(
                AnimalVisualMotion.MotionKind.Butterfly,
                visual.transform,
                wingL,
                wingR);
            root.AddComponent<AnimalInstance>();
            return SavePrefab(root, "Animal_Butterfly.prefab");
        }

        private static GameObject BuildBird()
        {
            var root = new GameObject("Animal_Bird");
            var visual = new GameObject("Visual");
            visual.transform.SetParent(root.transform, false);

            // Quaternius bird faces +Z; ~25% smaller so it doesn't dominate huts/trees.
            var model = InstantiateExternalModel(
                ExternalRoot + "/bird/Bird_Quaternius.obj",
                visual.transform,
                Vector3.one * 0.34f,
                Quaternion.Euler(0f, 180f, 0f));
            if (model == null)
            {
                model = InstantiateExternalModel(
                    ExternalRoot + "/bird/Bird_Quaternius.fbx",
                    visual.transform,
                    Vector3.one * 0.26f,
                    Quaternion.Euler(0f, 180f, 0f));
            }

            // Muted natural palette — soft tint keeps albedo maps when present.
            SoftTintRenderers(model, new Color(0.55f, 0.48f, 0.38f));
            SoftTintRenderers(model, new Color(0.42f, 0.46f, 0.34f), preferName: "Wing");
            SoftTintRenderers(model, new Color(0.62f, 0.55f, 0.44f), preferName: "Body");
            SoftTintRenderers(model, new Color(0.48f, 0.42f, 0.36f), preferName: "Bird");

            // Quaternius bird is a single mesh — bob/tilt via AnimalVisualMotion without fake wing cubes.
            var wingL = FindDeep(visual.transform, "Wing_L") ?? FindDeep(visual.transform, "wing_l");
            var wingR = FindDeep(visual.transform, "Wing_R") ?? FindDeep(visual.transform, "wing_r");

            var motion = root.AddComponent<AnimalVisualMotion>();
            motion.Configure(
                AnimalVisualMotion.MotionKind.Bird,
                visual.transform,
                wingL,
                wingR);
            root.AddComponent<AnimalInstance>();
            return SavePrefab(root, "Animal_Bird.prefab");
        }

        private static GameObject BuildFrog()
        {
            var root = new GameObject("Animal_Frog");
            var visual = new GameObject("Visual");
            visual.transform.SetParent(root.transform, false);

            // Drummyfish frog is authored around Y≈1.5; scale down and sit on ground.
            var model = InstantiateExternalModel(
                ExternalRoot + "/frog/Frog_Drummyfish.obj",
                visual.transform,
                Vector3.one * 0.14f,
                Quaternion.identity);
            if (model != null)
            {
                RecenterOnGround(model);
                ApplyFrogTexture(model);
            }
            else
            {
                TintRenderers(null, new Color(0.28f, 0.72f, 0.32f));
            }

            var motion = root.AddComponent<AnimalVisualMotion>();
            motion.Configure(AnimalVisualMotion.MotionKind.Frog, visual.transform);
            root.AddComponent<AnimalInstance>();
            return SavePrefab(root, "Animal_Frog.prefab");
        }

        private static GameObject InstantiateExternalModel(
            string assetPath,
            Transform parent,
            Vector3 localScale,
            Quaternion localRot)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (source == null)
            {
                Debug.LogWarning("[AFK Island] External model missing: " + assetPath);
                return null;
            }

            var instance = Object.Instantiate(source, parent);
            instance.name = Path.GetFileNameWithoutExtension(assetPath);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = localRot;
            instance.transform.localScale = localScale;
            StripColliders(instance);
            return instance;
        }

        private static void StripColliders(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            foreach (var c in go.GetComponentsInChildren<Collider>(true))
            {
                Object.DestroyImmediate(c);
            }
        }

        private static void RecenterOnGround(GameObject model)
        {
            if (model == null)
            {
                return;
            }

            var renders = model.GetComponentsInChildren<Renderer>();
            if (renders.Length == 0)
            {
                return;
            }

            var bounds = renders[0].bounds;
            for (var i = 1; i < renders.Length; i++)
            {
                bounds.Encapsulate(renders[i].bounds);
            }

            var offset = model.transform.position - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            model.transform.position += offset;
        }

        private static void ApplyFrogTexture(GameObject model)
        {
            if (model == null)
            {
                return;
            }

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(ExternalRoot + "/frog/Frog_Drummyfish.png");
            var mat = GetOrCreateMaterial("Frog_External", new Color(0.35f, 0.75f, 0.3f), tex);
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                r.sharedMaterial = mat;
            }
        }

        private static void TintRenderers(GameObject model, Color color, string preferName = null)
        {
            if (model == null)
            {
                return;
            }

            var mat = GetOrCreateMaterial(
                (preferName ?? model.name) + "_" + ColorUtility.ToHtmlStringRGB(color),
                color);
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (!string.IsNullOrEmpty(preferName) &&
                    r.name.IndexOf(preferName, System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    r.transform.name.IndexOf(preferName, System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                r.sharedMaterial = mat;
            }
        }

        /// <summary>
        /// Soft color multiply: keeps existing albedo textures when present instead of flat replace.
        /// </summary>
        private static void SoftTintRenderers(GameObject model, Color tint, string preferName = null)
        {
            if (model == null)
            {
                return;
            }

            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (!string.IsNullOrEmpty(preferName) &&
                    r.name.IndexOf(preferName, System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    r.transform.name.IndexOf(preferName, System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var shared = r.sharedMaterials;
                if (shared == null || shared.Length == 0)
                {
                    r.sharedMaterial = GetOrCreateMaterial(
                        (preferName ?? model.name) + "_" + ColorUtility.ToHtmlStringRGB(tint),
                        tint);
                    continue;
                }

                var mats = new Material[shared.Length];
                for (var i = 0; i < shared.Length; i++)
                {
                    var src = shared[i];
                    Texture tex = null;
                    var baseCol = Color.white;
                    if (src != null)
                    {
                        if (src.HasProperty("_BaseMap"))
                        {
                            tex = src.GetTexture("_BaseMap");
                        }
                        else if (src.HasProperty("_MainTex"))
                        {
                            tex = src.GetTexture("_MainTex");
                        }

                        if (src.HasProperty("_BaseColor"))
                        {
                            baseCol = src.GetColor("_BaseColor");
                        }
                        else if (src.HasProperty("_Color"))
                        {
                            baseCol = src.GetColor("_Color");
                        }
                    }

                    var mixed = new Color(
                        Mathf.Clamp01(baseCol.r * tint.r),
                        Mathf.Clamp01(baseCol.g * tint.g),
                        Mathf.Clamp01(baseCol.b * tint.b),
                        baseCol.a);
                    var name =
                        (preferName ?? model.name) + "_soft_" + ColorUtility.ToHtmlStringRGB(mixed) +
                        (tex != null ? "_" + tex.name : string.Empty);
                    mats[i] = GetOrCreateMaterial(name, mixed, tex as Texture2D);
                }

                r.sharedMaterials = mats;
            }
        }

        private static Transform CreateFlapWing(Transform parent, string name, Vector3 localPos, bool left)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(0.14f, 0.02f, 0.1f);
            go.transform.localRotation = Quaternion.Euler(0f, 0f, left ? 16f : -16f);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            var color = new Color(0.95f, 0.55f, 0.18f);
            if (parent.root.name.IndexOf("Bird", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                parent.name.IndexOf("Bird", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                parent.parent != null && parent.parent.name.IndexOf("Bird", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                color = new Color(0.5f, 0.44f, 0.36f);
            }

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = GetOrCreateMaterial(name + "_" + ColorUtility.ToHtmlStringRGB(color), color);
            }

            return go.transform;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }

            if (root.name == name)
            {
                return root;
            }

            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static Material GetOrCreateMaterial(string name, Color color, Texture2D tex = null)
        {
            var path = $"{MatFolder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                ApplyMat(existing, color, tex);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = name };
            ApplyMat(mat, color, tex);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        private static void ApplyMat(Material mat, Color color, Texture2D tex)
        {
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }
            else
            {
                mat.color = color;
            }

            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap"))
                {
                    mat.SetTexture("_BaseMap", tex);
                }
                else
                {
                    mat.mainTexture = tex;
                }
            }
        }

        private static GameObject SavePrefab(GameObject root, string fileName)
        {
            var path = PrefabFolder + "/" + fileName;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static void WireAnimalData(string animalId, GameObject prefab, Vector3 visualScale)
        {
            WireOne($"{ResourcesAnimals}/AnimalData_{animalId}.asset", prefab, visualScale);
            WireOne($"{SoAnimals}/AnimalData_{animalId}.asset", prefab, visualScale);
        }

        private static void WireOne(string assetPath, GameObject prefab, Vector3 visualScale)
        {
            var data = AssetDatabase.LoadAssetAtPath<AnimalData>(assetPath);
            if (data == null)
            {
                Debug.LogWarning("[AFK Island] AnimalData missing: " + assetPath);
                return;
            }

            data.prefab = prefab;
            data.visualScale = visualScale;
            EditorUtility.SetDirty(data);
        }
    }
}
#endif
