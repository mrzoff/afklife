#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using AfkIsland.CameraControl;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Cozy Diorama, stage A. Builds an isolated prototype scene and audits the existing
    /// reference prefabs. The scene is a sandbox: it has no GameBootstrap, no SaveService and no
    /// world simulation, and it is never added to Build Settings or to the production bootstrap.
    /// Nothing here touches Save v8, offline growth, WorldGrowthConfig or any runtime script.
    /// </summary>
    public static class CozyDioramaPrototypeSceneBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaPrototype.unity";
        private const string ScenesFolder = "Assets/_Project/Scenes";
        private const string PrototypesFolder = "Assets/_Project/Scenes/Prototypes";
        private const string PolyelerPrefabs = "Assets/Polyeler/EssentialNaturePack/Prefabs";
        private const string DecorResources = "Assets/_Project/Resources/AfkIsland/Decor";
        private const string SettlementResources = "Assets/_Project/Resources/AfkIsland/Settlement";
        private const string AnimalPrefabs = "Assets/_Project/Art/Animals/Prefabs";
        private const string MaterialsFolder = "Assets/_Project/Art/CozyDiorama/Materials";
        private static readonly string AuditPath =
            Path.Combine("Builds", "cozy-diorama", "stage-a-prefab-audit.txt");

        // Palette from Docs/AFK-ISLAND-ART-DIRECTION.md section 4.
        private static readonly Color Water = Hex("#579EB3");
        private static readonly Color Grass = Hex("#78945B");
        private static readonly Color Sand = Hex("#B6A477");

        private const float GalleryStep = 4f;

        private struct Entry
        {
            public string Row;
            public string Path;
            public float Scale;

            public Entry(string row, string path, float scale = 1f)
            {
                Row = row;
                Path = path;
                Scale = scale;
            }
        }

        // Rows are ordered by role. "Resources" entries are the copies the game really loads;
        // "Polyeler-only" entries are in the pack but not wired into the game yet.
        private static readonly Entry[] Reference =
        {
            new("1 Trees (in game)", DecorResources + "/JapaneseCedar.prefab"),
            new("1 Trees (in game)", DecorResources + "/JapaneseCypress.prefab"),
            new("1 Trees (in game)", DecorResources + "/Pine.prefab"),
            new("1 Trees (in game)", DecorResources + "/SawtoothOak.prefab"),
            new("1 Trees (in game)", DecorResources + "/Bamboo.prefab"),
            new("2 Trees (pack only)", PolyelerPrefabs + "/CherryBlossom.prefab"),
            new("2 Trees (pack only)", PolyelerPrefabs + "/DawnRedwood.prefab"),
            new("2 Trees (pack only)", PolyelerPrefabs + "/Ginkgo_wood.prefab"),
            new("2 Trees (pack only)", PolyelerPrefabs + "/PlaneTree.prefab"),
            new("3 Shrubs and ground cover", DecorResources + "/JapaneseLaurel.prefab"),
            new("3 Shrubs and ground cover", DecorResources + "/JapaneseEurya.prefab"),
            new("3 Shrubs and ground cover", DecorResources + "/LittleBamboo.prefab"),
            new("3 Shrubs and ground cover", DecorResources + "/Mugwort.prefab"),
            new("3 Shrubs and ground cover", DecorResources + "/ViolaGrypoceras.prefab"),
            new("3 Shrubs and ground cover", PolyelerPrefabs + "/FallenLeaves.prefab"),
            new("4 Rocks and deadwood", DecorResources + "/Rock_A.prefab"),
            new("4 Rocks and deadwood", DecorResources + "/Rock_B.prefab"),
            new("4 Rocks and deadwood", DecorResources + "/Rock_C.prefab"),
            new("4 Rocks and deadwood", DecorResources + "/Rock_D.prefab"),
            new("4 Rocks and deadwood", PolyelerPrefabs + "/Decayed_wood_A.prefab"),
            new("4 Rocks and deadwood", PolyelerPrefabs + "/Decayed_wood_C.prefab"),
            new("4 Rocks and deadwood", PolyelerPrefabs + "/Decayed_wood_E.prefab"),
            // Hut scale 0.62 is the in-game visual scale (SettlementCellQuery.HutVisualUniformScale).
            new("5 Settlement", SettlementResources + "/Hut_A.prefab", 0.62f),
            new("5 Settlement", SettlementResources + "/Hut_B.prefab", 0.62f),
            new("5 Settlement", SettlementResources + "/Path_Tile.prefab"),
            new("5 Settlement", SettlementResources + "/Clearing_Pad.prefab"),
            new("6 Fauna", AnimalPrefabs + "/Animal_Bird.prefab"),
            new("6 Fauna", AnimalPrefabs + "/Animal_Butterfly.prefab"),
            new("6 Fauna", AnimalPrefabs + "/Animal_Frog.prefab"),
        };

        [MenuItem("AFK Island/Cozy Diorama/Build Prototype Scene (Stage A)")]
        public static void BuildMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            var ok = BuildInternal();
            Debug.Log(ok
                ? "[AFK Island] CozyDioramaPrototype scene saved: " + ScenePath
                : "[AFK Island] CozyDioramaPrototype scene build finished with warnings, see log.");
        }

        [MenuItem("AFK Island/Cozy Diorama/Audit Reference Prefabs (Stage A)")]
        public static void AuditMenu()
        {
            WriteAudit();
        }

        /// <summary>Batchmode: -executeMethod ...CozyDioramaPrototypeSceneBuilder.BuildBatch. No dialogs.</summary>
        public static void BuildBatch()
        {
            try
            {
                var ok = BuildInternal();
                WriteAudit();
                Debug.Log("[AFK Island] Cozy Diorama stage A batch done. missingPrefabs=" + (ok ? 0 : 1));
                EditorApplication.Exit(ok ? 0 : 3);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AFK Island] Cozy Diorama stage A batch failed: " + ex);
                EditorApplication.Exit(1);
            }
        }

        private static void WriteAudit()
        {
            var report = BuildAuditReport();
            var dir = Path.GetDirectoryName(AuditPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(AuditPath, report, new UTF8Encoding(false));
            Debug.Log("[AFK Island] Cozy Diorama prefab audit written: " + AuditPath + "\n" + report);
        }

        private static bool BuildInternal()
        {
            EnsureFolder("Assets/_Project", "Scenes");
            EnsureFolder(ScenesFolder, "Prototypes");
            EnsureFolder("Assets/_Project", "Art");
            EnsureFolder("Assets/_Project/Art", "CozyDiorama");
            EnsureFolder("Assets/_Project/Art/CozyDiorama", "Materials");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            ConfigureEnvironment();
            CreateSun();
            CreateCamera();

            var root = new GameObject("CozyDioramaPrototype");
            var groups = new[]
            {
                "Terrain", "Water", "Paths", "Settlement", "Vegetation", "Rocks", "Fauna"
            };
            for (var i = 0; i < groups.Length; i++)
            {
                new GameObject(groups[i]).transform.SetParent(root.transform, false);
            }

            CreateWaterPlaceholder(root.transform.Find("Water"));

            var missing = BuildAssetReview();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return missing == 0;
        }

        internal static void ConfigureEnvironment()
        {
            // Soft tri-light ambient, no fog, no skybox. Matches the production daytime intent
            // (sun 45/-35, warm white) but with a gentler ambient so shadows stay readable, not black.
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Hex("#9DB8C8");
            RenderSettings.ambientEquatorColor = Hex("#8C9A86");
            RenderSettings.ambientGroundColor = Hex("#5E604C");
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.fog = false;
        }

        internal static void CreateSun()
        {
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.95f, 0.86f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;
            sunGo.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
            RenderSettings.sun = sun;
        }

        private static void CreateCamera()
        {
            // Same pose contract as production: ortho, yaw 45 / pitch 45, ortho size 8.5 by default,
            // zoom range 5..16 handled by IsometricCameraController.
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 8.5f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Hex("#9CC4D0");
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<UniversalAdditionalCameraData>();
            var ctrl = camGo.AddComponent<IsometricCameraController>();
            ctrl.ResetToIslandCenter();
        }

        internal static void CreateWaterPlaceholder(Transform parent)
        {
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = "Water_Placeholder";
            plane.transform.SetParent(parent, false);
            plane.transform.position = new Vector3(0f, -0.35f, 0f);
            plane.transform.localScale = new Vector3(20f, 1f, 20f);
            Object.DestroyImmediate(plane.GetComponent<Collider>());
            plane.GetComponent<Renderer>().sharedMaterial = TempMaterial("Cozy_WaterPlaceholder", Water);
        }

        /// <summary>
        /// Lays every reference prefab out in rows on neutral slabs, next to a 1 m cube.
        /// Stage B deletes the whole AssetReview object when the reference island is assembled.
        /// </summary>
        private static int BuildAssetReview()
        {
            var review = new GameObject("AssetReview");
            // Offset far from the future island so the two never overlap in one frame.
            review.transform.position = new Vector3(0f, 0f, 40f);

            var rowOrder = new List<string>();
            var rowCount = new Dictionary<string, int>();
            for (var i = 0; i < Reference.Length; i++)
            {
                var row = Reference[i].Row;
                if (!rowCount.ContainsKey(row))
                {
                    rowOrder.Add(row);
                    rowCount[row] = 0;
                }
            }

            var missing = 0;
            var slabMat = TempMaterial("Cozy_ReviewSlab", Grass);
            var cubeMat = TempMaterial("Cozy_ReviewScaleRef", Sand);
            for (var r = 0; r < rowOrder.Count; r++)
            {
                var rowName = rowOrder[r];
                var rowGo = new GameObject(rowName);
                rowGo.transform.SetParent(review.transform, false);
                rowGo.transform.localPosition = new Vector3(0f, 0f, r * GalleryStep);

                var count = 0;
                for (var i = 0; i < Reference.Length; i++)
                {
                    if (Reference[i].Row == rowName)
                    {
                        count++;
                    }
                }

                var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
                slab.name = "Slab";
                slab.transform.SetParent(rowGo.transform, false);
                var width = (count + 1) * GalleryStep;
                slab.transform.localScale = new Vector3(width, 0.1f, GalleryStep - 0.4f);
                slab.transform.localPosition = new Vector3(width * 0.5f - GalleryStep * 0.5f, -0.05f, 0f);
                Object.DestroyImmediate(slab.GetComponent<Collider>());
                slab.GetComponent<Renderer>().sharedMaterial = slabMat;

                // 1 m reference cube is the first item in every row.
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "ScaleRef_1m";
                cube.transform.SetParent(rowGo.transform, false);
                cube.transform.localPosition = new Vector3(0f, 0.5f, 0f);
                Object.DestroyImmediate(cube.GetComponent<Collider>());
                cube.GetComponent<Renderer>().sharedMaterial = cubeMat;

                var slot = 1;
                for (var i = 0; i < Reference.Length; i++)
                {
                    var entry = Reference[i];
                    if (entry.Row != rowName)
                    {
                        continue;
                    }

                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.Path);
                    if (prefab == null)
                    {
                        Debug.LogWarning("[AFK Island] Cozy Diorama: prefab not found: " + entry.Path);
                        missing++;
                        slot++;
                        continue;
                    }

                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    inst.name = Path.GetFileNameWithoutExtension(entry.Path);
                    inst.transform.SetParent(rowGo.transform, false);
                    inst.transform.localPosition = new Vector3(slot * GalleryStep, 0f, 0f);
                    inst.transform.localScale = Vector3.one * entry.Scale;
                    slot++;
                }
            }

            return missing;
        }

        private static string BuildAuditReport()
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine("Cozy Diorama stage A prefab audit");
            sb.AppendLine("Unity " + Application.unityVersion + ", render pipeline: " +
                          (GraphicsSettings.currentRenderPipeline != null
                              ? GraphicsSettings.currentRenderPipeline.name
                              : "none"));
            sb.AppendLine("Bounds are world-space at scale 1, origin at prefab pivot. LOD0 only when a LODGroup exists.");
            sb.AppendLine();
            sb.AppendLine("row | prefab | LODs | LOD0 tris | LOD0 verts | size x,y,z (m) | pivot-to-min-y | shaders | colliders");

            for (var i = 0; i < Reference.Length; i++)
            {
                var e = Reference[i];
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(e.Path);
                if (prefab == null)
                {
                    sb.AppendLine(e.Row + " | " + e.Path + " | MISSING");
                    continue;
                }

                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                try
                {
                    inst.transform.position = Vector3.zero;
                    inst.transform.rotation = Quaternion.identity;
                    inst.transform.localScale = Vector3.one;

                    var lodGroup = inst.GetComponent<LODGroup>();
                    var lods = lodGroup != null ? lodGroup.GetLODs() : null;
                    var renderers = new List<Renderer>();
                    if (lods != null && lods.Length > 0 && lods[0].renderers != null)
                    {
                        for (var r = 0; r < lods[0].renderers.Length; r++)
                        {
                            if (lods[0].renderers[r] != null)
                            {
                                renderers.Add(lods[0].renderers[r]);
                            }
                        }
                    }
                    else
                    {
                        renderers.AddRange(inst.GetComponentsInChildren<Renderer>(true));
                    }

                    long tris = 0;
                    long verts = 0;
                    var hasBounds = false;
                    var bounds = new Bounds();
                    var shaders = new HashSet<string>();
                    for (var r = 0; r < renderers.Count; r++)
                    {
                        var renderer = renderers[r];
                        if (renderer == null)
                        {
                            continue;
                        }

                        if (!hasBounds)
                        {
                            bounds = renderer.bounds;
                            hasBounds = true;
                        }
                        else
                        {
                            bounds.Encapsulate(renderer.bounds);
                        }

                        var mats = renderer.sharedMaterials;
                        for (var m = 0; m < mats.Length; m++)
                        {
                            if (mats[m] != null && mats[m].shader != null)
                            {
                                shaders.Add(mats[m].shader.name);
                            }
                        }

                        var mf = renderer.GetComponent<MeshFilter>();
                        var smr = renderer as SkinnedMeshRenderer;
                        var mesh = mf != null ? mf.sharedMesh : (smr != null ? smr.sharedMesh : null);
                        if (mesh != null)
                        {
                            verts += mesh.vertexCount;
                            for (var s = 0; s < mesh.subMeshCount; s++)
                            {
                                tris += (long)mesh.GetIndexCount(s) / 3;
                            }
                        }
                    }

                    var colliders = inst.GetComponentsInChildren<Collider>(true).Length;
                    var size = hasBounds ? bounds.size : Vector3.zero;
                    var minY = hasBounds ? bounds.min.y : 0f;
                    sb.AppendLine(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0} | {1} | {2} | {3} | {4} | {5:0.00},{6:0.00},{7:0.00} | {8:0.00} | {9} | {10}",
                        e.Row,
                        Path.GetFileNameWithoutExtension(e.Path),
                        lods != null ? lods.Length : 0,
                        tris,
                        verts,
                        size.x, size.y, size.z,
                        minY,
                        string.Join(";", new List<string>(shaders).ToArray()),
                        colliders));
                }
                finally
                {
                    Object.DestroyImmediate(inst);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Prototype materials are real assets under Art/CozyDiorama/Materials, so the scene
        /// keeps its references after reload. Existing assets are updated, not duplicated.
        /// </summary>
        internal static Material TempMaterial(string name, Color color)
        {
            var path = MaterialsFolder + "/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }

            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }
            else
            {
                mat.color = color;
            }

            if (mat.HasProperty("_Smoothness"))
            {
                mat.SetFloat("_Smoothness", 0.1f);
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        internal static Color Hex(string html)
        {
            return ColorUtility.TryParseHtmlString(html, out var c) ? c : Color.magenta;
        }

        internal static void EnsureFolder(string parent, string child)
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
