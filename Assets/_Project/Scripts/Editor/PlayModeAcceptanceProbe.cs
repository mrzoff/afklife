#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using AfkIsland.Core;
using AfkIsland.Save;
using AfkIsland.UI;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Stage 11G Play Mode acceptance helper. Opens MainIsland, installs Forest fixture,
    /// Continues, counts hut/tree visuals, writes Builds/playmode-acceptance-report.txt + PNG.
    /// Does not migrate Save v8 schema.
    /// </summary>
    public static class PlayModeAcceptanceProbe
    {
        private const string ScenePath = "Assets/_Project/Scenes/MainIsland.unity";
        private const string FixtureRelative = "Builds/save-forest-fixture.json";
        private static bool _pending;

        [MenuItem("AFK Island/Play Mode Acceptance Probe (11G)")]
        public static void RunMenu()
        {
            RunInternal(installForestFixture: true, reportName: "playmode-acceptance");
        }

        [MenuItem("AFK Island/Play Mode Acceptance Probe save056 (11G)")]
        public static void RunMenuSave056()
        {
            RunInternal(installForestFixture: false, reportName: "playmode-save056");
        }

        public static void RunBatchForest()
        {
            RunInternal(installForestFixture: true, reportName: "playmode-acceptance");
        }

        public static void RunBatchSave056()
        {
            RunInternal(installForestFixture: false, reportName: "playmode-save056");
        }

        internal static string ReportName { get; private set; } = "playmode-acceptance";

        private static void RunInternal(bool installForestFixture, string reportName)
        {
            ReportName = reportName;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("Stop Play Mode before starting the acceptance probe.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            if (installForestFixture)
            {
                if (!InstallForestFixtureToPersistent(out var installError))
                {
                    Debug.LogError("Forest fixture install failed: " + installError);
                    return;
                }
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError("Failed to open " + ScenePath);
                return;
            }

            _pending = true;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.isPlaying = true;
        }

        public static bool InstallForestFixtureToPersistent(out string error)
        {
            error = null;
            var root = Directory.GetParent(Application.dataPath)!.FullName;
            var fixturePath = Path.Combine(root, FixtureRelative);
            if (!File.Exists(fixturePath))
            {
                error = "missing " + fixturePath + " — run AFK Island/Build Forest Fixture (11G-B) first";
                return false;
            }

            var dest = Path.Combine(Application.persistentDataPath, "afk_island_save.json");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(fixturePath, dest, overwrite: true);
            var bak = dest + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }

            Debug.Log("[AFK Island] Forest fixture installed → " + dest);
            return true;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && _pending)
            {
                _pending = false;
                var host = new GameObject("__PlayModeAcceptanceProbe");
                host.hideFlags = HideFlags.DontSave;
                host.AddComponent<PlayModeAcceptanceRunner>();
                return;
            }

            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            }
        }
    }

    /// <summary>Runtime side of the probe. Editor Play Mode only.</summary>
    public sealed class PlayModeAcceptanceRunner : MonoBehaviour
    {
        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(2f);

            var saveService = FindAnyObjectByType<SaveService>();
            if (saveService != null && saveService.TryRead(out var diskSave) && diskSave != null)
            {
                // Daytime frame for hut readability (night made 0.56-scale huts nearly invisible).
                if (diskSave.weather == null)
                {
                    diskSave.weather = SaveMigrationService.CreateDefaultWeather(
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }

                diskSave.weather.timeOfDay01 = 0.5f;
                diskSave.weather.weatherId = "sunny";
                saveService.SetAuthoritative(diskSave);
            }

            var bootstrap = FindAnyObjectByType<GameBootstrap>();
            if (bootstrap != null)
            {
                bootstrap.ContinueGame();
                Debug.Log("[AFK Island] Play Mode probe called ContinueGame().");
            }
            else
            {
                Debug.LogWarning("[AFK Island] Play Mode probe: GameBootstrap not found.");
            }

            yield return new WaitForSecondsRealtime(2f);
            DismissReturnReportIfAny();

            // Offline AFK advances clock; force noon after Continue for hut readability shots.
            ForceDaytimeVisual(saveService);
            yield return new WaitForSecondsRealtime(8f);

            var report = new StringBuilder();
            report.AppendLine("=== Stage 11G Play Mode acceptance probe ===");
            report.AppendLine($"utc: {DateTime.UtcNow:o}");
            report.AppendLine($"unity: {Application.unityVersion}");
            report.AppendLine($"scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().path}");
            report.AppendLine($"persistentDataPath: {Application.persistentDataPath}");
            report.AppendLine($"bootstrap found: {bootstrap != null}");

            var auth = saveService != null ? saveService.AuthoritativeSnapshot : null;
            report.AppendLine($"authoritative plants: {auth?.plants?.Count ?? -1}");
            report.AppendLine($"authoritative hut cells: {CountHutCells(auth)}");

            var savePath = Path.Combine(Application.persistentDataPath, "afk_island_save.json");
            report.AppendLine($"disk save exists: {File.Exists(savePath)}");
            if (File.Exists(savePath))
            {
                try
                {
                    var save = JsonUtility.FromJson<GameSaveData>(File.ReadAllText(savePath));
                    report.AppendLine($"disk.version: {save?.version}");
                    report.AppendLine($"disk.plants: {save?.plants?.Count ?? -1}");
                    report.AppendLine($"disk hut cells: {CountHutCells(save)}");
                }
                catch (Exception ex)
                {
                    report.AppendLine("disk save parse error: " + ex.Message);
                }
            }

            CountSceneVisuals(out var huts, out var trees, out var decor, out var pathTiles, out var sample);
            report.AppendLine($"scene Hut_*: {huts}");
            report.AppendLine($"scene Tree_*: {trees}");
            report.AppendLine($"scene Decor_*: {decor}");
            report.AppendLine($"scene Path_*: {pathTiles}");
            report.Append(sample);
            report.AppendLine(
                $"option D scale: {SettlementCellQuery.HutVisualUniformScale} " +
                $"(target footprint {SettlementCellQuery.HutVisualFootprintMeters} m)");

            var mesh = FindAnyObjectByType<IslandMeshVisualizer>();
            var settle = FindAnyObjectByType<HomeSettlementVisualizer>();
            report.AppendLine($"IslandMeshVisualizer present: {mesh != null}");
            report.AppendLine($"HomeSettlementVisualizer present: {settle != null}");

            var root = Directory.GetParent(Application.dataPath)!.FullName;
            var stem = PlayModeAcceptanceProbe.ReportName;
            var reportPath = Path.Combine(root, $"Builds/{stem}-report.txt");
            var shotPath = Path.Combine(root, $"Builds/{stem}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            Debug.Log(report.ToString());

            ScreenCapture.CaptureScreenshot(shotPath);
            yield return null;
            yield return new WaitForSecondsRealtime(0.75f);
            report.AppendLine($"screenshot: {shotPath} exists={File.Exists(shotPath)}");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            Debug.Log("[AFK Island] Play Mode acceptance probe done → " + reportPath);

            EditorApplication.isPlaying = false;
        }

        private static int CountHutCells(GameSaveData save)
        {
            var hutMask = 0;
            if (save?.worldGrowth?.chunks == null)
            {
                return 0;
            }

            for (var c = 0; c < save.worldGrowth.chunks.Count; c++)
            {
                var g = save.worldGrowth.chunks[c];
                if (g == null ||
                    !ChunkMaskCodec.TryDecodeBase64(g.settlementMaskBase64, out var settle, out _))
                {
                    continue;
                }

                for (var i = 0; i < settle.Length; i++)
                {
                    if (settle[i] == (byte)SettlementStage.Hut)
                    {
                        hutMask++;
                    }
                }
            }

            return hutMask;
        }

        private static void CountSceneVisuals(
            out int huts, out int trees, out int decor, out int pathTiles, out StringBuilder sample)
        {
            huts = 0;
            trees = 0;
            decor = 0;
            pathTiles = 0;
            sample = new StringBuilder();
            var all = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (var i = 0; i < all.Length; i++)
            {
                var n = all[i].name;
                if (n.StartsWith("Hut_", StringComparison.Ordinal))
                {
                    huts++;
                    if (huts <= 5)
                    {
                        var t = all[i];
                        sample.AppendLine($"  hut sample: {n} scale={t.lossyScale} pos={t.position}");
                    }
                }
                else if (n.StartsWith("Tree_", StringComparison.Ordinal))
                {
                    trees++;
                }
                else if (n.StartsWith("Decor_", StringComparison.Ordinal))
                {
                    decor++;
                }
                else if (n.StartsWith("Path_", StringComparison.Ordinal))
                {
                    pathTiles++;
                }
            }
        }

        private static void DismissReturnReportIfAny()
        {
            var hud = FindAnyObjectByType<ReturnReportHud>();
            if (hud == null)
            {
                return;
            }

            var field = typeof(ReturnReportHud).GetField(
                "_onContinue", BindingFlags.Instance | BindingFlags.NonPublic);
            var cb = field?.GetValue(hud) as Action;
            hud.Hide();
            cb?.Invoke();
            Debug.Log("[AFK Island] Play Mode probe dismissed ReturnReportHud.");
        }

        private static void ForceDaytimeVisual(SaveService saveService)
        {
            var data = saveService != null ? saveService.AuthoritativeSnapshot : null;
            if (data?.weather == null)
            {
                return;
            }

            data.weather.timeOfDay01 = 0.5f;
            data.weather.weatherId = "sunny";
            saveService.SetAuthoritative(data);
            var visual = FindAnyObjectByType<DayNightWeatherVisual>();
            visual?.ApplyFromSave(data);
            Debug.Log("[AFK Island] Play Mode probe forced noon/sunny.");
        }
    }
}
#endif
