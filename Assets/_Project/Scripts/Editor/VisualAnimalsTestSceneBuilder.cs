#if UNITY_EDITOR
using AfkIsland.Animals;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace AfkIsland.EditorTools
{
    public static class VisualAnimalsTestSceneBuilder
    {
        private const string ScenePath = "Assets/_Project/Scenes/VisualAnimalsTest.unity";

        [MenuItem("AFK Island/Build Visual Animals Test Scene")]
        public static void BuildMenu()
        {
            BuildInternal(showDialog: true);
        }

        public static void BuildBatch()
        {
            try
            {
                BuildInternal(showDialog: false);
                EditorApplication.Exit(0);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AFK Island] VisualAnimalsTest build failed: " + ex);
                EditorApplication.Exit(1);
            }
        }

        private static void BuildInternal(bool showDialog)
        {
            AnimalVisualPrefabsBuilder.BuildAll();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.6f, 0.72f, 0.8f);

            var sunGo = new GameObject("Directional Light");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 4.5f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 80f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.48f, 0.72f, 0.86f);
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<UniversalAdditionalCameraData>();
            camGo.transform.position = new Vector3(0f, 6f, -6f);
            camGo.transform.rotation = Quaternion.Euler(42f, 0f, 0f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Decor_Grass";
            ground.transform.localScale = new Vector3(0.6f, 1f, 0.6f);
            var groundMat = CreateUrpMat("VisualTest_Grass", new Color(0.4f, 0.65f, 0.32f));
            ground.GetComponent<MeshRenderer>().sharedMaterial = groundMat;

            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "Decor_Water";
            water.transform.position = new Vector3(1.6f, -0.02f, -0.8f);
            water.transform.localScale = new Vector3(0.22f, 1f, 0.18f);
            water.GetComponent<MeshRenderer>().sharedMaterial =
                CreateUrpMat("VisualTest_Water", new Color(0.25f, 0.55f, 0.85f, 0.9f));

            PlaceDecor("Decor_Tree", "AfkIsland/Decor/LittleBamboo", new Vector3(-1.8f, 0f, 1.0f), 0.9f);
            PlaceDecor("Decor_Bush", "AfkIsland/Decor/Mugwort", new Vector3(0.9f, 0f, 1.2f), 1.1f);
            PlaceDecor("Decor_Bush_2", "AfkIsland/Decor/JapaneseLaurel", new Vector3(-0.5f, 0f, -1.3f), 0.85f);

            var harnessGo = new GameObject("VisualAnimalsTestHarness");
            var harness = harnessGo.AddComponent<VisualAnimalsTestHarness>();
            var so = new SerializedObject(harness);
            so.FindProperty("butterfly").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<AnimalData>(
                    "Assets/_Project/Resources/Animals/AnimalData_butterfly.asset");
            so.FindProperty("bird").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<AnimalData>(
                    "Assets/_Project/Resources/Animals/AnimalData_bird.asset");
            so.FindProperty("frog").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<AnimalData>(
                    "Assets/_Project/Resources/Animals/AnimalData_frog.asset");
            so.ApplyModifiedPropertiesWithoutUndo();

            if (!AssetDatabase.IsValidFolder("Assets/_Project/Scenes"))
            {
                AssetDatabase.CreateFolder("Assets/_Project", "Scenes");
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[AFK Island] VisualAnimalsTest saved → " + ScenePath);
            if (showDialog)
            {
                EditorUtility.DisplayDialog("AFK Island", "VisualAnimalsTest scene built.", "OK");
            }
        }

        private static void PlaceDecor(string name, string resourcesPath, Vector3 pos, float scale)
        {
            var prefab = Resources.Load<GameObject>(resourcesPath);
            GameObject go;
            if (prefab != null)
            {
                go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.name = name;
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = name + "_Fallback";
                Object.DestroyImmediate(go.GetComponent<Collider>());
            }

            go.transform.position = pos;
            go.transform.localScale = Vector3.one * scale;
        }

        private static Material CreateUrpMat(string name, Color color)
        {
            var folder = "Assets/_Project/Art/Animals/Materials";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                if (!AssetDatabase.IsValidFolder("Assets/_Project/Art"))
                {
                    AssetDatabase.CreateFolder("Assets/_Project", "Art");
                }

                if (!AssetDatabase.IsValidFolder("Assets/_Project/Art/Animals"))
                {
                    AssetDatabase.CreateFolder("Assets/_Project/Art", "Animals");
                }

                AssetDatabase.CreateFolder("Assets/_Project/Art/Animals", "Materials");
            }

            var path = folder + "/" + name + ".mat";
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

            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
#endif
