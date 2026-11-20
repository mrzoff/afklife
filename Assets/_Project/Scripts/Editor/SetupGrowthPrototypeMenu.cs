#if UNITY_EDITOR
using System.Collections.Generic;
using AfkIsland.Animals;
using AfkIsland.CameraControl;
using AfkIsland.Core;
using AfkIsland.Offline;
using AfkIsland.Plants;
using AfkIsland.Save;
using AfkIsland.UI;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    public static class SetupGrowthPrototypeMenu
    {
        private const string ScenePath = "Assets/_Project/Scenes/MainIsland.unity";
        private const string PlantFolder = "Assets/_Project/Prefabs/Plants";
        private const string SoFolder = "Assets/_Project/ScriptableObjects/Plants";
        private const string AnimalSoFolder = "Assets/_Project/ScriptableObjects/Animals";

        [MenuItem("AFK Island/Setup Growth Prototype (Stage 2)")]
        public static void Setup()
        {
            DirectoryEnsure("Assets/_Project/Prefabs");
            DirectoryEnsure(PlantFolder);
            DirectoryEnsure("Assets/_Project/ScriptableObjects");
            DirectoryEnsure(SoFolder);
            DirectoryEnsure(AnimalSoFolder);

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Prefer Stage 9.1 Nature Kit plant stages when present.
            Stage91NatureKitBuilder.ApplyMenuSafe();
            var stage0 = AssetDatabase.LoadAssetAtPath<GameObject>(PlantFolder + "/Stage_Seed.prefab")
                         ?? MakePrimitivePrefab(PlantFolder + "/Stage_Seed.prefab", PrimitiveType.Sphere,
                             new Vector3(0.18f, 0.18f, 0.18f), new Color(0.45f, 0.3f, 0.15f), 0.12f);
            var stage1 = AssetDatabase.LoadAssetAtPath<GameObject>(PlantFolder + "/Stage_Sprout.prefab")
                         ?? MakePrimitivePrefab(PlantFolder + "/Stage_Sprout.prefab", PrimitiveType.Cylinder,
                             new Vector3(0.08f, 0.25f, 0.08f), new Color(0.3f, 0.7f, 0.25f), 0.25f);
            var stage2 = AssetDatabase.LoadAssetAtPath<GameObject>(PlantFolder + "/Stage_Young.prefab")
                         ?? MakeScaledPrefab(PlantFolder + "/Stage_Young.prefab",
                             "Assets/Polyeler/EssentialNaturePack/Prefabs/JapaneseCedar.prefab", 0.28f)
                         ?? MakePrimitivePrefab(PlantFolder + "/Stage_Young.prefab", PrimitiveType.Cylinder,
                             new Vector3(0.15f, 0.7f, 0.15f), new Color(0.25f, 0.55f, 0.2f), 0.7f);
            var stage3 = AssetDatabase.LoadAssetAtPath<GameObject>(PlantFolder + "/Stage_Adult.prefab")
                         ?? MakeScaledPrefab(PlantFolder + "/Stage_Adult.prefab",
                             "Assets/Polyeler/EssentialNaturePack/Prefabs/SawtoothOak.prefab", 0.48f)
                         ?? MakePrimitivePrefab(PlantFolder + "/Stage_Adult.prefab", PrimitiveType.Cylinder,
                             new Vector3(0.25f, 1.2f, 0.25f), new Color(0.2f, 0.45f, 0.18f), 1.2f);

            var plantData = AssetDatabase.LoadAssetAtPath<PlantData>(SoFolder + "/PlantData_StarterTree.asset");
            if (plantData == null)
            {
                plantData = ScriptableObject.CreateInstance<PlantData>();
                AssetDatabase.CreateAsset(plantData, SoFolder + "/PlantData_StarterTree.asset");
            }

            plantData.plantId = "starter_tree";
            plantData.displayName = "Starter Tree";
            plantData.growthStagePrefabs = new[] { stage0, stage1, stage2, stage3 };
            plantData.stageDurationsSeconds = new[] { 20f, 40f, 60f, 90f };
            EditorUtility.SetDirty(plantData);

            var catalogPath = SoFolder + "/PlantCatalog.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<PlantCatalog>(catalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<PlantCatalog>();
                AssetDatabase.CreateAsset(catalog, catalogPath);
            }

            catalog.Configure(new[] { plantData });
            EditorUtility.SetDirty(catalog);

            var animalCatalog = new[]
            {
                EnsureAnimalData("butterfly", "Бабочка", new Color(0.95f, 0.55f, 0.2f), 0.7f, 2.2f, new Vector3(0.22f, 0.22f, 0.22f)),
                EnsureAnimalData("bird", "Птица", new Color(0.35f, 0.55f, 0.9f), 0.85f, 3.2f, new Vector3(0.28f, 0.28f, 0.28f)),
                EnsureAnimalData("frog", "Лягушка", new Color(0.25f, 0.7f, 0.3f), 0.4f, 1.4f, new Vector3(0.3f, 0.22f, 0.3f))
            };

            var world = GameObject.Find("World");
            var island = world != null ? world.transform.Find("Island") : null;
            if (island == null)
            {
                throw new System.InvalidOperationException("Island missing");
            }

            var systems = GameObject.Find("GameSystems");
            if (systems != null)
            {
                Object.DestroyImmediate(systems);
            }

            systems = new GameObject("GameSystems");

            var plantsRoot = new GameObject("Plants");
            plantsRoot.transform.SetParent(systems.transform, false);

            var plotsRoot = island.Find("PlantPlots");
            if (plotsRoot != null)
            {
                Object.DestroyImmediate(plotsRoot.gameObject);
            }

            plotsRoot = new GameObject("PlantPlots").transform;
            plotsRoot.SetParent(island, false);

            var plotPositions = new[]
            {
                new Vector3(-1.5f, 1.88f, 1.2f),
                new Vector3(1.4f, 1.88f, 1.0f),
                new Vector3(0.0f, 1.88f, -1.6f),
                new Vector3(2.2f, 1.88f, -0.4f)
            };

            var plots = new List<PlantPlot>();
            for (var i = 0; i < plotPositions.Length; i++)
            {
                var plotGo = new GameObject($"Plot_{i + 1:00}");
                plotGo.transform.SetParent(plotsRoot, false);
                plotGo.transform.localPosition = plotPositions[i];
                var plot = plotGo.AddComponent<PlantPlot>();
                plot.Setup($"plot_{i + 1:00}");
                plots.Add(plot);
            }

            var planting = systems.AddComponent<PlantingSystem>();
            var online = systems.AddComponent<OnlineGrowthTicker>();
            var save = systems.AddComponent<SaveService>();
            var offline = systems.AddComponent<OfflineGrowthService>();
            var chunks = systems.AddComponent<ChunkManager>();
            var worldGrowth = systems.AddComponent<WorldGrowthService>();
            var hud = systems.AddComponent<ReturnSummaryHud>();
            var reportHud = systems.AddComponent<ReturnReportHud>();
            var expansionHud = systems.AddComponent<IslandExpansionFeedback>();
            var meshViz = systems.AddComponent<IslandMeshVisualizer>();
            var settleViz = systems.AddComponent<HomeSettlementVisualizer>();
            var growthDebug = systems.AddComponent<PrototypeWorldGrowthDebug>();
            var animals = systems.AddComponent<AnimalSpawner>();
            var weatherVisual = systems.AddComponent<DayNightWeatherVisual>();
            systems.AddComponent<WorldVisualEnvironment>();
            // Stage 11E-1: VFX root is scene-level — never parented under GameSystems.
            var atmosphereRoot = GameObject.Find(WorldAtmosphereVfx.IsolatedRootName);
            if (atmosphereRoot == null)
            {
                atmosphereRoot = new GameObject(WorldAtmosphereVfx.IsolatedRootName);
            }

            if (atmosphereRoot.GetComponent<WorldAtmosphereVfx>() == null)
            {
                atmosphereRoot.AddComponent<WorldAtmosphereVfx>();
            }

            systems.AddComponent<WaterSurfaceApplicator>();
            var ecoHud = systems.AddComponent<EcosystemHud>();
            var menu = systems.AddComponent<MainMenuHud>();
            var bootstrap = systems.AddComponent<GameBootstrap>();

            var cam = Camera.main != null ? Camera.main : Object.FindAnyObjectByType<Camera>();
            if (cam.GetComponent<IsometricCameraController>() == null)
            {
                cam.gameObject.AddComponent<IsometricCameraController>();
            }

            cam.orthographic = true;
            cam.GetComponent<IsometricCameraController>().ResetToIslandCenter();

            planting.Configure(plantData, cam, plantsRoot.transform, plots, catalog);
            online.Configure(planting);
            chunks.EnsureHomeChunkInScene(plots);
            save.Configure(planting, chunks);
            offline.Configure(save, planting, 1f, catalog, chunks);
            var growthConfig = WorldGrowthConfig.CreateDefault();
            worldGrowth.Configure(save, chunks, offline, planting, growthConfig);
            hud.Configure(offline);
            expansionHud.Configure(save, chunks, worldGrowth);
            meshViz.Configure(save, chunks);
            settleViz.Configure(save, chunks);
            growthDebug.Configure(save, chunks, meshViz, expansionHud, growthConfig, settleViz);
            animals.Configure(save, null, animalCatalog);
            weatherVisual.Configure(save);
            ecoHud.Configure(save);
            menu.Configure(save, bootstrap);
            var atmosphere = atmosphereRoot.GetComponent<WorldAtmosphereVfx>();
            var visualEnv = systems.GetComponent<WorldVisualEnvironment>();
            bootstrap.Configure(
                save, offline, hud, menu, planting, online, chunks, worldGrowth, expansionHud, meshViz, settleViz,
                growthDebug, reportHud, animals, weatherVisual, ecoHud, visualEnv, atmosphere);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[AFK Island] Stage 2 growth prototype ready.");
        }

        private static AnimalData EnsureAnimalData(
            string animalId,
            string displayNameRu,
            Color fallbackColor,
            float moveSpeed,
            float wanderRadius,
            Vector3 visualScale)
        {
            var path = $"{AnimalSoFolder}/AnimalData_{animalId}.asset";
            var data = AssetDatabase.LoadAssetAtPath<AnimalData>(path);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<AnimalData>();
                AssetDatabase.CreateAsset(data, path);
            }

            data.animalId = animalId;
            data.displayNameRu = displayNameRu;
            data.fallbackColor = fallbackColor;
            data.moveSpeed = moveSpeed;
            data.wanderRadius = wanderRadius;
            data.visualScale = visualScale;
            data.prefab = null;
            EditorUtility.SetDirty(data);
            return data;
        }

        private static void DirectoryEnsure(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var parts = path.Split('/');
            var cur = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(cur, parts[i]);
                }

                cur = next;
            }
        }

        private static GameObject MakePrimitivePrefab(string path, PrimitiveType type, Vector3 scale, Color color,
            float yOffset)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null)
            {
                return existing;
            }

            var go = GameObject.CreatePrimitive(type);
            go.name = System.IO.Path.GetFileNameWithoutExtension(path);
            go.transform.localScale = scale;
            go.transform.position = new Vector3(0f, yOffset * 0.5f, 0f);
            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                Object.DestroyImmediate(col);
            }

            var matFolder = "Assets/_Project/Materials/Plants";
            DirectoryEnsure(matFolder);
            var matPath = matFolder + "/" + System.IO.Path.GetFileNameWithoutExtension(path) + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader);
                if (mat.HasProperty("_BaseColor"))
                {
                    mat.SetColor("_BaseColor", color);
                }
                else
                {
                    mat.color = color;
                }

                AssetDatabase.CreateAsset(mat, matPath);
            }

            go.GetComponent<Renderer>().sharedMaterial = mat;
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        private static GameObject MakeScaledPrefab(string path, string sourcePrefabPath, float scale)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null)
            {
                return existing;
            }

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
            if (source == null)
            {
                return null;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            instance.name = System.IO.Path.GetFileNameWithoutExtension(path);
            instance.transform.localScale = Vector3.one * scale;
            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
            Object.DestroyImmediate(instance);
            return prefab;
        }
    }
}
#endif
