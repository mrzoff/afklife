#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace AfkIsland.EditorTools
{
    public static class AndroidBuild
    {
        public const string OutputApk = "Builds/Android/AFKIsland.apk";

        [MenuItem("AFK Island/Build Android APK")]
        public static void BuildApkMenu()
        {
            var code = BuildApkInternal();
            if (code != 0)
            {
                throw new System.Exception("Android build failed with code " + code);
            }
        }

        public static void BuildApkBatch()
        {
            var code = BuildApkInternal();
            EditorApplication.Exit(code);
        }

        private static int BuildApkInternal()
        {
            // Same recipe that installed in BlueStacks for poka-tebya-net:
            // IL2CPP + ARM64 + Activity (not GameActivity), targetSdk 34, GLES3/Vulkan.
            PlayerSettings.companyName = "AFKIsland";
            PlayerSettings.productName = "AFK Island";
            // Stage 0.3 fishing (Stages 5–6): upgrade over vc37 without wipe.
            // Stage 9.3 Wind unify (planted + authored tree use VegetationWind).
            // CIF 0.1 + Stage 10B pond input (overlay over vc49).
            // Stage 11E: solid terrain mats on Android + camera recenter; no decor without mesh.
            // Stage 11E-1: WorldAtmosphereVfx on isolated root (GameSystems/Plants no longer follow camera).
            // Stage 11E-2/3: tree LOD0 holds through ortho zoom-out; foliage mips keep alpha coverage.
            // Stage 11E-4: VegetationWind clips alpha only on cutout sources (leaf cards).
            // Stage 11F-A: day/night only — no fog, no weather light tint, no rain particles.
            // Stage 11F-B: grown land has a hit mesh so grass taps do not fall through to ocean fishing.
            // Plant taps follow the pressed touch slot, same as pond/ocean (BlueStacks primaryTouch stays idle).
            // Keep authored PlantPlots: the settlement upgrade was destroying them on every launch.
            // Stage 11F-C: adult oak/cedar keep the leafy mesh through ortho zoom-out (no brown LOD wall).
            // Stage 11F-D: fog is a distant haze, rain is particles. Light stays day/night only.
            // Leaf slots use the same VegetationWind shader as the trunk. Sway formula unchanged.
            // Stage 11F-E: BlueStacks wheel is Android AXIS_VSCROLL, not the legacy Input manager.
            // Stage 11E-2: cedar pivot, double-sided leaves, pond trunk ellipse, tall spacing.
            // 0.6.46: settlement and home fauna use the same pond reserve. LOD and sway unchanged.
            // 0.6.47: cedar mesh was already on the trunk; the 10.3 m child offset is removed.
            // 0.6.48: authored NaturePackProps pine stood in the pond. LOD and sway unchanged.
            // 0.6.49: tall decor and wild plants skip a hut cell. Coordinates and save version stay.
            // 0.6.50: bushes and plants also skip a hut cell (roof stays readable). Save version stays 8.
            // 0.6.51: chunk decor gets deterministic in-cell jitter so props stop lining up on a grid.
            // 0.6.52: Stage 13 residents derived from hut cells. Save version stays 8.
            // 0.6.53: residents stand on the nearest road cell, not inside the hut.
            // 0.6.54: log stand point status for verification.
            // 0.6.55: stand point rings outward when all neighbours are huts.
            // 0.6.56: Stage 13 editor smoke (roster, graph, director, save contract). No runtime change.
            // 0.6.57: Stage 11G-B scatter + visual fixes + hut option D (scale 0.62). Save v8 unchanged.
            PlayerSettings.bundleVersion = "0.6.57";
            PlayerSettings.Android.bundleVersionCode = 87;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.afkisland.game");
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.optimizedFramePacing = false;
            PlayerSettings.Android.blitType = AndroidBlitType.Auto;
            PlayerSettings.stripEngineCode = false;
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Minimal);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[]
            {
                GraphicsDeviceType.OpenGLES3,
                GraphicsDeviceType.Vulkan
            });

            EditorUserBuildSettings.SwitchActiveBuildTarget(NamedBuildTarget.Android, BuildTarget.Android);
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;

            var outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", OutputApk));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            // Build to a temp APK so a killed mid-build doesn't wipe the last good install.
            var tempPath = outPath + ".building.apk";
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            var scenes = new[] { "Assets/_Project/Scenes/MainIsland.unity" };
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = tempPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            Debug.Log($"[AFK Island] building Android APK (IL2CPP ARM64 Activity) -> {tempPath}");
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[AFK Island] Android build result: {report.summary.result} -> {tempPath}");
            if (report.summary.result != BuildResult.Succeeded)
            {
                return 1;
            }

            if (File.Exists(outPath))
                File.Delete(outPath);
            File.Move(tempPath, outPath);
            Debug.Log($"[AFK Island] APK published -> {outPath}");
            return 0;
        }
    }
}
#endif
