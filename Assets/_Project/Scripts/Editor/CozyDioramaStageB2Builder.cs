#if UNITY_EDITOR
using System.Collections.Generic;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Stage B-2 composition review. Builds an isolated miniature-island scene using existing
    /// prefabs and the approved V2 tree-to-hut height ratios. No production systems are loaded.
    /// </summary>
    public static class CozyDioramaStageB2Builder
    {
        public const string ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageB2.unity";
        private const string Decor = "Assets/_Project/Resources/AfkIsland/Decor/";
        private const string Settlement = "Assets/_Project/Resources/AfkIsland/Settlement/";
        private const string Polyeler = "Assets/Polyeler/EssentialNaturePack/Prefabs/";

        private struct TreeSpec
        {
            public string Name;
            public string Path;
            public int HeightClass;
            public TreeSpec(string name, string path, int heightClass)
            { Name = name; Path = path; HeightClass = heightClass; }
        }

        private static readonly TreeSpec[] Trees =
        {
            new("SawtoothOak", Decor + "SawtoothOak.prefab", 0),
            new("Ginkgo_wood", Polyeler + "Ginkgo_wood.prefab", 0),
            new("CherryBlossom", Polyeler + "CherryBlossom.prefab", 1),
            new("PlaneTree", Polyeler + "PlaneTree.prefab", 1),
            new("Pine", Decor + "Pine.prefab", 1),
            new("Bamboo", Decor + "Bamboo.prefab", 1),
            new("DawnRedwood", Polyeler + "DawnRedwood.prefab", 2),
            new("JapaneseCedar", Decor + "JapaneseCedar.prefab", 2),
            new("JapaneseCypress", Decor + "JapaneseCypress.prefab", 2),
        };

        // V2 = midpoint of the approved per-class ranges: 1.75H / 2.25H / 3.00H.
        private static readonly float[] V2Ratios = { 1.75f, 2.25f, 3.00f };
        private static readonly Dictionary<string, Bounds> NativeTreeBounds = new();
        private static float _hutHeight;

        [MenuItem("AFK Island/Cozy Diorama/Stage B-2/Build Composition Review Scene")]
        public static void BuildMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            try
            {
                BuildScene();
                Debug.Log("[AFK Island] Stage B-2 scene saved: " + ScenePath);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AFK Island] Stage B-2 build failed: " + ex);
                throw;
            }
        }

        private static void BuildScene()
        {
            MeasureReferences();
            CozyDioramaPrototypeSceneBuilder.EnsureFolder("Assets/_Project", "Scenes");
            CozyDioramaPrototypeSceneBuilder.EnsureFolder("Assets/_Project/Scenes", "Prototypes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CozyDioramaPrototypeSceneBuilder.ConfigureEnvironment();
            CozyDioramaPrototypeSceneBuilder.CreateSun();

            var root = new GameObject("CozyDioramaStageB2");
            var ground = new GameObject("Island");
            ground.transform.SetParent(root.transform, false);
            BuildIslandMesh(ground.transform);

            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "Water_Placeholder";
            water.transform.SetParent(root.transform, false);
            water.transform.position = new Vector3(0f, -0.62f, 0f);
            water.transform.localScale = new Vector3(12f, 1f, 12f);
            Object.DestroyImmediate(water.GetComponent<Collider>());
            water.GetComponent<Renderer>().sharedMaterial =
                CozyDioramaPrototypeSceneBuilder.TempMaterial("Cozy_WaterPlaceholder",
                    CozyDioramaPrototypeSceneBuilder.Hex("#579EB3"));

            var settlement = new GameObject("Settlement");
            settlement.transform.SetParent(root.transform, false);
            PlaceHut(settlement.transform, "Hut_A", new Vector3(-1.65f, 0f, -0.15f), -145f);
            PlaceHut(settlement.transform, "Hut_B", new Vector3(1.35f, 0f, 0.65f), -115f);
            PlacePath(settlement.transform, new Vector3(-0.7f, 0f, -1.0f), -35f);
            PlacePath(settlement.transform, new Vector3(0.15f, 0f, -1.55f), -20f);
            PlacePath(settlement.transform, new Vector3(1.0f, 0f, -1.85f), 5f);
            PlacePath(settlement.transform, new Vector3(1.8f, 0f, -1.55f), 35f);
            PlaceDecor(settlement.transform, Decor + "Rock_A.prefab", new Vector3(-2.8f, 0f, -1.0f), 0.32f, 25f);
            PlaceDecor(settlement.transform, Decor + "Rock_B.prefab", new Vector3(2.7f, 0f, -0.9f), 0.30f, 70f);

            var vegetation = new GameObject("Vegetation_V2");
            vegetation.transform.SetParent(root.transform, false);
            // Rear arc frames the clearing; side clusters leave both huts and the center readable.
            var positions = new[]
            {
                new Vector3(-4.5f,0f,3.0f), new Vector3(-3.2f,0f,4.0f),
                new Vector3(-1.7f,0f,4.45f), new Vector3(0.1f,0f,4.55f),
                new Vector3(1.9f,0f,4.15f), new Vector3(3.55f,0f,3.2f),
                new Vector3(4.45f,0f,1.75f), new Vector3(-4.75f,0f,0.9f),
                new Vector3(4.65f,0f,-0.25f)
            };
            for (var i = 0; i < Trees.Length; i++)
            {
                var p = positions[i];
                PlaceTree(vegetation.transform, Trees[i], p, i * 43f);
            }

            // Low vegetation transitions the grove into the open settlement clearing.
            PlaceDecor(vegetation.transform, Decor + "JapaneseLaurel.prefab", new Vector3(-3.3f,0f,1.7f), 0.40f, 20f);
            PlaceDecor(vegetation.transform, Decor + "JapaneseEurya.prefab", new Vector3(3.3f,0f,1.55f), 0.40f, 110f);
            PlaceDecor(vegetation.transform, Decor + "Mugwort.prefab", new Vector3(-3.4f,0f,-2.0f), 0.42f, 55f);
            PlaceDecor(vegetation.transform, Decor + "ViolaGrypoceras.prefab", new Vector3(3.25f,0f,-2.25f), 0.44f, 180f);
            PlaceDecor(vegetation.transform, Decor + "LittleBamboo.prefab", new Vector3(4.0f,0f,0.9f), 0.42f, 235f);

            CreateCamera(root.transform);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static void MeasureReferences()
        {
            NativeTreeBounds.Clear();
            var hut = AssetDatabase.LoadAssetAtPath<GameObject>(Settlement + "Hut_A.prefab");
            if (hut == null) throw new System.InvalidOperationException("Missing prefab: " + Settlement + "Hut_A.prefab");
            var hutInstance = (GameObject)PrefabUtility.InstantiatePrefab(hut);
            try
            {
                hutInstance.transform.localScale = Vector3.one * SettlementCellQuery.HutVisualUniformScale;
                _hutHeight = MeasureBounds(hutInstance).size.y;
            }
            finally { Object.DestroyImmediate(hutInstance); }

            foreach (var tree in Trees)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(tree.Path);
                if (prefab == null) throw new System.InvalidOperationException("Missing tree prefab: " + tree.Path);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                try
                {
                    instance.transform.localScale = Vector3.one;
                    NativeTreeBounds[tree.Name] = MeasureLod0Bounds(instance);
                }
                finally { Object.DestroyImmediate(instance); }
            }
            if (_hutHeight <= 0f) throw new System.InvalidOperationException("Could not measure hut height.");
        }

        private static Bounds MeasureBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new System.InvalidOperationException("Prefab has no renderers: " + go.name);
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static Bounds MeasureLod0Bounds(GameObject go)
        {
            var group = go.GetComponent<LODGroup>();
            var renderers = new List<Renderer>();
            if (group != null)
            {
                var lods = group.GetLODs();
                if (lods.Length > 0 && lods[0].renderers != null)
                    foreach (var r in lods[0].renderers) if (r != null) renderers.Add(r);
            }
            if (renderers.Count == 0) renderers.AddRange(go.GetComponentsInChildren<Renderer>(true));
            if (renderers.Count == 0) throw new System.InvalidOperationException("Tree has no renderers: " + go.name);
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Count; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static void PlaceTree(Transform parent, TreeSpec spec, Vector3 groundPosition, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(spec.Path);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = spec.Name + "_V2";
            instance.transform.SetParent(parent, true);
            var native = NativeTreeBounds[spec.Name];
            var scale = V2Ratios[spec.HeightClass] * _hutHeight / native.size.y;
            instance.transform.localScale = Vector3.one * scale;
            instance.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            var scaled = MeasureLod0Bounds(instance);
            instance.transform.position = groundPosition + Vector3.up * (scaled.extents.y - scaled.center.y);
        }

        private static void PlaceHut(Transform parent, string name, Vector3 position, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Settlement + name + ".prefab");
            if (prefab == null) throw new System.InvalidOperationException("Missing hut prefab: " + name);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = name;
            instance.transform.SetParent(parent, true);
            instance.transform.localScale = Vector3.one * SettlementCellQuery.HutVisualUniformScale;
            instance.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            var bounds = MeasureBounds(instance);
            instance.transform.position = position - Vector3.up * bounds.min.y;
        }

        private static void PlacePath(Transform parent, Vector3 position, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Settlement + "Path_Tile.prefab");
            if (prefab == null) return;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = "Path_Tile";
            instance.transform.SetParent(parent, true);
            instance.transform.position = position;
            instance.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        private static void PlaceDecor(Transform parent, string path, Vector3 position, float scale, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) return;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.SetParent(parent, true);
            instance.transform.localScale = Vector3.one * scale;
            instance.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            var bounds = MeasureBounds(instance);
            instance.transform.position = position - Vector3.up * bounds.min.y;
        }

        private static void BuildIslandMesh(Transform parent)
        {
            const int segments = 40;
            const float topY = 0f;
            const float bottomY = -0.48f;
            var vertices = new Vector3[segments * 2 + 1];
            var triangles = new List<int>(segments * 9);
            vertices[0] = new Vector3(0f, topY, 0f);
            for (var i = 0; i < segments; i++)
            {
                var a = Mathf.PI * 2f * i / segments;
                var wave = 1f + 0.075f * Mathf.Sin(3f * a + 0.4f) + 0.045f * Mathf.Sin(7f * a - 0.8f);
                var x = Mathf.Cos(a) * 5.9f * wave;
                var z = Mathf.Sin(a) * 4.75f * wave;
                vertices[1 + i] = new Vector3(x, topY, z);
                vertices[1 + segments + i] = new Vector3(x * 0.96f, bottomY, z * 0.96f);
                var next = (i + 1) % segments;
                triangles.Add(0); triangles.Add(1 + i); triangles.Add(1 + next);
                var topA = 1 + i; var topB = 1 + next;
                var lowA = 1 + segments + i; var lowB = 1 + segments + next;
                triangles.Add(topA); triangles.Add(lowA); triangles.Add(topB);
                triangles.Add(topB); triangles.Add(lowA); triangles.Add(lowB);
            }
            // Reverse winding so the top surface normals face upward and the island wall faces outward.
            for (var i = 0; i < triangles.Count; i += 9)
            {
                var a = triangles[i + 1];
                triangles[i + 1] = triangles[i + 2];
                triangles[i + 2] = a;
                var b = triangles[i + 3];
                triangles[i + 3] = triangles[i + 5];
                triangles[i + 5] = b;
                var c = triangles[i + 6];
                triangles[i + 6] = triangles[i + 8];
                triangles[i + 8] = c;
            }

            const string meshPath = "Assets/_Project/Art/CozyDiorama/CozyDioramaStageB2_IslandMesh.asset";
            CozyDioramaPrototypeSceneBuilder.EnsureFolder("Assets/_Project", "Art");
            CozyDioramaPrototypeSceneBuilder.EnsureFolder("Assets/_Project/Art", "CozyDiorama");
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh == null)
            {
                mesh = new Mesh { name = "CozyDioramaStageB2_IslandMesh" };
                AssetDatabase.CreateAsset(mesh, meshPath);
            }
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);

            var go = new GameObject("Island_Ground");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial =
                CozyDioramaPrototypeSceneBuilder.TempMaterial("Cozy_ReviewSlab",
                    CozyDioramaPrototypeSceneBuilder.Hex("#78945B"));
        }

        private static void CreateCamera(Transform parent)
        {
            var go = new GameObject("StageB2_Camera") { tag = "MainCamera" };
            var camera = go.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 12.8f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 150f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = CozyDioramaPrototypeSceneBuilder.Hex("#9CC4D0");
            go.AddComponent<AudioListener>();
            go.AddComponent<UniversalAdditionalCameraData>();
            go.transform.rotation = Quaternion.Euler(45f, 45f, 0f);
            go.transform.position = new Vector3(0f, 1.2f, 0f) + go.transform.rotation * new Vector3(0f, 0f, -28f);
            go.transform.SetParent(parent, true);
        }
    }
}
#endif
