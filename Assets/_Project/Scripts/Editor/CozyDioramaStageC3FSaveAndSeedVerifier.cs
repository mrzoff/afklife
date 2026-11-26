using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace AfkIsland.Editor
{
    /// <summary>
    /// Stage C-3F automated verification suite on an isolated Save v8 copy and empty temporary scene:
    /// 1. Confirms CozyDioramaStageC3FMainIslandDiagnostic compiles and guards Edit Mode cleanly.
    /// 2. Verifies byte-for-byte Save v8 JSON immutability before/after legacy & continuous visual rebuilds.
    /// 3. Verifies seed-only invalidation (commit 0336dd4): mutating only growthSeed updates hut yaw and
    ///    path branches deterministically without modifying Save v8 or untouched chunks.
    /// 4. Exercises isolated in-memory offline growth (WorldGrowthSimulation + SettlementSimulation) +
    ///    visual rebuild and verifies Save v8 immutability, zero leaks, and seamless chunk borders.
    /// 5. Exercises chunk add / remove / reload lifecycle and OFF rollback with zero Mesh leaks.
    /// Does NOT touch MainIsland.unity, persistent disk saves, or the default feature flag (false).
    /// </summary>
    public static class CozyDioramaStageC3FSaveAndSeedVerifier
    {
        private const string BuildShotsDir = "Builds/cozy-diorama/stage-c3f-shots";
        private const string DocsShotsDir = "Docs/CozyDiorama/StageC3F-Shots";
        private const string BuildReportPath = "Builds/cozy-diorama/stage-c3f-report.txt";
        private const string DocsReportPath = "Docs/CozyDiorama/StageC3F-Shots/c3f-verification-report.txt";
        private const string MaterialsFolder = "Assets/_Project/Art/CozyDiorama/Materials";

        private static readonly string[] ProtectedScenes =
        {
            "Assets/_Project/Scenes/MainIsland.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageB2.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC2.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3C.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3D.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3E.unity"
        };

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3F/Run Save v8 Immutability & Seed Invalidation Suite")]
        public static void RunVerificationMenu()
        {
            var ok = RunVerificationInternal();
            Debug.Log(ok
                ? "[AFK Island] Stage C-3F automated Save v8 & seed verification PASSED. Report: " + DocsReportPath
                : "[AFK Island] Stage C-3F automated Save v8 & seed verification FAILED. Report: " + DocsReportPath);
        }

        public static void RunBatchVerification()
        {
            var ok = RunVerificationInternal();
            Debug.Log("[AFK Island] Stage C-3F batch verification finished: ok=" + ok);
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

            Directory.CreateDirectory(BuildShotsDir);
            Directory.CreateDirectory(DocsShotsDir);
            Directory.CreateDirectory(Path.GetDirectoryName(BuildReportPath) ?? "Builds/cozy-diorama");

            var report = new StringBuilder();
            report.AppendLine("Stage C-3F — Save v8 Immutability, Seed-Only Invalidation & Offline Growth Verification Report");
            report.AppendLine("Execution mode: Isolated empty editor scene + in-memory SaveClone copies (zero disk save writes)");

            // =========================================================================
            // TEST 1: Diagnostic Edit-Mode safety guard
            // =========================================================================
            var diagEditModeSafe = true;
            try
            {
                CozyDioramaStageC3FMainIslandDiagnostic.ReportRuntimeWiring();
            }
            catch (Exception ex)
            {
                diagEditModeSafe = false;
                report.AppendLine("  Diagnostic exception: " + ex);
            }
            report.AppendLine($"Test 1 — Diagnostic Compile & Edit-Mode Guard: ok={diagEditModeSafe}");

            // Build fresh isolated test scene (never saved over MainIsland or C-3E scene)
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "StageC3F_TempVerification";

            var matOcean = LoadOrTempMat(MaterialsFolder + "/Cozy_C3E_Ocean.mat", new Color(0.28f, 0.64f, 0.78f));
            var matBaseRock = LoadOrTempMat(MaterialsFolder + "/Cozy_C3E_BaseRock.mat", new Color(0.45f, 0.40f, 0.34f));
            var matMidPlateau = LoadOrTempMat(MaterialsFolder + "/Cozy_C3E_MidPlateau.mat", new Color(0.42f, 0.62f, 0.32f));
            var matYard = LoadOrTempMat(MaterialsFolder + "/Cozy_C3E_SettlementYard.mat", new Color(0.47f, 0.67f, 0.35f));
            var matPond = LoadOrTempMat(MaterialsFolder + "/Cozy_C3E_Pond.mat", new Color(0.22f, 0.58f, 0.84f));
            var matAuthoredPath = LoadOrTempMat(MaterialsFolder + "/Cozy_C3E_AuthoredPath.mat", new Color(0.58f, 0.49f, 0.35f));
            var matHearthStone = LoadOrTempMat(MaterialsFolder + "/Cozy_C3E_HearthStone.mat", new Color(0.36f, 0.34f, 0.32f));
            var matHearthFire = LoadOrTempMat(MaterialsFolder + "/Cozy_C3E_HearthFire.mat", new Color(0.95f, 0.48f, 0.16f));
            var matBorderX = LoadOrTempMat(MaterialsFolder + "/Cozy_C3E_BorderX.mat", new Color(0.96f, 0.82f, 0.16f));
            var matBorderZ = LoadOrTempMat(MaterialsFolder + "/Cozy_C3E_BorderZ.mat", new Color(0.20f, 0.85f, 0.92f));

            SetupLightingAndOcean(matOcean);

            var world = new GameObject("World");
            BuildAuthoredHomeIsland(
                world.transform,
                matBaseRock, matMidPlateau, matYard, matPond, matAuthoredPath, matHearthStone, matHearthFire,
                out var waterSource,
                out var authoredPathMain,
                out var authoredPathCross);
            PlaceBorderStakes(world.transform, matBorderX, matBorderZ);

            var chunkManager = world.AddComponent<ChunkManager>();
            var islandMeshVisualizer = world.AddComponent<IslandMeshVisualizer>();
            islandMeshVisualizer.Configure(null, chunkManager);

            var homeVisualizer = world.AddComponent<HomeSettlementVisualizer>();
            var defaultFlagFalse = !homeVisualizer.UseContinuousPathNetworkRenderer;
            homeVisualizer.Configure(null, chunkManager);

            report.AppendLine($"Test 2 — Default Feature Flag (false): ok={defaultFlagFalse}");

            var cam = CreateCamera();

            // =========================================================================
            // TEST 3: Save v8 Byte-for-Byte Immutability Across Visual Rebuilds
            // =========================================================================
            var baseSave = BuildSaveV8Fixture();
            var saveCopy = SaveClone.DeepClone(baseSave);
            var shaInitial = SaveJsonSha256(saveCopy);

            islandMeshVisualizer.RebuildFromSave(saveCopy);
            var shaAfterTerrain = SaveJsonSha256(saveCopy);

            homeVisualizer.UseContinuousPathNetworkRenderer = false;
            homeVisualizer.RebuildFromSave(saveCopy);
            var shaAfterLegacy = SaveJsonSha256(saveCopy);
            var legacyTilesBaseline = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");

            homeVisualizer.UseContinuousPathNetworkRenderer = true;
            homeVisualizer.RebuildFromSave(saveCopy);
            var shaAfterContinuous = SaveJsonSha256(saveCopy);

            // Toggle OFF -> ON -> move WaterSource -> restore -> rebuild
            homeVisualizer.UseContinuousPathNetworkRenderer = false;
            homeVisualizer.RebuildFromSave(saveCopy);
            homeVisualizer.UseContinuousPathNetworkRenderer = true;
            homeVisualizer.RebuildFromSave(saveCopy);
            var origWaterPos = waterSource.transform.localPosition;
            waterSource.transform.localPosition = origWaterPos + new Vector3(0.35f, 0f, -0.25f);
            homeVisualizer.RebuildFromSave(saveCopy);
            waterSource.transform.localPosition = origWaterPos;
            WaterSourceLayout.ClearCenterCache();
            homeVisualizer.RebuildFromSave(saveCopy);
            var shaAfterTogglesAndWater = SaveJsonSha256(saveCopy);

            var test3Ok =
                saveCopy.version == 8 &&
                saveCopy.worldGrowth.lastSimulationUtc == 1760000000L &&
                saveCopy.worldGrowth.settlementWatermarkUtc == 1760000000L &&
                shaInitial == shaAfterTerrain &&
                shaInitial == shaAfterLegacy &&
                shaInitial == shaAfterContinuous &&
                shaInitial == shaAfterTogglesAndWater;

            report.AppendLine();
            report.AppendLine($"Test 3 — Save v8 Byte-for-Byte Immutability: ok={test3Ok}");
            report.AppendLine($"  version={saveCopy.version}, lastSimUtc={saveCopy.worldGrowth.lastSimulationUtc}, settleWatermarkUtc={saveCopy.worldGrowth.settlementWatermarkUtc}");
            report.AppendLine($"  shaInitial={shaInitial}");
            report.AppendLine($"  shaAfterTerrain={shaAfterTerrain} match={shaInitial == shaAfterTerrain}");
            report.AppendLine($"  shaAfterLegacy={shaAfterLegacy} match={shaInitial == shaAfterLegacy}");
            report.AppendLine($"  shaAfterContinuous={shaAfterContinuous} match={shaInitial == shaAfterContinuous}");
            report.AppendLine($"  shaAfterTogglesAndWater={shaAfterTogglesAndWater} match={shaInitial == shaAfterTogglesAndWater}");

            // =========================================================================
            // TEST 4: Seed-Only Invalidation (commit 0336dd4)
            // =========================================================================
            var renderer = homeVisualizer.ContinuousPathRenderer;
            var planBefore = homeVisualizer.LastContinuousPathPlan;
            var buildBefore = renderer != null ? renderer.LastBuildReport : null;
            var applyBefore = renderer != null ? renderer.GetStats().TotalApplyCalls : 0;

            var hut70YawBefore = FindHutYaw(homeVisualizer.GrownSettlementRoot, "SettlementChunk_1_0", "Hut_70");
            var hut91YawBefore = FindHutYaw(homeVisualizer.GrownSettlementRoot, "SettlementChunk_1_0", "Hut_91");
            var planHashBefore = planBefore != null ? planBefore.TopologyHash : "";
            var meshHashBefore = buildBefore != null ? buildBefore.MeshHash : "";
            var chunk01HashBefore = GetChunkHash(buildBefore, new Vector2Int(0, 1));
            var chunk10HashBefore = GetChunkHash(buildBefore, new Vector2Int(1, 0));

            RenderShot(cam, new Vector3(14.5f, 11.5f, -9.5f), new Vector3(13.5f, 0.7f, 0.5f), 38f, "c3f_01_seed_before.png");

            // Mutate ONLY chunk (1,0).growthSeed — masks, water, other chunks untouched
            var chunk10Data = FindChunk(saveCopy, 1, 0);
            var origSeed10 = chunk10Data.growthSeed;
            chunk10Data.growthSeed = 9912345;
            var shaSeedMutatedBefore = SaveJsonSha256(saveCopy);

            homeVisualizer.RebuildFromSave(saveCopy);
            var shaSeedMutatedAfter = SaveJsonSha256(saveCopy);

            var planAfter = homeVisualizer.LastContinuousPathPlan;
            var buildAfter = renderer != null ? renderer.LastBuildReport : null;
            var statsAfter = renderer != null ? renderer.GetStats() : null;
            var hut70YawAfter = FindHutYaw(homeVisualizer.GrownSettlementRoot, "SettlementChunk_1_0", "Hut_70");
            var hut91YawAfter = FindHutYaw(homeVisualizer.GrownSettlementRoot, "SettlementChunk_1_0", "Hut_91");
            var planHashAfter = planAfter != null ? planAfter.TopologyHash : "";
            var meshHashAfter = buildAfter != null ? buildAfter.MeshHash : "";
            var chunk01HashAfter = GetChunkHash(buildAfter, new Vector2Int(0, 1));
            var chunk10HashAfter = GetChunkHash(buildAfter, new Vector2Int(1, 0));
            var legacyPathsAfterSeed = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");

            RenderShot(cam, new Vector3(14.5f, 11.5f, -9.5f), new Vector3(13.5f, 0.7f, 0.5f), 38f, "c3f_02_seed_after_mutation.png");

            // Restore original seed and verify exact round-trip determinism
            chunk10Data.growthSeed = origSeed10;
            homeVisualizer.RebuildFromSave(saveCopy);
            var planRestored = homeVisualizer.LastContinuousPathPlan;
            var buildRestored = renderer != null ? renderer.LastBuildReport : null;
            var hut70YawRestored = FindHutYaw(homeVisualizer.GrownSettlementRoot, "SettlementChunk_1_0", "Hut_70");
            var hut91YawRestored = FindHutYaw(homeVisualizer.GrownSettlementRoot, "SettlementChunk_1_0", "Hut_91");
            var planHashRestored = planRestored != null ? planRestored.TopologyHash : "";
            var meshHashRestored = buildRestored != null ? buildRestored.MeshHash : "";

            var yawChanged = Mathf.Abs(Mathf.DeltaAngle(hut70YawBefore, hut70YawAfter)) > 1f ||
                             Mathf.Abs(Mathf.DeltaAngle(hut91YawBefore, hut91YawAfter)) > 1f;
            var yawRestored = Mathf.Abs(Mathf.DeltaAngle(hut70YawBefore, hut70YawRestored)) < 0.01f &&
                              Mathf.Abs(Mathf.DeltaAngle(hut91YawBefore, hut91YawRestored)) < 0.01f;

            var test4Ok =
                statsAfter != null &&
                buildAfter != null &&
                statsAfter.TotalApplyCalls == applyBefore + 1 &&
                yawChanged &&
                planHashAfter != planHashBefore &&
                meshHashAfter != meshHashBefore &&
                chunk10HashAfter != chunk10HashBefore &&
                chunk01HashAfter == chunk01HashBefore &&
                shaSeedMutatedBefore == shaSeedMutatedAfter &&
                legacyPathsAfterSeed == 0 &&
                buildAfter.DownwardTriangles == 0 &&
                buildAfter.FoldbackTriangles == 0 &&
                buildAfter.MaxBorderPositionDelta <= 0.0001f &&
                statsAfter.UsesSharedMaterialsOnly &&
                statsAfter.LiveOwnedMeshCount == statsAfter.ActiveChunkCount * 2 &&
                yawRestored &&
                planHashRestored == planHashBefore &&
                meshHashRestored == meshHashBefore;

            report.AppendLine();
            report.AppendLine($"Test 4 — Seed-Only Invalidation (commit 0336dd4): ok={test4Ok}");
            report.AppendLine(string.Format(inv,
                "  chunk(1,0) seed: {0} -> {1} -> {0}; applyCalls: {2} -> {3}",
                origSeed10, 9912345, applyBefore, statsAfter?.TotalApplyCalls));
            report.AppendLine(string.Format(inv,
                "  Hut_70 yaw: {0:0.0} -> {1:0.0} -> {2:0.0}; Hut_91 yaw: {3:0.0} -> {4:0.0} -> {5:0.0}",
                hut70YawBefore, hut70YawAfter, hut70YawRestored, hut91YawBefore, hut91YawAfter, hut91YawRestored));
            report.AppendLine($"  planHash: before={planHashBefore} after={planHashAfter} restored={planHashRestored} (roundTrip={planHashRestored == planHashBefore})");
            report.AppendLine($"  meshHash: before={meshHashBefore} after={meshHashAfter} restored={meshHashRestored} (roundTrip={meshHashRestored == meshHashBefore})");
            report.AppendLine($"  chunk(1,0) hash changed={chunk10HashAfter != chunk10HashBefore}; untouched chunk(0,1) hash preserved={chunk01HashAfter == chunk01HashBefore}");
            report.AppendLine($"  saveImmutabilityAfterSeedVisualRebuild={shaSeedMutatedBefore == shaSeedMutatedAfter}, legacyPathTiles={legacyPathsAfterSeed}, foldbacks={buildAfter?.FoldbackTriangles}, downwardTris={buildAfter?.DownwardTriangles}");

            // =========================================================================
            // TEST 5: Isolated In-Memory Offline Growth Catch-Up + Visual Rebuild
            // =========================================================================
            var offlineSource = SaveClone.DeepClone(baseSave);
            var sim = new WorldGrowthSimulation();
            var cfg = WorldGrowthConfig.CreateDefault();
            var simResult = sim.Simulate(offlineSource, TimeSpan.FromHours(48), cfg);
            var offlineSimOk = simResult != null && simResult.success && simResult.updatedSave != null && simResult.appliedSeconds > 0;
            var offlineSave = offlineSimOk ? simResult.updatedSave : offlineSource;
            if (offlineSimOk)
            {
                SaveMigrationService.EnsureEcosystemWeatherAnimals(offlineSave);
                var rng = new DeterministicRng(unchecked((uint)(offlineSave.worldGrowth.growthSeed ^ (int)simResult.appliedSeconds ^ 0xEC05)));
                SettlementSimulation.Apply(offlineSave, simResult.appliedSeconds, rng);
                SettlementSimulation.RebalanceExcessStones(offlineSave);
                offlineSave.worldGrowth.growthSeed = unchecked((int)rng.State);
            }

            var shaAfterOfflineSim = SaveJsonSha256(offlineSave);
            // Rebuild visuals from committed offline clone (in-memory only)
            homeVisualizer.RebuildFromSave(offlineSave);
            var shaAfterOfflineVisual = SaveJsonSha256(offlineSave);

            var offlineRenderer = homeVisualizer.ContinuousPathRenderer;
            var offlineStats = offlineRenderer != null ? offlineRenderer.GetStats() : null;
            var offlineBuild = offlineRenderer != null ? offlineRenderer.LastBuildReport : null;
            var offlinePlan = homeVisualizer.LastContinuousPathPlan;
            var offlineLegacyPaths = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");
            var offlinePondIntrusions = CountCoreVerticesInsidePond(offlineRenderer, WaterSourceLayout.GetWorldCenter(), WaterSourceLayout.WaterHalfExtentX, WaterSourceLayout.WaterHalfExtentZ);

            RenderShot(cam, new Vector3(14f, 22f, -15f), new Vector3(4.5f, 1.0f, 3.5f), 42f, "c3f_03_after_offline_growth.png");

            var test5Ok =
                offlineSimOk &&
                offlineSave.worldGrowth.lastSimulationUtc == baseSave.worldGrowth.lastSimulationUtc + simResult.appliedSeconds &&
                shaAfterOfflineSim == shaAfterOfflineVisual &&
                offlineStats != null &&
                offlineBuild != null &&
                offlinePlan != null &&
                offlineLegacyPaths == 0 &&
                offlinePondIntrusions == 0 &&
                offlineBuild.DownwardTriangles == 0 &&
                offlineBuild.FoldbackTriangles == 0 &&
                offlineBuild.MaxBorderPositionDelta <= 0.0001f &&
                offlineBuild.MaxBorderHeightDelta <= 0.0001f &&
                offlineStats.UsesSharedMaterialsOnly &&
                offlineStats.LiveOwnedMeshCount == offlineStats.ActiveChunkCount * 2;

            report.AppendLine();
            report.AppendLine($"Test 5 — Isolated Offline Growth (+48h) + Visual Rebuild: ok={test5Ok}");
            report.AppendLine($"  simSuccess={offlineSimOk} (err={simResult?.errorCode}:{simResult?.errorMessage}), appliedSeconds={simResult?.appliedSeconds}, lastSimUtc: {baseSave.worldGrowth.lastSimulationUtc} -> {offlineSave.worldGrowth.lastSimulationUtc}, settleWatermarkUtc={offlineSave.worldGrowth.settlementWatermarkUtc}");
            report.AppendLine($"  saveImmutabilityAfterVisualRebuild={shaAfterOfflineSim == shaAfterOfflineVisual} (sha={shaAfterOfflineVisual})");
            report.AppendLine(string.Format(inv,
                "  chunks={0}, nodes={1}, edges={2}, huts={3}, legacyPaths={4}, pondIntrusions={5}, foldbacks={6}, downwardTris={7}, maxBorderPos={8:0.00000}m, liveOwnedMeshes={9}",
                offlineStats?.ActiveChunkCount, offlinePlan?.Nodes.Count, offlinePlan?.Edges.Count, offlinePlan?.HutReports.Count,
                offlineLegacyPaths, offlinePondIntrusions, offlineBuild?.FoldbackTriangles, offlineBuild?.DownwardTriangles,
                offlineBuild != null ? offlineBuild.MaxBorderPositionDelta : -1f, offlineStats?.LiveOwnedMeshCount));

            // =========================================================================
            // TEST 6: Chunk Add / Remove / Reload Lifecycle & Rollback Zero Leaks
            // =========================================================================
            var chunkTestSave = SaveClone.DeepClone(baseSave);
            homeVisualizer.RebuildFromSave(chunkTestSave);
            var baseChunkCount = homeVisualizer.ContinuousPathRenderer.GetStats().ActiveChunkCount;

            // Add new chunk (2,0) extending East road across x = 24.0m border + new hut
            var chunk20 = BuildChunk20Data();
            chunkTestSave.worldGrowth.chunks.Add(chunk20);
            homeVisualizer.RebuildFromSave(chunkTestSave);
            var statsAdded = homeVisualizer.ContinuousPathRenderer.GetStats();
            var buildAdded = homeVisualizer.ContinuousPathRenderer.LastBuildReport;
            var meshHashAdded = buildAdded.MeshHash;
            var hasSlot20Added = homeVisualizer.ContinuousPathRenderer.TryGetChunkMeshes(new Vector2Int(2, 0), out var core20, out _) && core20 != null;
            var hasChunkRoot20Added = homeVisualizer.GrownSettlementRoot != null && homeVisualizer.GrownSettlementRoot.Find("SettlementChunk_2_0") != null;

            RenderShot(cam, new Vector3(24f, 16f, -11f), new Vector3(20f, 0.7f, 0.5f), 42f, "c3f_04_chunk_added_2_0.png");

            // Remove chunk (2,0)
            chunkTestSave.worldGrowth.chunks.Remove(chunk20);
            homeVisualizer.RebuildFromSave(chunkTestSave);
            var statsRemoved = homeVisualizer.ContinuousPathRenderer.GetStats();
            var hasSlot20Removed = homeVisualizer.ContinuousPathRenderer.TryGetChunkMeshes(new Vector2Int(2, 0), out _, out _);
            var hasChunkRoot20Removed = homeVisualizer.GrownSettlementRoot != null && homeVisualizer.GrownSettlementRoot.Find("SettlementChunk_2_0") != null;

            // Re-add chunk (2,0) and confirm deterministic MeshHash recovery
            chunkTestSave.worldGrowth.chunks.Add(chunk20);
            homeVisualizer.RebuildFromSave(chunkTestSave);
            var buildReloaded = homeVisualizer.ContinuousPathRenderer.LastBuildReport;
            var reloadHashMatch = buildReloaded != null && buildReloaded.MeshHash == meshHashAdded;

            // Toggle OFF and verify 0 Mesh leaks + legacy rollback
            homeVisualizer.UseContinuousPathNetworkRenderer = false;
            homeVisualizer.RebuildFromSave(chunkTestSave);
            var clearedStats = homeVisualizer.LastClearedRendererStats;
            var rootGoneAfterOff = GameObject.Find("SettlementPathNetwork") == null && homeVisualizer.ContinuousPathRenderer == null;
            var authoredRestoredAfterOff = authoredPathMain.activeSelf && authoredPathCross.activeSelf;
            var zeroLeaksAfterOff = clearedStats != null &&
                                    clearedStats.LiveOwnedMeshCount == 0 &&
                                    clearedStats.TotalMeshesCreated == clearedStats.TotalMeshesDestroyed;

            // Restore baseSave with flag OFF to check exact legacy tile count
            homeVisualizer.RebuildFromSave(baseSave);
            var legacyTilesRestored = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");

            var test6Ok =
                hasSlot20Added &&
                hasChunkRoot20Added &&
                statsAdded.ActiveChunkCount == baseChunkCount + 1 &&
                buildAdded.MaxBorderPositionDelta <= 0.0001f &&
                !hasSlot20Removed &&
                !hasChunkRoot20Removed &&
                statsRemoved.ActiveChunkCount == baseChunkCount &&
                statsRemoved.LiveOwnedMeshCount == statsRemoved.ActiveChunkCount * 2 &&
                reloadHashMatch &&
                rootGoneAfterOff &&
                authoredRestoredAfterOff &&
                zeroLeaksAfterOff &&
                legacyTilesRestored == legacyTilesBaseline;

            report.AppendLine();
            report.AppendLine($"Test 6 — Chunk Add / Remove / Reload & OFF Rollback Zero Leaks: ok={test6Ok}");
            report.AppendLine($"  chunks: base={baseChunkCount} -> added={statsAdded.ActiveChunkCount} (slot20={hasSlot20Added}, maxBorderPos={buildAdded.MaxBorderPositionDelta.ToString("0.00000", inv)}m) -> removed={statsRemoved.ActiveChunkCount} (slot20={hasSlot20Removed}, root20={hasChunkRoot20Removed})");
            report.AppendLine($"  reloadDeterministicHashMatch={reloadHashMatch}");
            report.AppendLine($"  rollbackOff: rootGone={rootGoneAfterOff}, zeroLeaks={zeroLeaksAfterOff} (created={clearedStats?.TotalMeshesCreated}, destroyed={clearedStats?.TotalMeshesDestroyed}, liveOwned={clearedStats?.LiveOwnedMeshCount}), authoredRestored={authoredRestoredAfterOff}, legacyTilesRestored={legacyTilesRestored}/{legacyTilesBaseline}");

            // =========================================================================
            // TEST 7: Protected Scenes Unchanged
            // =========================================================================
            var protectedOk = true;
            report.AppendLine();
            report.AppendLine("Test 7 — Protected Scenes Unchanged Check:");
            foreach (var p in ProtectedScenes)
            {
                var after = FileSha256Short(p);
                var same = string.Equals(protectedBefore[p], after, StringComparison.Ordinal);
                if (!same) protectedOk = false;
                report.AppendLine($"  {p}: before={protectedBefore[p]} after={after} unchanged={same}");
            }

            var allOk = diagEditModeSafe && defaultFlagFalse && test3Ok && test4Ok && test5Ok && test6Ok && protectedOk;
            report.AppendLine();
            report.AppendLine("OVERALL STAGE C-3F AUTOMATED VERIFICATION: " + (allOk ? "PASS" : "FAIL"));

            var text = report.ToString();
            File.WriteAllText(BuildReportPath, text, Encoding.UTF8);
            File.WriteAllText(DocsReportPath, text, Encoding.UTF8);

            // Clean up temp scene objects cleanly before exiting
            UnityEngine.Object.DestroyImmediate(world);

            return allOk;
        }

        private static GameSaveData BuildSaveV8Fixture()
        {
            var save = new GameSaveData
            {
                version = 8,
                lastSaveUtc = 1760000000L,
                worldGrowth = new WorldGrowthSaveData
                {
                    growthSeed = 314159,
                    lastSimulationUtc = 1760000000L,
                    settlementWatermarkUtc = 1760000000L,
                    chunks = new List<ChunkGrowthSaveData>()
                }
            };

            var settle00 = new byte[ChunkMaskCodec.CellCount];
            var biome00 = FillBiome(BiomeType.Mossy);
            for (var x = 8; x <= 15; x++) SetCell(settle00, x, 8, SettlementStage.Path);
            for (var z = 8; z <= 15; z++) SetCell(settle00, 8, z, SettlementStage.Path);
            for (var z = 5; z <= 7; z++) SetCell(settle00, 7, z, SettlementStage.Path);
            SetCell(settle00, 6, 5, SettlementStage.Path);
            SetCell(settle00, 5, 5, SettlementStage.Clearing);
            SetCell(settle00, 11, 8, SettlementStage.Stone);
            SetCell(settle00, 6, 8, SettlementStage.Hut); // dedupe near Starter_Hut_A

            var land00 = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < land00.Length; i++) land00[i] = (byte)CellStage.Moss;

            save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
            {
                chunkX = 0,
                chunkZ = 0,
                isLegacyBase = true,
                growthSeed = 1001,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land00),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biome00),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle00)
            });
            save.chunks.Add(new ChunkSaveData { chunkX = 0, chunkZ = 0, isUnlocked = true, biomeId = "starter_island" });

            var settle10 = new byte[ChunkMaskCodec.CellCount];
            for (var x = 0; x <= 15; x++) SetCell(settle10, x, 8, SettlementStage.Path);
            for (var z = 4; z <= 15; z++) SetCell(settle10, 5, z, SettlementStage.Path);
            for (var z = 5; z <= 8; z++) SetCell(settle10, 9, z, SettlementStage.Path);
            SetCell(settle10, 7, 8, SettlementStage.Stone);
            SetCell(settle10, 3, 6, SettlementStage.Clearing);
            SetCell(settle10, 6, 4, SettlementStage.Hut);  // cell 70
            SetCell(settle10, 11, 5, SettlementStage.Hut); // cell 91

            save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
            {
                chunkX = 1,
                chunkZ = 0,
                isLegacyBase = false,
                growthSeed = 2002,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(FillLand(1, 0, fullLand: true)),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(FillBiome(BiomeType.Mossy)),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle10)
            });
            save.chunks.Add(new ChunkSaveData { chunkX = 1, chunkZ = 0, isUnlocked = true, biomeId = "mossy" });

            var settle01 = new byte[ChunkMaskCodec.CellCount];
            for (var z = 0; z <= 9; z++) SetCell(settle01, 8, z, SettlementStage.Path);
            for (var x = 8; x <= 15; x++) SetCell(settle01, x, 5, SettlementStage.Path);
            SetCell(settle01, 11, 5, SettlementStage.Stone);
            SetCell(settle01, 5, 9, SettlementStage.Hut);

            save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
            {
                chunkX = 0,
                chunkZ = 1,
                isLegacyBase = false,
                growthSeed = 3003,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(FillLand(0, 1, fullLand: false)),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(FillBiome(BiomeType.Forest)),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle01)
            });
            save.chunks.Add(new ChunkSaveData { chunkX = 0, chunkZ = 1, isUnlocked = true, biomeId = "forest" });

            var settle11 = new byte[ChunkMaskCodec.CellCount];
            for (var x = 0; x <= 8; x++) SetCell(settle11, x, 5, SettlementStage.Path);
            for (var z = 0; z <= 8; z++) SetCell(settle11, 5, z, SettlementStage.Path);
            SetCell(settle11, 8, 7, SettlementStage.Hut);

            save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
            {
                chunkX = 1,
                chunkZ = 1,
                isLegacyBase = false,
                growthSeed = 4004,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(FillLand(1, 1, fullLand: false)),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(FillBiome(BiomeType.Mossy)),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle11)
            });
            save.chunks.Add(new ChunkSaveData { chunkX = 1, chunkZ = 1, isUnlocked = true, biomeId = "mossy" });

            return save;
        }

        private static ChunkGrowthSaveData BuildChunk20Data()
        {
            var settle20 = new byte[ChunkMaskCodec.CellCount];
            for (var x = 0; x <= 7; x++) SetCell(settle20, x, 8, SettlementStage.Path);
            for (var z = 5; z <= 11; z++) SetCell(settle20, 4, z, SettlementStage.Path);
            SetCell(settle20, 6, 5, SettlementStage.Hut);

            return new ChunkGrowthSaveData
            {
                chunkX = 2,
                chunkZ = 0,
                isLegacyBase = false,
                growthSeed = 5005,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(FillLand(2, 0, fullLand: true)),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(FillBiome(BiomeType.Mossy)),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle20)
            };
        }

        private static byte[] FillLand(int chunkX, int chunkZ, bool fullLand)
        {
            var land = new byte[ChunkMaskCodec.CellCount];
            if (fullLand)
            {
                for (var i = 0; i < land.Length; i++) land[i] = (byte)CellStage.Vegetation;
                return land;
            }

            var origin = GrowthWorldPlacement.ChunkOriginXZ(chunkX, chunkZ);
            var cell = GrowthWorldPlacement.CellSizeMeters;
            var center = new Vector2(5.5f, 5.5f);
            for (var lz = 0; lz < ChunkMaskCodec.GridSize; lz++)
            {
                for (var lx = 0; lx < ChunkMaskCodec.GridSize; lx++)
                {
                    var wx = origin.x + (lx + 0.5f) * cell;
                    var wz = origin.y + (lz + 0.5f) * cell;
                    var dist = Vector2.Distance(new Vector2(wx, wz), center);
                    var idx = ChunkMaskCodec.CellIndex(lx, lz);
                    if (dist < 14.5f) land[idx] = (byte)CellStage.Vegetation;
                    else if (dist < 17.0f) land[idx] = (byte)CellStage.Moss;
                    else if (dist < 18.2f) land[idx] = (byte)CellStage.Soil;
                    else land[idx] = (byte)CellStage.Void;
                }
            }

            return land;
        }

        private static byte[] FillBiome(BiomeType b)
        {
            var arr = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < arr.Length; i++) arr[i] = (byte)b;
            return arr;
        }

        private static void SetCell(byte[] mask, int lx, int lz, SettlementStage stage)
        {
            mask[ChunkMaskCodec.CellIndex(lx, lz)] = (byte)stage;
        }

        private static ChunkGrowthSaveData FindChunk(GameSaveData save, int cx, int cz)
        {
            foreach (var c in save.worldGrowth.chunks)
            {
                if (c != null && c.chunkX == cx && c.chunkZ == cz) return c;
            }
            return null;
        }

        private static float FindHutYaw(Transform grownRoot, string chunkRootName, string hutName)
        {
            if (grownRoot == null) return float.NaN;
            var chunk = grownRoot.Find(chunkRootName);
            if (chunk == null) return float.NaN;
            var hut = chunk.Find(hutName);
            return hut != null ? hut.localEulerAngles.y : float.NaN;
        }

        private static string GetChunkHash(SettlementPathMeshBuilder.MeshBuildReport report, Vector2Int coord)
        {
            if (report != null && report.Chunks.TryGetValue(coord, out var data))
            {
                return data.ChunkHash ?? string.Empty;
            }
            return string.Empty;
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

        private static void BuildAuthoredHomeIsland(
            Transform world,
            Material matBaseRock, Material matMidPlateau, Material matYard, Material matPond,
            Material matAuthoredPath, Material matHearthStone, Material matHearthFire,
            out GameObject waterSource, out GameObject authoredPathMain, out GameObject authoredPathCross)
        {
            var island = new GameObject("Island");
            island.transform.SetParent(world, false);
            CreateCylinder(island.transform, "BaseRock", new Vector3(0f, 0.2f, 0f), new Vector3(17.2f, 0.6f, 13.6f), matBaseRock);
            CreateCylinder(island.transform, "MidPlateau", new Vector3(0f, 0.9f, 0f), new Vector3(12.0f, 0.5f, 10.0f), matMidPlateau);
            CreateCylinder(island.transform, "SettlementYard", new Vector3(0f, 1.55f, 0f), new Vector3(7.4f, 0.3f, 6.4f), matYard);

            waterSource = CreateCylinder(island.transform, "WaterSource", WaterSourceLayout.LocalPosition, WaterSourceLayout.LocalScale, matPond);

            var settlement = new GameObject("Settlement");
            settlement.transform.SetParent(island.transform, false);
            authoredPathMain = CreateBox(settlement.transform, "Path_Main", new Vector3(0f, 1.88f, 0.2f), new Vector3(1.1f, 0.06f, 4.2f), matAuthoredPath);
            authoredPathCross = CreateBox(settlement.transform, "Path_Cross", new Vector3(0.9f, 1.88f, -0.6f), new Vector3(2.4f, 0.05f, 0.7f), matAuthoredPath);
            SpawnStarterHut(settlement.transform, "Hut_A", "AfkIsland/Settlement/Hut_A", new Vector3(-1.6f, 1.85f, 0.9f), 12f);
            SpawnStarterHut(settlement.transform, "Hut_B", "AfkIsland/Settlement/Hut_B", new Vector3(1.8f, 1.85f, -0.2f), -25f);
            CreateBox(settlement.transform, "Hearth", new Vector3(0.15f, 1.92f, -1.5f), new Vector3(0.9f, 0.18f, 0.9f), matHearthStone);
            CreateBox(settlement.transform, "HearthFire", new Vector3(0.15f, 2.05f, -1.5f), new Vector3(0.35f, 0.25f, 0.35f), matHearthFire);
        }

        private static void SpawnStarterHut(Transform parent, string name, string resourcePath, Vector3 pos, float yaw)
        {
            var prefab = Resources.Load<GameObject>(resourcePath);
            var go = prefab != null ? UnityEngine.Object.Instantiate(prefab, parent) : GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            if (prefab == null) go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * SettlementCellQuery.HutVisualUniformScale;
        }

        private static void PlaceBorderStakes(Transform parent, Material matX, Material matZ)
        {
            var root = new GameObject("ChunkBorderMarkers");
            root.transform.SetParent(parent, false);
            foreach (var z in new[] { -2.5f, 3.5f, 11.5f })
                CreateCylinder(root.transform, $"BorderX8_{z:0}", new Vector3(8.0f, 1.2f, z), new Vector3(0.14f, 0.85f, 0.14f), matX);
            foreach (var z in new[] { -2.5f, 3.5f })
                CreateCylinder(root.transform, $"BorderX24_{z:0}", new Vector3(24.0f, 1.2f, z), new Vector3(0.14f, 0.85f, 0.14f), matX);
            foreach (var x in new[] { -2.0f, 4.0f, 12.5f })
                CreateCylinder(root.transform, $"BorderZ8_{x:0}", new Vector3(x, 1.2f, 8.0f), new Vector3(0.14f, 0.85f, 0.14f), matZ);
        }

        private static GameObject CreateCylinder(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            ApplyMatAndStripCollider(go, mat);
            return go;
        }

        private static GameObject CreateBox(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            ApplyMatAndStripCollider(go, mat);
            return go;
        }

        private static void ApplyMatAndStripCollider(GameObject go, Material mat)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null && mat != null) { r.sharedMaterial = mat; r.shadowCastingMode = ShadowCastingMode.On; r.receiveShadows = true; }
            var c = go.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }

        private static Material LoadOrTempMat(string assetPath, Color fallback)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (mat != null) return mat;
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader) { hideFlags = HideFlags.DontSave };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", fallback);
            if (m.HasProperty("_Color")) m.SetColor("_Color", fallback);
            return m;
        }

        private static void SetupLightingAndOcean(Material matOcean)
        {
            var lightGo = new GameObject("Sun_Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1.0f, 0.96f, 0.88f);
            light.intensity = 1.2f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(48f, -38f, 0f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.72f, 0.84f, 0.94f);
            RenderSettings.ambientEquatorColor = new Color(0.64f, 0.68f, 0.62f);
            RenderSettings.ambientGroundColor = new Color(0.38f, 0.42f, 0.34f);

            var ocean = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ocean.name = "Ocean_Water";
            ocean.transform.position = new Vector3(8f, -0.35f, 4f);
            ocean.transform.localScale = new Vector3(32f, 1f, 32f);
            ApplyMatAndStripCollider(ocean, matOcean);
        }

        private static Camera CreateCamera()
        {
            var camGo = new GameObject("StageC3F_Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.34f, 0.68f, 0.82f);
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 200f;
            cam.fieldOfView = 42f;
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
    }
}
