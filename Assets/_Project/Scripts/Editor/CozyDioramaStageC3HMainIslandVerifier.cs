using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AfkIsland.Core;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AfkIsland.Editor
{
    /// <summary>
    /// Stage C-3H — Real MainIsland.unity & real Save v8 verification + WouldDisconnectRoadNetwork performance benchmark.
    /// Opens Assets/_Project/Scenes/MainIsland.unity strictly read-only (never saves scene), reads the real user
    /// Save v8 strictly read-only via SaveClone.DeepClone (never writes disk save), verifies OFF vs ON baseline,
    /// controlled offline growth (+48h deterministic & +21d multi-session expansion), OFF->ON->OFF zero-leak rollback,
    /// and benchmarks WouldDisconnectRoadNetwork on real + scaled 3x3 and 5x5 saves.
    /// </summary>
    public static class CozyDioramaStageC3HMainIslandVerifier
    {
        private const string MainIslandScenePath = "Assets/_Project/Scenes/MainIsland.unity";
        private const string BuildShotsDir = "Builds/cozy-diorama/stage-c3h-shots";
        private const string DocsShotsDir = "Docs/CozyDiorama/StageC3H-Shots";
        private const string BuildReportPath = "Builds/cozy-diorama/stage-c3h-report.txt";
        private const string DocsReportPath = "Docs/CozyDiorama/StageC3H-Shots/c3h-verification-report.txt";

        private static readonly string[] ProtectedScenes =
        {
            "Assets/_Project/Scenes/MainIsland.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageB2.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC2.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3C.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3D.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3E.unity"
        };

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3H/Run MainIsland & Real Save Verification")]
        public static void RunVerificationMenu()
        {
            var ok = RunVerificationInternal();
            Debug.Log(ok
                ? "[AFK Island] Stage C-3H MainIsland verification PASSED. Report: " + DocsReportPath
                : "[AFK Island] Stage C-3H MainIsland verification FAILED. Report: " + DocsReportPath);
        }

        public static void RunBatchVerification()
        {
            var ok = RunVerificationInternal();
            Debug.Log("[AFK Island] Stage C-3H batch verification finished: ok=" + ok);
            if (!ok)
            {
                EditorApplication.Exit(1);
            }
        }

        private static bool RunVerificationInternal()
        {
            var inv = CultureInfo.InvariantCulture;
            var protectedBefore = new Dictionary<string, string>();
            foreach (var p in ProtectedScenes)
            {
                protectedBefore[p] = FileSha256Short(p);
            }

            var realSavePath = Path.Combine(Application.persistentDataPath, "afk_island_save.json");
            var realSaveExists = File.Exists(realSavePath);
            var realSaveDiskShaBefore = realSaveExists ? FileSha256Short(realSavePath) : "MISSING";

            Directory.CreateDirectory(BuildShotsDir);
            Directory.CreateDirectory(DocsShotsDir);
            Directory.CreateDirectory(Path.GetDirectoryName(BuildReportPath) ?? "Builds/cozy-diorama");

            var report = new StringBuilder();
            report.AppendLine("Stage C-3H — Real MainIsland.unity & Real Save v8 Verification Report");
            report.AppendLine("Execution mode: Read-only MainIsland.unity open (never saved) + in-memory SaveClone copies (zero disk save writes)");
            report.AppendLine($"Real save path: {realSavePath} (exists={realSaveExists}, diskShaBefore={realSaveDiskShaBefore})");

            // =========================================================================
            // STEP 1: Open REAL MainIsland.unity and inspect wiring + default flag
            // =========================================================================
            EditorSceneManager.OpenScene(MainIslandScenePath, OpenSceneMode.Single);

            var prototypeBuilder = UnityEngine.Object.FindAnyObjectByType<IslandPrototypeBuilder>();
            if (prototypeBuilder != null)
            {
                // Execute the same scene layout initialization MainIsland runs in Awake() without modifying the saved asset.
                var awakeMethod = typeof(IslandPrototypeBuilder).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
                awakeMethod?.Invoke(prototypeBuilder, null);
            }

            var bootstrap = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
            var saveService = UnityEngine.Object.FindAnyObjectByType<SaveService>();
            var chunkManager = UnityEngine.Object.FindAnyObjectByType<ChunkManager>();
            var growthService = UnityEngine.Object.FindAnyObjectByType<WorldGrowthService>();
            var islandMeshVisualizer = UnityEngine.Object.FindAnyObjectByType<IslandMeshVisualizer>();
            var homeVisualizer = UnityEngine.Object.FindAnyObjectByType<HomeSettlementVisualizer>();

            if (bootstrap != null)
            {
                chunkManager ??= bootstrap.GetComponent<ChunkManager>() ?? bootstrap.gameObject.AddComponent<ChunkManager>();
                islandMeshVisualizer ??= bootstrap.GetComponent<IslandMeshVisualizer>() ?? bootstrap.gameObject.AddComponent<IslandMeshVisualizer>();
                homeVisualizer ??= bootstrap.GetComponent<HomeSettlementVisualizer>() ?? bootstrap.gameObject.AddComponent<HomeSettlementVisualizer>();
            }

            var defaultFlagFalse = homeVisualizer != null && !homeVisualizer.UseContinuousPathNetworkRenderer;
            var waterSourceGo = GameObject.Find("WaterSource");
            var islandGo = GameObject.Find("Island");
            var settlementTf = islandGo != null ? islandGo.transform.Find("Settlement") : null;
            var plantPlotsTf = islandGo != null ? islandGo.transform.Find("PlantPlots") : null;
            var meadowTf = islandGo != null ? islandGo.transform.Find("CentralMeadow") : null;
            var hutAGo = settlementTf != null ? settlementTf.Find("Hut_A")?.gameObject : null;
            var hutBGo = settlementTf != null ? settlementTf.Find("Hut_B")?.gameObject : null;
            var pathMainGo = settlementTf != null ? settlementTf.Find("Path_Main")?.gameObject : null;
            var pathCrossGo = settlementTf != null ? settlementTf.Find("Path_Cross")?.gameObject : null;

            var step1Ok =
                bootstrap != null &&
                saveService != null &&
                chunkManager != null &&
                islandMeshVisualizer != null &&
                homeVisualizer != null &&
                defaultFlagFalse &&
                waterSourceGo != null;

            report.AppendLine();
            report.AppendLine($"Step 1 — Real MainIsland.unity Scene Wiring & Default Flag: ok={step1Ok}");
            report.AppendLine($"  bootstrap={bootstrap != null}, saveService={saveService != null}, chunkManager={chunkManager != null}, growthService={growthService != null}");
            report.AppendLine($"  islandMeshVisualizer={islandMeshVisualizer != null}, homeVisualizer={homeVisualizer != null}, defaultFlagFalse={defaultFlagFalse}");
            report.AppendLine($"  WaterSource={waterSourceGo != null} (pos={FormatVec3(waterSourceGo != null ? waterSourceGo.transform.position : Vector3.zero)}), PlantPlots={plantPlotsTf != null}, CentralMeadow={meadowTf != null} (pos={FormatVec3(meadowTf != null ? meadowTf.position : Vector3.zero)})");
            report.AppendLine($"  AuthoredSettlement={settlementTf != null}, Hut_A={hutAGo != null}, Hut_B={hutBGo != null}, Path_Main={pathMainGo != null}, Path_Cross={pathCrossGo != null}");

            if (!step1Ok)
            {
                FinalizeAndWrite(report, protectedBefore, realSavePath, realSaveDiskShaBefore, false);
                return false;
            }

            // Configure visualizers without binding SaveService disk I/O
            islandMeshVisualizer.Configure(null, chunkManager);
            homeVisualizer.Configure(null, chunkManager);

            // Load real Save v8 strictly read-only into memory
            GameSaveData realSave;
            if (realSaveExists)
            {
                var json = File.ReadAllText(realSavePath, Encoding.UTF8);
                realSave = JsonUtility.FromJson<GameSaveData>(json);
            }
            else
            {
                realSave = new GameSaveData
                {
                    version = 8,
                    lastSaveUtc = 1795687614L,
                    worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(1795687614L)
                };
                realSave.chunks.Add(new ChunkSaveData { chunkX = 0, chunkZ = 0, isUnlocked = true, biomeId = "starter_island" });
            }

            var realSaveCopy0 = SaveClone.DeepClone(realSave);
            SaveMigrationService.EnsureAllSettlementMasks(realSaveCopy0);
            var shaRealInitial = SaveJsonSha256(realSaveCopy0);
            SettlementSimulation.CountStages(realSaveCopy0, out var c0, out var p0, out var s0, out var h0);

            report.AppendLine();
            report.AppendLine($"Real Save v8 Snapshot (in-memory copy):");
            report.AppendLine($"  version={realSaveCopy0.version}, chunks={realSaveCopy0.worldGrowth?.chunks?.Count ?? 0}, lastSimUtc={realSaveCopy0.worldGrowth?.lastSimulationUtc}, settleWatermarkUtc={realSaveCopy0.worldGrowth?.settlementWatermarkUtc}");
            report.AppendLine($"  stages: clearings={c0}, paths={p0}, stones={s0}, huts={h0}, sha={shaRealInitial}");

            // Create clean verification camera (never mutates Main Camera on disk)
            var cam = CreateVerificationCamera();

            // =========================================================================
            // STEP 2: Current Real MainIsland.unity + Real Save v8 WITHOUT Growth (OFF vs ON)
            // =========================================================================
            islandMeshVisualizer.RebuildFromSave(realSaveCopy0);
            homeVisualizer.UseContinuousPathNetworkRenderer = false;
            homeVisualizer.RebuildFromSave(realSaveCopy0);
            var shaAfterOff0 = SaveJsonSha256(realSaveCopy0);
            RenderShot(cam, new Vector3(11.5f, 14.5f, -11.5f), new Vector3(0.2f, 1.4f, 0.2f), 38f, "c3h_01_mainisland_real_save_OFF.png");

            homeVisualizer.UseContinuousPathNetworkRenderer = true;
            homeVisualizer.RebuildFromSave(realSaveCopy0);
            var shaAfterOn0 = SaveJsonSha256(realSaveCopy0);
            RenderShot(cam, new Vector3(11.5f, 14.5f, -11.5f), new Vector3(0.2f, 1.4f, 0.2f), 38f, "c3h_02_mainisland_real_save_ON.png");
            RenderShot(cam, new Vector3(5.2f, 8.2f, -5.8f), new Vector3(0.5f, 1.8f, -0.1f), 40f, "c3h_03_mainisland_pond_and_huts_ON.png");

            var plan0 = homeVisualizer.LastContinuousPathPlan;
            var renderer0 = homeVisualizer.ContinuousPathRenderer;
            var stats0 = renderer0 != null ? renderer0.GetStats() : null;
            var build0 = renderer0 != null ? renderer0.LastBuildReport : null;
            var pond0 = CountCoreVerticesInsidePond(renderer0, WaterSourceLayout.GetWorldCenter(), WaterSourceLayout.WaterHalfExtentX, WaterSourceLayout.WaterHalfExtentZ);
            var legacyPaths0 = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");
            var authoredSuppressed0 = (pathMainGo == null || !pathMainGo.activeSelf) && (pathCrossGo == null || !pathCrossGo.activeSelf);
            var connectedHuts0 = CountConnectedHuts(plan0);
            var totalHuts0 = plan0 != null ? plan0.HutReports.Count : 0;

            var hasAuthoredSettlement = hutAGo != null || hutBGo != null || pathMainGo != null || pathCrossGo != null;
            var expectEmptyContinuous = !hasAuthoredSettlement && p0 == 0 && s0 == 0 && h0 == 0;
            var step2TopologyOk = expectEmptyContinuous
                ? (plan0 != null && plan0.Nodes.Count == 0 && plan0.Edges.Count == 0 && totalHuts0 == 0 && plan0.StarterIssues.Count == 0)
                : (plan0 != null && plan0.ConnectedComponents == 1 && connectedHuts0 == totalHuts0 && plan0.StarterIssues.Count == 0);

            var step2Ok =
                shaAfterOff0 == shaRealInitial &&
                shaAfterOn0 == shaRealInitial &&
                stats0 != null &&
                build0 != null &&
                step2TopologyOk &&
                pond0 == 0 &&
                legacyPaths0 == 0 &&
                authoredSuppressed0 &&
                build0.DownwardTriangles == 0 &&
                build0.FoldbackTriangles == 0 &&
                stats0.UsesSharedMaterialsOnly;

            report.AppendLine();
            report.AppendLine($"Step 2 — Real MainIsland.unity WITHOUT Growth (OFF vs ON): ok={step2Ok}");
            report.AppendLine($"  saveImmutability: off={shaAfterOff0 == shaRealInitial}, on={shaAfterOn0 == shaRealInitial}, noPhantomStarterPaths={expectEmptyContinuous && plan0 != null && plan0.Nodes.Count == 0}");
            report.AppendLine($"  authoredPathsSuppressed={authoredSuppressed0}, legacyPaths={legacyPaths0}, pondIntrusions={pond0}");
            report.AppendLine($"  topology: connectedComponents={plan0?.ConnectedComponents}, connectedHuts={connectedHuts0}/{totalHuts0}, nodes={plan0?.Nodes.Count}, edges={plan0?.Edges.Count}, starterIssues={plan0?.StarterIssues.Count}");
            report.AppendLine(string.Format(inv,
                "  geometry: chunks={0}, downwardTris={1}, foldbacks={2}, maxBorderPos={3:0.00000}m, sharedMaterialsOnly={4}, liveOwnedMeshes={5}",
                stats0?.ActiveChunkCount, build0?.DownwardTriangles, build0?.FoldbackTriangles,
                build0 != null ? build0.MaxBorderPositionDelta : -1f, stats0?.UsesSharedMaterialsOnly, stats0?.LiveOwnedMeshCount));

            // =========================================================================
            // STEP 3: Controlled Offline Growth on Clones of Real Save v8 (+48h deterministic, then +21d multi-session)
            // =========================================================================
            var sw48 = Stopwatch.StartNew();
            var grown48A = RunOfflineGrowthSession(realSaveCopy0, TimeSpan.FromHours(48), out var sim48A, out var settle48A);
            sw48.Stop();
            var grown48B = RunOfflineGrowthSession(realSaveCopy0, TimeSpan.FromHours(48), out var sim48B, out _);
            var sha48A = SaveJsonSha256(grown48A);
            var sha48B = SaveJsonSha256(grown48B);
            var det48 = sim48A != null && sim48A.success && sim48B != null && sim48B.success && sha48A == sha48B;

            islandMeshVisualizer.RebuildFromSave(grown48A);
            homeVisualizer.UseContinuousPathNetworkRenderer = true;
            homeVisualizer.RebuildFromSave(grown48A);
            var sha48AfterVisual = SaveJsonSha256(grown48A);
            var plan48 = homeVisualizer.LastContinuousPathPlan;
            var renderer48 = homeVisualizer.ContinuousPathRenderer;
            var build48 = renderer48 != null ? renderer48.LastBuildReport : null;
            var pond48 = CountCoreVerticesInsidePond(renderer48, WaterSourceLayout.GetWorldCenter(), WaterSourceLayout.WaterHalfExtentX, WaterSourceLayout.WaterHalfExtentZ);
            var legacy48 = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");
            var connectedHuts48 = CountConnectedHuts(plan48);
            var totalHuts48 = plan48 != null ? plan48.HutReports.Count : 0;
            SettlementSimulation.CountStages(grown48A, out var c48, out var p48, out var s48, out var h48);
            RenderShot(cam, new Vector3(13.5f, 17.5f, -13.5f), new Vector3(1.5f, 1.3f, 1.0f), 40f, "c3h_04_mainisland_after_48h_ON.png");

            // Continue controlled multi-session progression (+7d x 3 = +21d total) on real save clone to unlock & grow multiple chunks
            var grown21d = SaveClone.DeepClone(grown48A);
            var sessionSummaries = new List<string>();
            for (var s = 1; s <= 3; s++)
            {
                var swStep = Stopwatch.StartNew();
                grown21d = RunOfflineGrowthSession(grown21d, TimeSpan.FromDays(7), out var simStep, out var settleStep);
                swStep.Stop();
                SettlementSimulation.CountStages(grown21d, out var cs, out var ps, out var ss, out var hs);
                sessionSummaries.Add(string.Format(inv,
                    "session+{0}w: chunks={1}, clearings={2}, paths={3}, stones={4}, huts={5}, advances={6}, hutsAdded={7}, elapsed={8:0.00}ms",
                    s, grown21d.worldGrowth.chunks.Count, cs, ps, ss, hs, settleStep?.cellsAdvanced ?? 0, settleStep?.hutsAdded ?? 0, swStep.Elapsed.TotalMilliseconds));
            }

            var sha21BeforeVisual = SaveJsonSha256(grown21d);
            islandMeshVisualizer.RebuildFromSave(grown21d);
            homeVisualizer.UseContinuousPathNetworkRenderer = true;
            homeVisualizer.RebuildFromSave(grown21d);
            var sha21AfterVisual = SaveJsonSha256(grown21d);

            var plan21 = homeVisualizer.LastContinuousPathPlan;
            var renderer21 = homeVisualizer.ContinuousPathRenderer;
            var stats21 = renderer21 != null ? renderer21.GetStats() : null;
            var build21 = renderer21 != null ? renderer21.LastBuildReport : null;
            var pond21 = CountCoreVerticesInsidePond(renderer21, WaterSourceLayout.GetWorldCenter(), WaterSourceLayout.WaterHalfExtentX, WaterSourceLayout.WaterHalfExtentZ);
            var legacy21 = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");
            var connectedHuts21 = CountConnectedHuts(plan21);
            var totalHuts21 = plan21 != null ? plan21.HutReports.Count : 0;
            SettlementSimulation.CountStages(grown21d, out var c21, out var p21, out var s21, out var h21);
            RenderShot(cam, new Vector3(17.5f, 23.0f, -17.5f), new Vector3(2.5f, 1.2f, 2.0f), 42f, "c3h_05_mainisland_after_21d_grown_ON.png");

            // Also run deterministic check on a second copy of +21d
            var grown21dCheck = SaveClone.DeepClone(grown48B);
            for (var s = 1; s <= 3; s++)
            {
                grown21dCheck = RunOfflineGrowthSession(grown21dCheck, TimeSpan.FromDays(7), out _, out _);
            }
            var det21 = SaveJsonSha256(grown21dCheck) == sha21BeforeVisual;

            var step3Ok =
                det48 &&
                det21 &&
                sha48AfterVisual == sha48A &&
                sha21AfterVisual == sha21BeforeVisual &&
                plan48 != null && build48 != null &&
                plan48.StarterIssues.Count == 0 &&
                pond48 == 0 && legacy48 == 0 &&
                connectedHuts48 == totalHuts48 &&
                build48.DownwardTriangles == 0 && build48.FoldbackTriangles == 0 &&
                build48.MaxBorderPositionDelta <= 0.0001f &&
                plan21 != null && build21 != null && stats21 != null &&
                plan21.StarterIssues.Count == 0 &&
                pond21 == 0 && legacy21 == 0 &&
                connectedHuts21 == totalHuts21 &&
                build21.DownwardTriangles == 0 && build21.FoldbackTriangles == 0 &&
                build21.MaxBorderPositionDelta <= 0.0001f &&
                build21.MaxBorderHeightDelta <= 0.0001f &&
                stats21.UsesSharedMaterialsOnly;

            report.AppendLine();
            report.AppendLine($"Step 3 — Controlled Offline Growth on Real Save v8 Clones (+48h & +21d): ok={step3Ok}");
            report.AppendLine(string.Format(inv,
                "  +48h: deterministic={0} (sha={1}), chunks={2}, stages(C/P/S/H)={3}/{4}/{5}/{6}, advances={7}, elapsed={8:0.00}ms",
                det48, sha48A, grown48A.worldGrowth.chunks.Count, c48, p48, s48, h48, settle48A?.cellsAdvanced ?? 0, sw48.Elapsed.TotalMilliseconds));
            report.AppendLine(string.Format(inv,
                "  +48h visual: saveImmutable={0}, connectedComponents={1}, connectedHuts={2}/{3}, pondIntrusions={4}, legacyPaths={5}, foldbacks={6}, maxBorderPos={7:0.00000}m",
                sha48AfterVisual == sha48A, plan48?.ConnectedComponents, connectedHuts48, totalHuts48, pond48, legacy48, build48?.FoldbackTriangles,
                build48 != null ? build48.MaxBorderPositionDelta : -1f));
            for (var i = 0; i < sessionSummaries.Count; i++)
            {
                report.AppendLine("  " + sessionSummaries[i]);
            }
            report.AppendLine(string.Format(inv,
                "  +21d: deterministic={0} (sha={1}), chunks={2}, stages(C/P/S/H)={3}/{4}/{5}/{6}",
                det21, sha21BeforeVisual, grown21d.worldGrowth.chunks.Count, c21, p21, s21, h21));
            report.AppendLine(string.Format(inv,
                "  +21d visual: saveImmutable={0}, connectedComponents={1}, connectedHuts={2}/{3}, nodes={4}, edges={5}, starterIssues={6}, pondIntrusions={7}, legacyPaths={8}, downwardTris={9}, foldbacks={10}, maxBorderPos={11:0.00000}m, maxBorderHeight={12:0.00000}m",
                sha21AfterVisual == sha21BeforeVisual, plan21?.ConnectedComponents, connectedHuts21, totalHuts21,
                plan21?.Nodes.Count, plan21?.Edges.Count, plan21?.StarterIssues.Count, pond21, legacy21,
                build21?.DownwardTriangles, build21?.FoldbackTriangles,
                build21 != null ? build21.MaxBorderPositionDelta : -1f, build21 != null ? build21.MaxBorderHeightDelta : -1f));
            if (plan21 != null && plan21.ConnectedComponents > 1)
            {
                AppendComponentDiagnostics(report, plan21);
            }

            // =========================================================================
            // STEP 4: OFF -> ON -> OFF Toggle & Zero-Leak Rollback on Real MainIsland.unity
            // =========================================================================
            homeVisualizer.UseContinuousPathNetworkRenderer = false;
            homeVisualizer.RebuildFromSave(grown21d);
            homeVisualizer.UseContinuousPathNetworkRenderer = true;
            homeVisualizer.RebuildFromSave(grown21d);
            homeVisualizer.UseContinuousPathNetworkRenderer = false;
            homeVisualizer.RebuildFromSave(grown21d);
            var shaAfterToggle = SaveJsonSha256(grown21d);
            RenderShot(cam, new Vector3(17.5f, 23.0f, -17.5f), new Vector3(2.5f, 1.2f, 2.0f), 42f, "c3h_06_mainisland_rollback_OFF.png");

            var cleared = homeVisualizer.LastClearedRendererStats;
            var rootGone = homeVisualizer.ContinuousPathRenderer == null;
            var zeroLeaks = cleared != null && cleared.LiveOwnedMeshCount == 0 && cleared.TotalMeshesCreated == cleared.TotalMeshesDestroyed;
            var authoredRestored = (pathMainGo == null || pathMainGo.activeSelf) && (pathCrossGo == null || pathCrossGo.activeSelf);
            var legacyRestoredCount = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");

            var step4Ok =
                rootGone &&
                zeroLeaks &&
                authoredRestored &&
                shaAfterToggle == sha21BeforeVisual;

            report.AppendLine();
            report.AppendLine($"Step 4 — OFF -> ON -> OFF Toggle & Zero-Leak Rollback on MainIsland: ok={step4Ok}");
            report.AppendLine($"  rootGone={rootGone}, zeroLeaks={zeroLeaks} (created={cleared?.TotalMeshesCreated}, destroyed={cleared?.TotalMeshesDestroyed}, liveOwned={cleared?.LiveOwnedMeshCount})");
            report.AppendLine($"  authoredPathsRestored={authoredRestored}, legacyPathTilesRestored={legacyRestoredCount}, saveImmutable={shaAfterToggle == sha21BeforeVisual}");

            // =========================================================================
            // STEP 5: Performance Benchmark & Tarjan-vs-BFS Exact Equivalence
            // =========================================================================
            report.AppendLine();
            report.AppendLine("Step 5 — WouldDisconnectRoadNetwork vs Single-Pass Tarjan FindRoadArticulationPoints Benchmark:");
            var benchRealOk = BenchmarkSave(report, "Real Save v8 (+21d grown)", grown21d);
            var scaled3x3 = BuildStressGridSave(radius: 1); // 3x3 = 9 chunks
            var bench3x3Ok = BenchmarkSave(report, "Stress 3x3 (9 chunks mature network)", scaled3x3);
            var scaled5x5 = BuildStressGridSave(radius: 2); // 5x5 = 25 chunks
            var bench5x5Ok = BenchmarkSave(report, "Stress 5x5 (25 chunks late-game network)", scaled5x5);
            var step5Ok = benchRealOk && bench3x3Ok && bench5x5Ok;

            // Discard opened MainIsland scene without saving
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var allOk = step1Ok && step2Ok && step3Ok && step4Ok && step5Ok;
            return FinalizeAndWrite(report, protectedBefore, realSavePath, realSaveDiskShaBefore, allOk);
        }

        private static bool FinalizeAndWrite(
            StringBuilder report,
            Dictionary<string, string> protectedBefore,
            string realSavePath,
            string realSaveDiskShaBefore,
            bool coreOk)
        {
            var protectedOk = true;
            report.AppendLine();
            report.AppendLine("Step 6 — Disk Integrity (Protected Scenes & Real Save File Unchanged):");
            foreach (var p in ProtectedScenes)
            {
                var after = FileSha256Short(p);
                var same = string.Equals(protectedBefore[p], after, StringComparison.Ordinal);
                if (!same) protectedOk = false;
                report.AppendLine($"  {p}: before={protectedBefore[p]} after={after} unchanged={same}");
            }

            var realSaveDiskShaAfter = File.Exists(realSavePath) ? FileSha256Short(realSavePath) : "MISSING";
            var diskSaveUnchanged = string.Equals(realSaveDiskShaBefore, realSaveDiskShaAfter, StringComparison.Ordinal);
            if (!diskSaveUnchanged) protectedOk = false;
            report.AppendLine($"  realSaveDisk({realSavePath}): before={realSaveDiskShaBefore} after={realSaveDiskShaAfter} unchanged={diskSaveUnchanged}");

            var allOk = coreOk && protectedOk;
            report.AppendLine();
            report.AppendLine("OVERALL STAGE C-3H VERIFICATION: " + (allOk ? "PASS" : "FAIL"));

            var text = report.ToString();
            File.WriteAllText(BuildReportPath, text, Encoding.UTF8);
            File.WriteAllText(DocsReportPath, text, Encoding.UTF8);
            return allOk;
        }

        private static bool BenchmarkSave(StringBuilder report, string label, GameSaveData save)
        {
            var inv = CultureInfo.InvariantCulture;
            var roadTargets = new List<(int cx, int cz, int idx)>();
            var deg2Targets = new List<(int cx, int cz, int idx)>();
            var chunks = save?.worldGrowth?.chunks;
            if (chunks != null)
            {
                for (var c = 0; c < chunks.Count; c++)
                {
                    var g = chunks[c];
                    if (g == null || !ChunkMaskCodec.TryDecodeBase64(g.settlementMaskBase64, out var mask, out _))
                        continue;
                    for (var i = 0; i < mask.Length; i++)
                    {
                        if (mask[i] != (byte)SettlementStage.Path) continue;
                        roadTargets.Add((g.chunkX, g.chunkZ, i));
                        if (SettlementSimulation.CountCardinalRoadNeighbors(save, g.chunkX, g.chunkZ, i) == 2)
                        {
                            deg2Targets.Add((g.chunkX, g.chunkZ, i));
                        }
                    }
                }
            }

            // Warm-up JIT
            SettlementSimulation.FindRoadArticulationPoints(save);
            if (roadTargets.Count > 0)
            {
                var t0 = roadTargets[0];
                SettlementSimulation.WouldDisconnectRoadNetwork(save, t0.cx, t0.cz, t0.idx);
            }

            // Measure single-pass O(V+E) Tarjan articulation points (used in CollectCandidates)
            var swTarjan = Stopwatch.StartNew();
            var tarjanSet = SettlementSimulation.FindRoadArticulationPoints(save);
            swTarjan.Stop();

            // Measure per-cell BFS scan AND assert 100% exact equivalence with Tarjan
            var swAllBfs = Stopwatch.StartNew();
            var cutVertices = 0;
            var mismatches = 0;
            for (var i = 0; i < roadTargets.Count; i++)
            {
                var t = roadTargets[i];
                var bfsDisconnects = SettlementSimulation.WouldDisconnectRoadNetwork(save, t.cx, t.cz, t.idx);
                if (bfsDisconnects) cutVertices++;
                ChunkMaskCodec.IndexToLocal(t.idx, out var lx, out var lz);
                var worldKey = ((long)(t.cx * ChunkMaskCodec.GridSize + lx) << 32) ^ (uint)(t.cz * ChunkMaskCodec.GridSize + lz);
                var tarjanDisconnects = tarjanSet.Contains(worldKey);
                if (bfsDisconnects != tarjanDisconnects) mismatches++;
            }
            swAllBfs.Stop();

            // Measure full SettlementSimulation.Apply(+7d) on a clone (now using single-pass Tarjan)
            var applyClone = SaveClone.DeepClone(save);
            if (applyClone.worldGrowth != null)
            {
                applyClone.worldGrowth.lastSimulationUtc += 7 * 86400L;
            }
            var swApply = Stopwatch.StartNew();
            var applyRes = SettlementSimulation.Apply(applyClone, 7 * 86400L, new DeterministicRng(9876543u));
            swApply.Stop();

            var speedup = swTarjan.Elapsed.TotalMilliseconds > 0.0001
                ? swAllBfs.Elapsed.TotalMilliseconds / swTarjan.Elapsed.TotalMilliseconds
                : 0.0;
            report.AppendLine(string.Format(inv,
                "  {0}: chunks={1}, pathCells={2} (deg2={3}, cutVertices={4}), tarjanPass={5:0.000}ms vs bfsScan={6:0.00}ms ({7:0.0}x faster, mismatches={8}), fullApply(+7d)={9:0.00}ms (advances={10}, hutsAdded={11})",
                label, chunks?.Count ?? 0, roadTargets.Count, deg2Targets.Count, cutVertices,
                swTarjan.Elapsed.TotalMilliseconds, swAllBfs.Elapsed.TotalMilliseconds, speedup, mismatches,
                swApply.Elapsed.TotalMilliseconds, applyRes?.cellsAdvanced ?? 0, applyRes?.hutsAdded ?? 0));
            return mismatches == 0;
        }

        private static GameSaveData BuildStressGridSave(int radius)
        {
            var save = new GameSaveData
            {
                version = 8,
                lastSaveUtc = 1795687614L,
                worldGrowth = new WorldGrowthSaveData
                {
                    growthSeed = 555111,
                    lastSimulationUtc = 1795687614L,
                    settlementWatermarkUtc = 1795687614L,
                    chunks = new List<ChunkGrowthSaveData>()
                }
            };

            var landFull = new byte[ChunkMaskCodec.CellCount];
            var biomeFull = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < ChunkMaskCodec.CellCount; i++)
            {
                landFull[i] = (byte)CellStage.Vegetation;
                biomeFull[i] = (byte)BiomeType.Mossy;
            }

            var landB64 = ChunkMaskCodec.EncodeToBase64(landFull);
            var biomeB64 = BiomeMaskCodec.EncodeToBase64(biomeFull);

            for (var cz = -radius; cz <= radius; cz++)
            {
                for (var cx = -radius; cx <= radius; cx++)
                {
                    var settle = new byte[ChunkMaskCodec.CellCount];
                    // Continuous east-west highway at z=8 and north-south avenue at x=8 + side spur at x=4, z=4..8
                    for (var x = 0; x < 16; x++) settle[ChunkMaskCodec.CellIndex(x, 8)] = (byte)SettlementStage.Path;
                    for (var z = 0; z < 16; z++) settle[ChunkMaskCodec.CellIndex(8, z)] = (byte)SettlementStage.Path;
                    for (var z = 4; z <= 8; z++) settle[ChunkMaskCodec.CellIndex(4, z)] = (byte)SettlementStage.Path;

                    save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
                    {
                        chunkX = cx,
                        chunkZ = cz,
                        isLegacyBase = cx == 0 && cz == 0,
                        growthSeed = 1000 + cx * 31 + cz * 97,
                        stageMaskBase64 = landB64,
                        biomeMaskBase64 = biomeB64,
                        settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle)
                    });
                    save.chunks.Add(new ChunkSaveData { chunkX = cx, chunkZ = cz, isUnlocked = true, biomeId = "mossy" });
                }
            }

            return save;
        }

        private static GameSaveData RunOfflineGrowthSession(
            GameSaveData source,
            TimeSpan elapsed,
            out GrowthSimulationResult simResult,
            out SettlementSimulationResult settleResult)
        {
            var copy = SaveClone.DeepClone(source);
            var sim = new WorldGrowthSimulation();
            simResult = sim.Simulate(copy, elapsed, WorldGrowthConfig.CreateDefault());
            var working = simResult != null && simResult.success && simResult.updatedSave != null ? simResult.updatedSave : copy;
            settleResult = null;
            if (simResult != null && simResult.success)
            {
                SaveMigrationService.EnsureEcosystemWeatherAnimals(working);
                var rng = new DeterministicRng(unchecked((uint)(working.worldGrowth.growthSeed ^ (int)simResult.appliedSeconds ^ 0xEC05)));
                settleResult = SettlementSimulation.Apply(working, simResult.appliedSeconds, rng);
                SettlementSimulation.RebalanceExcessStones(working);
                working.worldGrowth.growthSeed = unchecked((int)rng.State);
            }
            return working;
        }

        private static int CountConnectedHuts(SettlementPathTopology.TopologyPlan plan)
        {
            if (plan == null) return 0;
            var count = 0;
            for (var i = 0; i < plan.HutReports.Count; i++)
            {
                var st = plan.HutReports[i].Status;
                if (st == SettlementPathTopology.HutConnectionStatus.Connected ||
                    st == SettlementPathTopology.HutConnectionStatus.ConnectedWithVisualDoorCorrection)
                {
                    count++;
                }
            }
            return count;
        }

        private static int CountNamedChildren(Transform root, string prefix)
        {
            if (root == null) return 0;
            var count = 0;
            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                for (var i = 0; i < cur.childCount; i++)
                {
                    var child = cur.GetChild(i);
                    if (child.name.StartsWith(prefix, StringComparison.Ordinal)) count++;
                    else stack.Push(child);
                }
            }
            return count;
        }

        private static int CountCoreVerticesInsidePond(SettlementPathNetworkRenderer renderer, Vector3 pondCenter, float rx, float rz)
        {
            if (renderer == null || renderer.LastBuildReport == null) return 0;
            var inside = 0;
            foreach (var kv in renderer.LastBuildReport.Chunks)
            {
                if (!renderer.TryGetChunkMeshes(kv.Key, out var coreMesh, out _) || coreMesh == null) continue;
                var origin = GrowthWorldPlacement.ChunkOriginXZ(kv.Key.x, kv.Key.y);
                var verts = coreMesh.vertices;
                for (var i = 0; i < verts.Length; i++)
                {
                    var dx = (origin.x + verts[i].x - pondCenter.x) / rx;
                    var dz = (origin.y + verts[i].z - pondCenter.z) / rz;
                    if (dx * dx + dz * dz < 0.96f) inside++;
                }
            }
            return inside;
        }

        private static void AppendComponentDiagnostics(StringBuilder report, SettlementPathTopology.TopologyPlan plan)
        {
            var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var byId = new Dictionary<string, SettlementPathTopology.PathNode>(StringComparer.Ordinal);
            for (var i = 0; i < plan.Nodes.Count; i++)
            {
                var n = plan.Nodes[i];
                byId[n.Id] = n;
                adj[n.Id] = new List<string>();
            }
            for (var i = 0; i < plan.Edges.Count; i++)
            {
                var e = plan.Edges[i];
                if (adj.ContainsKey(e.FromNodeId) && adj.ContainsKey(e.ToNodeId))
                {
                    adj[e.FromNodeId].Add(e.ToNodeId);
                    adj[e.ToNodeId].Add(e.FromNodeId);
                }
            }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var compIdx = 0;
            for (var i = 0; i < plan.Nodes.Count; i++)
            {
                var start = plan.Nodes[i];
                if (start.Degree == 0 || !visited.Add(start.Id)) continue;
                compIdx++;
                var comp = new List<string>();
                var q = new Queue<string>();
                q.Enqueue(start.Id);
                while (q.Count > 0)
                {
                    var cur = q.Dequeue();
                    var p = byId[cur].Position;
                    comp.Add($"{cur}({p.x:0.0},{p.y:0.0})");
                    foreach (var next in adj[cur])
                    {
                        if (visited.Add(next)) q.Enqueue(next);
                    }
                }
                report.AppendLine($"    component[{compIdx}] ({comp.Count} nodes): " + string.Join(", ", comp));
            }
        }

        private static Camera CreateVerificationCamera()
        {
            var camGo = new GameObject("StageC3H_VerifyCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.36f, 0.68f, 0.84f);
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 220f;
            cam.fieldOfView = 40f;
            return cam;
        }

        private static void RenderShot(Camera cam, Vector3 pos, Vector3 target, float fov, string fileName)
        {
            const int width = 1280;
            const int height = 720;
            cam.transform.position = pos;
            cam.transform.LookAt(target);
            cam.fieldOfView = fov;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                var png = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);
                File.WriteAllBytes(Path.Combine(BuildShotsDir, fileName), png);
                File.WriteAllBytes(Path.Combine(DocsShotsDir, fileName), png);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private static string SaveJsonSha256(GameSaveData data)
        {
            var json = JsonUtility.ToJson(data, prettyPrint: false);
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
                var sb = new StringBuilder(16);
                for (var i = 0; i < 8; i++) sb.Append(bytes[i].ToString("X2"));
                return sb.ToString();
            }
        }

        private static string FileSha256Short(string path)
        {
            if (!File.Exists(path)) return "MISSING";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(File.ReadAllBytes(path));
                var sb = new StringBuilder(16);
                for (var i = 0; i < 8; i++) sb.Append(hash[i].ToString("X2"));
                return sb.ToString();
            }
        }

        private static string FormatVec3(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.00},{1:0.00},{2:0.00})", v.x, v.y, v.z);
    }
}
