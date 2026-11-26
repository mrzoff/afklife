#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Cozy Diorama stage B, step 1: tree scale study.
    /// Measures the hut and the Polyeler trees, then builds three scale variants next to the hut
    /// in an isolated scene and renders review shots. Source prefabs, colliders, masks, coordinates,
    /// Save v8 and simulation are not touched. No scale is fixed here: the variants are candidates.
    /// </summary>
    public static class CozyDioramaTreeScaleStudy
    {
        public const string ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaTreeScaleStudy.unity";
        private const string DecorResources = "Assets/_Project/Resources/AfkIsland/Decor";
        private const string PolyelerPrefabs = "Assets/Polyeler/EssentialNaturePack/Prefabs";
        private const string SettlementResources = "Assets/_Project/Resources/AfkIsland/Settlement";
        private static readonly string OutDir = Path.Combine("Builds", "cozy-diorama");

        // Target tree height as a multiple of hut height, per height class (user decision, stage B).
        private static readonly Vector2[] ClassRange =
        {
            new Vector2(1.5f, 2.0f), // small
            new Vector2(2.0f, 2.5f), // medium
            new Vector2(2.5f, 3.5f), // tall
        };

        private static readonly string[] ClassName = { "small", "medium", "tall" };

        private readonly struct Species
        {
            public readonly string Name;
            public readonly string Path;
            public readonly int Class;

            public Species(string name, string path, int cls)
            {
                Name = name;
                Path = path;
                Class = cls;
            }
        }

        // Class by native silhouette height (see stage A audit): oak/ginkgo ~6-8 m, mid ~10-14 m, tall 13-19 m.
        private static readonly Species[] Trees =
        {
            new("SawtoothOak", DecorResources + "/SawtoothOak.prefab", 0),
            new("Ginkgo_wood", PolyelerPrefabs + "/Ginkgo_wood.prefab", 0),
            new("CherryBlossom", PolyelerPrefabs + "/CherryBlossom.prefab", 1),
            new("PlaneTree", PolyelerPrefabs + "/PlaneTree.prefab", 1),
            new("Pine", DecorResources + "/Pine.prefab", 1),
            new("Bamboo", DecorResources + "/Bamboo.prefab", 1),
            new("DawnRedwood", PolyelerPrefabs + "/DawnRedwood.prefab", 2),
            new("JapaneseCedar", DecorResources + "/JapaneseCedar.prefab", 2),
            new("JapaneseCypress", DecorResources + "/JapaneseCypress.prefab", 2),
        };

        private readonly struct Variant
        {
            public readonly string Name;
            public readonly float T; // position inside each class range: 0 = low end, 1 = high end

            public Variant(string name, float t)
            {
                Name = name;
                T = t;
            }
        }

        private static readonly Variant[] Variants =
        {
            new("V1 low end", 0.10f),
            new("V2 middle", 0.50f),
            new("V3 high end", 0.90f),
        };

        private const float BlockSpacing = 16f;
        private const float GrassDiameter = 12.4f;
        private static readonly Vector3 Right = new Vector3(0.7071f, 0f, -0.7071f);
        private static readonly Vector3 Back = new Vector3(0.7071f, 0f, 0.7071f);

        private static readonly Dictionary<string, Bounds> Native = new();
        private static float _hutHeight;

        [MenuItem("AFK Island/Cozy Diorama/Tree Scale Study (Stage B)/Measure")]
        public static void MeasureMenu()
        {
            Debug.Log(WriteMeasure());
        }

        [MenuItem("AFK Island/Cozy Diorama/Tree Scale Study (Stage B)/Build Scene")]
        public static void BuildMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            BuildScene();
        }

        [MenuItem("AFK Island/Cozy Diorama/Tree Scale Study (Stage B)/Render Review Shots")]
        public static void RenderMenu()
        {
            RenderShots();
        }

        /// <summary>Batchmode (needs graphics, so no -nographics): measure, build scene, render shots.</summary>
        public static void RunBatch()
        {
            var code = 0;
            try
            {
                Debug.Log(WriteMeasure());
                BuildScene();
                RenderShots();
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AFK Island] Tree scale study failed: " + ex);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        // ---------------------------------------------------------------- measurement

        private static string WriteMeasure()
        {
            var sb = new StringBuilder(8192);
            var inv = CultureInfo.InvariantCulture;
            sb.AppendLine("Cozy Diorama stage B: scale measurements (Renderer.bounds, world space, Unity " +
                          Application.unityVersion + ")");
            sb.AppendLine();

            var hutScale = SettlementCellQuery.HutVisualUniformScale;
            sb.AppendLine("== Huts ==");
            sb.AppendLine("In-game visual scale: SettlementCellQuery.HutVisualUniformScale = " +
                          hutScale.ToString("0.00", inv));
            foreach (var hutName in new[] { "Hut_A", "Hut_B" })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SettlementResources + "/" + hutName + ".prefab");
                if (prefab == null)
                {
                    sb.AppendLine(hutName + ": MISSING");
                    continue;
                }

                foreach (var scale in new[] { 1f, hutScale })
                {
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    try
                    {
                        inst.transform.position = Vector3.zero;
                        inst.transform.rotation = Quaternion.identity;
                        inst.transform.localScale = Vector3.one * scale;
                        var all = inst.GetComponentsInChildren<Renderer>(true);
                        var total = new Bounds();
                        var has = false;
                        foreach (var r in all)
                        {
                            if (!has)
                            {
                                total = r.bounds;
                                has = true;
                            }
                            else
                            {
                                total.Encapsulate(r.bounds);
                            }
                        }

                        sb.AppendLine(string.Format(inv,
                            "{0} at scale {1:0.00}: root localScale={2}, total size {3:0.00} x {4:0.00} x {5:0.00} m (w x h x d), top y={6:0.00}",
                            hutName, scale, inst.transform.localScale,
                            total.size.x, total.size.y, total.size.z, total.max.y));
                        foreach (var r in all)
                        {
                            sb.AppendLine(string.Format(inv,
                                "    {0,-8} localScale=({1:0.###},{2:0.###},{3:0.###}) bounds h={4:0.00} w={5:0.00} d={6:0.00} y=[{7:0.00}..{8:0.00}]",
                                r.name, r.transform.localScale.x, r.transform.localScale.y, r.transform.localScale.z,
                                r.bounds.size.y, r.bounds.size.x, r.bounds.size.z, r.bounds.min.y, r.bounds.max.y));
                        }

                        var door = inst.transform.Find("Door");
                        var doorRenderer = door != null ? door.GetComponent<Renderer>() : null;
                        if (doorRenderer != null)
                        {
                            sb.AppendLine(string.Format(inv,
                                "    => door height {0:0.00} m = {1:0.0}% of hut height; resident capsule is {2:0.00} m tall (ResidentDirector scale 0.45 on a 2 m capsule)",
                                doorRenderer.bounds.size.y,
                                100f * doorRenderer.bounds.size.y / total.size.y,
                                0.9f));
                        }
                    }
                    finally
                    {
                        Object.DestroyImmediate(inst);
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("== Trees (native, scale 1, LOD0 renderers) ==");
            sb.AppendLine("name | class | root localScale | importer globalScale/useFileScale | height | width x depth | in-game height (scale 0.58..0.88)");
            Native.Clear();
            foreach (var t in Trees)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(t.Path);
                if (prefab == null)
                {
                    sb.AppendLine(t.Name + " | MISSING " + t.Path);
                    continue;
                }

                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                try
                {
                    inst.transform.position = Vector3.zero;
                    inst.transform.rotation = Quaternion.identity;
                    inst.transform.localScale = Vector3.one;
                    var b = MeasureLod0(inst);
                    Native[t.Name] = b;

                    var importerInfo = "n/a";
                    var mr = inst.GetComponentInChildren<MeshFilter>(true);
                    if (mr != null && mr.sharedMesh != null)
                    {
                        var meshPath = AssetDatabase.GetAssetPath(mr.sharedMesh);
                        if (AssetImporter.GetAtPath(meshPath) is ModelImporter mi)
                        {
                            importerInfo = mi.globalScale.ToString("0.###", inv) + "/" + mi.useFileScale;
                        }
                    }

                    sb.AppendLine(string.Format(inv,
                        "{0} | {1} | {2} | {3} | {4:0.00} m | {5:0.00} x {6:0.00} m | {7:0.0}..{8:0.0} m",
                        t.Name, ClassName[t.Class], inst.transform.localScale, importerInfo,
                        b.size.y, b.size.x, b.size.z, b.size.y * 0.58f, b.size.y * 0.88f));
                }
                finally
                {
                    Object.DestroyImmediate(inst);
                }
            }

            _hutHeight = MeasureHutHeight();
            sb.AppendLine();
            sb.AppendLine(string.Format(inv, "Reference hut height H (Hut_A at in-game scale) = {0:0.000} m", _hutHeight));
            sb.AppendLine();
            sb.AppendLine("== Candidate variants (height = ratio x H) ==");
            foreach (var v in Variants)
            {
                sb.AppendLine(v.Name + " (t=" + v.T.ToString("0.00", inv) + ")");
                foreach (var t in Trees)
                {
                    if (!Native.TryGetValue(t.Name, out var nb))
                    {
                        continue;
                    }

                    var ratio = RatioFor(t.Class, v.T, 0f);
                    var scale = ratio * _hutHeight / nb.size.y;
                    sb.AppendLine(string.Format(inv,
                        "    {0,-16} {1,-6} ratio {2:0.00}x  height {3:0.00} m  prefab scale {4:0.000}  crown {5:0.00} x {6:0.00} m",
                        t.Name, ClassName[t.Class], ratio, ratio * _hutHeight, scale,
                        nb.size.x * scale, nb.size.z * scale));
                }
            }

            var text = sb.ToString();
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(Path.Combine(OutDir, "stage-b-scale-measure.txt"), text, new UTF8Encoding(false));
            return text;
        }

        private static float MeasureHutHeight()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SettlementResources + "/Hut_A.prefab");
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                inst.transform.position = Vector3.zero;
                inst.transform.rotation = Quaternion.identity;
                inst.transform.localScale = Vector3.one * SettlementCellQuery.HutVisualUniformScale;
                return MeasureAll(inst).size.y;
            }
            finally
            {
                Object.DestroyImmediate(inst);
            }
        }

        private static Bounds MeasureAll(GameObject inst)
        {
            var total = new Bounds();
            var has = false;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                if (!has)
                {
                    total = r.bounds;
                    has = true;
                }
                else
                {
                    total.Encapsulate(r.bounds);
                }
            }

            return total;
        }

        private static Bounds MeasureLod0(GameObject inst)
        {
            var group = inst.GetComponent<LODGroup>();
            var renderers = new List<Renderer>();
            if (group != null)
            {
                var lods = group.GetLODs();
                if (lods.Length > 0 && lods[0].renderers != null)
                {
                    foreach (var r in lods[0].renderers)
                    {
                        if (r != null)
                        {
                            renderers.Add(r);
                        }
                    }
                }
            }

            if (renderers.Count == 0)
            {
                renderers.AddRange(inst.GetComponentsInChildren<Renderer>(true));
            }

            var total = renderers[0].bounds;
            for (var i = 1; i < renderers.Count; i++)
            {
                total.Encapsulate(renderers[i].bounds);
            }

            return total;
        }

        private static float RatioFor(int cls, float t, float jitter)
        {
            var range = ClassRange[cls];
            var tt = Mathf.Clamp01(t + jitter);
            return Mathf.Lerp(range.x, range.y, tt);
        }

        // ---------------------------------------------------------------- scene

        private static void BuildScene()
        {
            if (_hutHeight <= 0f || Native.Count == 0)
            {
                WriteMeasure();
            }

            CozyDioramaPrototypeSceneBuilder.EnsureFolder("Assets/_Project", "Scenes");
            CozyDioramaPrototypeSceneBuilder.EnsureFolder("Assets/_Project/Scenes", "Prototypes");
            CozyDioramaPrototypeSceneBuilder.EnsureFolder("Assets/_Project", "Art");
            CozyDioramaPrototypeSceneBuilder.EnsureFolder("Assets/_Project/Art", "CozyDiorama");
            CozyDioramaPrototypeSceneBuilder.EnsureFolder("Assets/_Project/Art/CozyDiorama", "Materials");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CozyDioramaPrototypeSceneBuilder.ConfigureEnvironment();
            CozyDioramaPrototypeSceneBuilder.CreateSun();

            var root = new GameObject("TreeScaleStudy");
            var water = new GameObject("Water");
            water.transform.SetParent(root.transform, false);
            CozyDioramaPrototypeSceneBuilder.CreateWaterPlaceholder(water.transform);

            for (var i = 0; i < Variants.Length; i++)
            {
                var center = (i - 1) * BlockSpacing * Right;
                BuildBlock(root.transform, Variants[i].Name, center, GrassDiameter, Variants[i].T, 1, false); // same grove seed in every variant: only scale differs
            }

            // Fourth block: today's in-game scale for comparison. Placed outside the general frame.
            BuildBlock(root.transform, "Reference current in-game scale", 3f * BlockSpacing * Right, 17f, 0f, 0, true);

            var camGo = new GameObject("StudyCamera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 15f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = CozyDioramaPrototypeSceneBuilder.Hex("#9CC4D0");
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<UniversalAdditionalCameraData>();
            PoseCamera(camGo.transform, new Vector3(0f, 1.2f, 0f));

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[AFK Island] Tree scale study scene saved: " + ScenePath);
        }

        private static void PoseCamera(Transform t, Vector3 focus)
        {
            t.rotation = Quaternion.Euler(45f, 45f, 0f);
            t.position = focus + t.rotation * new Vector3(0f, 0f, -40f);
        }

        private static Vector3 At(Vector3 center, float u, float f, float y = 0f)
        {
            return center + u * Right + f * Back + new Vector3(0f, y, 0f);
        }

        private static void BuildBlock(
            Transform parent, string name, Vector3 center, float grassDiameter, float t, int seedId, bool current)
        {
            var block = new GameObject(name);
            block.transform.SetParent(parent, false);
            block.transform.position = center;

            // Ground: sand ring under a grass disc. Top of the grass is y = 0.
            var sand = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            sand.name = "Sand";
            sand.transform.SetParent(block.transform, false);
            sand.transform.localPosition = new Vector3(0f, -0.16f, 0f);
            sand.transform.localScale = new Vector3(grassDiameter + 1.0f, 0.14f, grassDiameter + 1.0f);
            StripAndPaint(sand, "Cozy_ReviewScaleRef");
            var grass = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            grass.name = "Grass";
            grass.transform.SetParent(block.transform, false);
            grass.transform.localPosition = new Vector3(0f, -0.15f, 0f);
            grass.transform.localScale = new Vector3(grassDiameter, 0.15f, grassDiameter);
            StripAndPaint(grass, "Cozy_ReviewSlab");

            // Huts at the in-game visual scale, doors toward the camera.
            PlaceHut(block.transform, "Hut_A", At(center, -1.4f, 0.3f), -135f + 12f);
            PlaceHut(block.transform, "Hut_B", At(center, 1.0f, -0.4f), -135f - 25f);

            // Resident: a capsule like ResidentDirector builds (0.42 x 0.45 x 0.42 on a 2 m capsule).
            var resident = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            resident.name = "Resident_Reference";
            resident.transform.SetParent(block.transform, false);
            resident.transform.position = At(center, -0.2f, -1.4f, 0.45f);
            resident.transform.localScale = new Vector3(0.42f, 0.45f, 0.42f);
            StripAndPaint(resident, "Cozy_StudyResident", "#D9C3A5");

            BuildRuler(block.transform, At(center, -4.6f, -1.6f));

            // Shrubs and rocks keep their in-game scale; only trees are being normalised here.
            PlaceDecor(block.transform, DecorResources + "/JapaneseLaurel.prefab", At(center, -3.0f, 1.6f), 0.40f, 11f);
            PlaceDecor(block.transform, DecorResources + "/JapaneseEurya.prefab", At(center, 0.2f, 1.5f), 0.40f, 77f);
            PlaceDecor(block.transform, DecorResources + "/Mugwort.prefab", At(center, 2.9f, 1.2f), 0.45f, 133f);
            PlaceDecor(block.transform, DecorResources + "/ViolaGrypoceras.prefab", At(center, -2.4f, -2.6f), 0.45f, 201f);
            PlaceDecor(block.transform, DecorResources + "/LittleBamboo.prefab", At(center, 3.2f, -3.0f), 0.50f, 17f);
            PlaceDecor(block.transform, DecorResources + "/Rock_A.prefab", At(center, 1.9f, -2.2f), 0.30f, 40f);
            PlaceDecor(block.transform, DecorResources + "/Rock_B.prefab", At(center, -1.2f, 2.3f), 0.35f, 210f);
            PlaceDecor(block.transform, DecorResources + "/Rock_D.prefab", At(center, -4.0f, 0.8f), 0.25f, 99f);

            var label = new GameObject("Label");
            label.transform.SetParent(block.transform, false);
            label.transform.position = At(center, -3.2f, -5.2f, 0.3f);
            label.transform.rotation = Quaternion.Euler(45f, 45f, 0f);
            var text = label.AddComponent<TextMesh>();
            text.text = name;
            text.fontSize = 64;
            text.characterSize = 0.075f;
            text.anchor = TextAnchor.MiddleLeft;
            text.color = Color.white;

            if (current)
            {
                // In-game scale: cedar 0.55, other tall trees 0.58..0.88 (RebuildTallTrees). Midpoint 0.72.
                var names = new[] { "SawtoothOak", "JapaneseCedar", "Pine", "JapaneseCypress", "Bamboo" };
                var scales = new[] { 0.72f, 0.55f, 0.72f, 0.72f, 0.72f };
                for (var i = 0; i < names.Length; i++)
                {
                    var sp = FindSpecies(names[i]);
                    PlaceTreeAbsolute(block.transform, sp, At(center, -4.0f + 2.1f * i, 5.0f), scales[i], i * 71f);
                }

                return;
            }

            // Back arc: the nine species, small to tall, spacing fixed, exact class ratio (no jitter).
            for (var k = 0; k < Trees.Length; k++)
            {
                var sp = Trees[k];
                var ratio = RatioFor(sp.Class, t, 0f);
                PlaceTreeRatio(block.transform, sp, At(center, -4.6f + 1.15f * k, 3.4f), ratio, k * 37f + 11f);
            }

            // Loose grove on the right: deterministic jitter inside each class range.
            var groveCenterU = 3.7f;
            var groveCenterF = -1.0f;
            for (var g = 0; g < 7; g++)
            {
                var hu = WorldScatterField.Hash01(seedId, g, 3, 5u);
                var hf = WorldScatterField.Hash01(seedId, g, 3, 6u);
                var hs = WorldScatterField.Hash01(seedId, g, 3, 7u);
                var hj = WorldScatterField.Hash01(seedId, g, 3, 8u);
                var sp = Trees[Mathf.Min(Trees.Length - 1, Mathf.FloorToInt(hs * Trees.Length))];
                var ratio = RatioFor(sp.Class, t, (hj - 0.5f) * 0.2f);
                var u = groveCenterU + (hu - 0.5f) * 2.4f;
                var f = groveCenterF + (hf - 0.5f) * 3.2f;
                PlaceTreeRatio(block.transform, sp, At(center, u, f), ratio, hj * 360f);
            }
        }

        private static Species FindSpecies(string name)
        {
            foreach (var s in Trees)
            {
                if (s.Name == name)
                {
                    return s;
                }
            }

            return Trees[0];
        }

        private static void PlaceTreeRatio(Transform parent, Species sp, Vector3 pos, float ratio, float yaw)
        {
            if (!Native.TryGetValue(sp.Name, out var nb) || nb.size.y <= 0f)
            {
                return;
            }

            PlaceTreeAbsolute(parent, sp, pos, ratio * _hutHeight / nb.size.y, yaw);
        }

        private static void PlaceTreeAbsolute(Transform parent, Species sp, Vector3 pos, float scale, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(sp.Path);
            if (prefab == null)
            {
                return;
            }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.name = sp.Name;
            inst.transform.SetParent(parent, true);
            inst.transform.position = pos;
            inst.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            inst.transform.localScale = Vector3.one * scale;
        }

        private static void PlaceDecor(Transform parent, string path, Vector3 pos, float scale, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                return;
            }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.transform.SetParent(parent, true);
            inst.transform.position = pos;
            inst.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            inst.transform.localScale = Vector3.one * scale;
        }

        private static void PlaceHut(Transform parent, string name, Vector3 pos, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SettlementResources + "/" + name + ".prefab");
            if (prefab == null)
            {
                return;
            }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.name = name;
            inst.transform.SetParent(parent, true);
            inst.transform.position = pos;
            inst.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            inst.transform.localScale = Vector3.one * SettlementCellQuery.HutVisualUniformScale;
        }

        /// <summary>A pole as tall as 3.5 hut heights with ticks at 1.0 / 1.5 / 2.0 / 2.5 / 3.0 / 3.5 H.</summary>
        private static void BuildRuler(Transform parent, Vector3 basePos)
        {
            var ruler = new GameObject("Ruler_HutHeights");
            ruler.transform.SetParent(parent, false);
            ruler.transform.position = basePos;

            var pole = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pole.name = "Pole";
            pole.transform.SetParent(ruler.transform, false);
            var height = 3.5f * _hutHeight;
            pole.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
            pole.transform.localScale = new Vector3(0.05f, height, 0.05f);
            StripAndPaint(pole, "Cozy_StudyRulerPole", "#FFFFFF");

            var marks = new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 3.5f };
            var colors = new[] { "#FFFFFF", "#7ED67E", "#7ED67E", "#F2D45C", "#F2994A", "#F2994A" };
            for (var i = 0; i < marks.Length; i++)
            {
                var tick = GameObject.CreatePrimitive(PrimitiveType.Cube);
                tick.name = "Tick_" + marks[i].ToString("0.0", CultureInfo.InvariantCulture) + "H";
                tick.transform.SetParent(ruler.transform, false);
                tick.transform.localPosition = new Vector3(0f, marks[i] * _hutHeight, 0f);
                tick.transform.localScale = new Vector3(0.45f, 0.04f, 0.07f);
                StripAndPaint(tick, "Cozy_StudyTick" + i, colors[i]);
            }
        }

        private static void StripAndPaint(GameObject go, string materialName, string hex = null)
        {
            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                Object.DestroyImmediate(col);
            }

            var color = hex != null
                ? CozyDioramaPrototypeSceneBuilder.Hex(hex)
                : (materialName == "Cozy_ReviewSlab"
                    ? CozyDioramaPrototypeSceneBuilder.Hex("#78945B")
                    : CozyDioramaPrototypeSceneBuilder.Hex("#B6A477"));
            go.GetComponent<Renderer>().sharedMaterial =
                CozyDioramaPrototypeSceneBuilder.TempMaterial(materialName, color);
        }

        // ---------------------------------------------------------------- screenshots

        private static void RenderShots()
        {
            var camGo = GameObject.Find("StudyCamera");
            if (camGo == null)
            {
                Debug.LogError("[AFK Island] StudyCamera not found: build the study scene first.");
                return;
            }

            var cam = camGo.GetComponent<Camera>();
            var dir = Path.Combine(OutDir, "stage-b-shots");
            Directory.CreateDirectory(dir);

            // Warm-up: the first frames in batchmode render with unfinished shader variants
            // (black ground, purple foliage). Render and discard before the real shots.
            var warmup = Path.Combine(dir, "_warmup.png");
            for (var w = 0; w < 3; w++)
            {
                Shot(cam, warmup, new Vector3(0f, 1.5f, 0f), 15f);
            }

            File.Delete(warmup);

            // General view: all three variants. Medium view: one variant at the production default
            // ortho size 8.5. Reference: in-game scale for comparison.
            Shot(cam, Path.Combine(dir, "general_3_variants.png"), new Vector3(0f, 1.5f, 0f), 15f);
            for (var i = 0; i < Variants.Length; i++)
            {
                var center = (i - 1) * BlockSpacing * Right;
                Shot(cam, Path.Combine(dir, "medium_v" + (i + 1) + ".png"), center + new Vector3(0f, 1.5f, 0f), 8.5f);
            }

            Shot(cam, Path.Combine(dir, "reference_current_scale.png"),
                3f * BlockSpacing * Right + new Vector3(0f, 3.5f, 0f), 12f);
            Debug.Log("[AFK Island] Tree scale study shots written to " + dir);
        }

        private static void Shot(Camera cam, string file, Vector3 focus, float ortho)
        {
            const int w = 1920;
            const int h = 1080;
            PoseCamera(cam.transform, focus);
            cam.orthographicSize = ortho;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                File.WriteAllBytes(file, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = previous;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
        }
    }
}
#endif
