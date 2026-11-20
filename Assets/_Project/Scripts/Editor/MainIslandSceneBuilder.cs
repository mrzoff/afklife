#if UNITY_EDITOR
using AfkIsland.CameraControl;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace AfkIsland.EditorTools
{
    public static class MainIslandSceneBuilder
    {
        private const string ScenePath = "Assets/_Project/Scenes/MainIsland.unity";

        [MenuItem("AFK Island/Build MainIsland Scene (Stage 1)")]
        public static void Build()
        {
            BuildInternal(showDialog: true);
        }

        /// <summary>
        /// Batchmode entry: Unity.exe -batchmode -quit -projectPath ... -executeMethod AfkIsland.EditorTools.MainIslandSceneBuilder.BuildBatch
        /// </summary>
        public static void BuildBatch()
        {
            try
            {
                BuildInternal(showDialog: false);
                EditorApplication.Exit(0);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[AFK Island] Stage 1 build failed: {ex}");
                EditorApplication.Exit(1);
            }
        }

        private static void BuildInternal(bool showDialog)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.68f, 0.78f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.55f, 0.72f, 0.82f);
            RenderSettings.fogDensity = 0.008f;

            var sunGo = new GameObject("Directional Light");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.intensity = 1.2f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(45f, -35f, 0f);

            var islandRoot = new GameObject("World");
            var builder = islandRoot.AddComponent<IslandPrototypeBuilder>();
            builder.Build();

            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 14f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 200f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.45f, 0.70f, 0.85f);
            cameraGo.AddComponent<AudioListener>();
            cameraGo.AddComponent<UniversalAdditionalCameraData>();
            var camCtrl = cameraGo.AddComponent<IsometricCameraController>();
            camCtrl.ResetToIslandCenter();

            if (!AssetDatabase.IsValidFolder("Assets/_Project"))
            {
                AssetDatabase.CreateFolder("Assets", "_Project");
            }

            if (!AssetDatabase.IsValidFolder("Assets/_Project/Scenes"))
            {
                AssetDatabase.CreateFolder("Assets/_Project", "Scenes");
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings(ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AFK Island] Stage 1 scene saved: {ScenePath}");
            if (showDialog)
            {
                EditorUtility.DisplayDialog(
                    "AFK Island — Stage 1",
                    "Сцена MainIsland создана.\n\nОстров-прототип + океан + изометрическая камера.\nPlay Mode: ЛКМ — панорама, колесо — зум.",
                    "OK");
            }
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes;
            foreach (var s in scenes)
            {
                if (s.path == scenePath)
                {
                    return;
                }
            }

            var list = new EditorBuildSettingsScene[scenes.Length + 1];
            for (var i = 0; i < scenes.Length; i++)
            {
                list[i] = scenes[i];
            }

            list[scenes.Length] = new EditorBuildSettingsScene(scenePath, true);
            EditorBuildSettings.scenes = list;
        }
    }
}
#endif
