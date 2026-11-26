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
using UnityEngine.SceneManagement;

namespace AfkIsland.Editor
{
    /// <summary>
    /// Stage C-3E isolated integration verifier and test scene builder.
    /// Exercises the real HomeSettlementVisualizer + WorldTerrainSampleCache + IslandMeshVisualizer
    /// + SettlementPathTopology + SettlementPathNetworkRenderer pipeline on an isolated 2x2 chunk
    /// test scene (never touches MainIsland.unity, B-2, C-2, C-3C, C-3D, or any Save v8 files).
    /// </summary>
    public static class CozyDioramaStageC3EVerifier
    {
        private const string ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3E.unity";
        private const string MaterialsFolder = "Assets/_Project/Art/CozyDiorama/Materials";
        private const string BuildShotsDir = "Builds/cozy-diorama/stage-c3e-shots";
        private const string DocsShotsDir = "Docs/CozyDiorama/StageC3E-Shots";
        private const string BuildReportPath = "Builds/cozy-diorama/stage-c3e-report.txt";
        private const string DocsReportPath = "Docs/CozyDiorama/StageC3E-Shots/integration-report.txt";

        private static readonly string[] ProtectedScenes =
        {
            "Assets/_Project/Scenes/MainIsland.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageB2.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC2.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3C.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3D.unity"
        };

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3/Verify Stage C-3E Integration (Test Scene)")]
        public static void RunVerificationMenu()
        {
            var ok = RunVerificationInternal(throwOnFail: false);
            Debug.Log(ok
                ? "[AFK Island] Stage C-3E integration verification PASSED. Report: " + DocsReportPath
                : "[AFK Island] Stage C-3E integration verification FAILED. Inspect: " + DocsReportPath);
        }

        public static void RunBatchVerification()
        {
            var ok = RunVerificationInternal(throwOnFail: false);
            Debug.Log("[AFK Island] Stage C-3E batch verification finished: ok=" + ok);
            if (!ok)
            {
                EditorApplication.Exit(1);
            }
        }

        private static bool RunVerificationInternal(bool throwOnFail)
        {
            var inv = CultureInfo.InvariantCulture;
            var protectedHashesBefore = new Dictionary<string, string>();
            foreach (var p in ProtectedScenes)
            {
                protectedHashesBefore[p] = FileSha256Short(p);
            }

            Directory.CreateDirectory(BuildShotsDir);
            Directory.CreateDirectory(DocsShotsDir);
            Directory.CreateDirectory(Path.GetDirectoryName(BuildReportPath) ?? "Builds/cozy-diorama");
            EnsureFolder("Assets/_Project/Scenes/Prototypes");
            EnsureFolder(MaterialsFolder);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "CozyDioramaStageC3E";

            var matOcean = CreateOrUpdateMaterial(MaterialsFolder + "/Cozy_C3E_Ocean.mat", new Color(0.28f, 0.64f, 0.78f));
            var matBaseRock = CreateOrUpdateMaterial(MaterialsFolder + "/Cozy_C3E_BaseRock.mat", new Color(0.45f, 0.40f, 0.34f));
            var matMidPlateau = CreateOrUpdateMaterial(MaterialsFolder + "/Cozy_C3E_MidPlateau.mat", new Color(0.42f, 0.62f, 0.32f));
            var matYard = CreateOrUpdateMaterial(MaterialsFolder + "/Cozy_C3E_SettlementYard.mat", new Color(0.47f, 0.67f, 0.35f));
            var matPond = CreateOrUpdateMaterial(MaterialsFolder + "/Cozy_C3E_Pond.mat", new Color(0.22f, 0.58f, 0.84f));
            var matAuthoredPath = CreateOrUpdateMaterial(MaterialsFolder + "/Cozy_C3E_AuthoredPath.mat", new Color(0.58f, 0.49f, 0.35f));
            var matHearthStone = CreateOrUpdateMaterial(MaterialsFolder + "/Cozy_C3E_HearthStone.mat", new Color(0.36f, 0.34f, 0.32f));
            var matHearthFire = CreateOrUpdateMaterial(MaterialsFolder + "/Cozy_C3E_HearthFire.mat", new Color(0.95f, 0.48f, 0.16f));
            var matBorderX = CreateOrUpdateMaterial(MaterialsFolder + "/Cozy_C3E_BorderX.mat", new Color(0.96f, 0.82f, 0.16f));
            var matBorderZ = CreateOrUpdateMaterial(MaterialsFolder + "/Cozy_C3E_BorderZ.mat", new Color(0.20f, 0.85f, 0.92f));

            SetupLightingAndOcean(matOcean);

            // Create World root matching MainIsland production hierarchy
            var world = new GameObject("World");
            var island = BuildAuthoredHomeIsland(
                world.transform,
                matBaseRock,
                matMidPlateau,
                matYard,
                matPond,
                matAuthoredPath,
                matHearthStone,
                matHearthFire,
                out var waterSource,
                out var authoredPathMain,
                out var authoredPathCross,
                out var authoredHutA,
                out var authoredHutB,
                out var authoredHearth);

            // Chunk border stakes (X = 8.0m yellow, Z = 8.0m cyan)
            PlaceBorderStakes(world.transform, matBorderX, matBorderZ);

            var chunkManager = world.AddComponent<ChunkManager>();
            var islandMeshVisualizer = world.AddComponent<IslandMeshVisualizer>();
            islandMeshVisualizer.Configure(null, chunkManager);

            var homeVisualizer = world.AddComponent<HomeSettlementVisualizer>();
            var defaultFlagIsFalse = !homeVisualizer.UseContinuousPathNetworkRenderer;
            homeVisualizer.Configure(null, chunkManager);

            var save = BuildIntegrationTestSave();

            // Build grown terrain chunks (1,0), (0,1), (1,1) using real IslandMeshVisualizer + WorldTerrainSampleCache
            islandMeshVisualizer.RebuildFromSave(save);

            var cam = CreateReviewCamera();

            var report = new StringBuilder();
            report.AppendLine("Stage C-3E — Production Integration Verification Report (Isolated Test Scene)");
            report.AppendLine("Scene: " + ScenePath);
            report.AppendLine("Default flag check (useContinuousPathNetworkRenderer == false): ok=" + defaultFlagIsFalse);

            // =========================================================================
            // PASS 1: Flag OFF (legacy tile renderer)
            // =========================================================================
            homeVisualizer.UseContinuousPathNetworkRenderer = false;
            homeVisualizer.RebuildFromSave(save);

            var pass1LegacyPaths = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");
            var pass1Stones = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Stone_");
            var pass1Clearings = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Clearing_");
            var pass1GrownHuts = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Hut_");
            var pass1AuthoredPathsActive = (authoredPathMain.activeSelf ? 1 : 0) + (authoredPathCross.activeSelf ? 1 : 0);
            var pass1HasContinuousRoot = GameObject.Find("SettlementPathNetwork") != null || homeVisualizer.ContinuousPathRenderer != null;
            var pass1Ok = pass1LegacyPaths > 0 && pass1AuthoredPathsActive == 2 && !pass1HasContinuousRoot && pass1Stones > 0 && pass1Clearings > 0 && pass1GrownHuts > 0;

            RenderCameraShot(cam, new Vector3(14f, 22f, -15f), new Vector3(4.5f, 1.0f, 3.5f), 42f, "c3e_01_flag_off_legacy.png");

            report.AppendLine();
            report.AppendLine("Pass 1 — Flag OFF (Legacy Mode): ok=" + pass1Ok);
            report.AppendLine($"  legacyPathTiles={pass1LegacyPaths}, clearings={pass1Clearings}, stones={pass1Stones}, grownHuts={pass1GrownHuts}");
            report.AppendLine($"  authoredStarterPathsActive={pass1AuthoredPathsActive}/2, continuousRendererPresent={pass1HasContinuousRoot}");

            // =========================================================================
            // PASS 2: Toggle OFF -> ON (Continuous Path Network Renderer)
            // =========================================================================
            homeVisualizer.UseContinuousPathNetworkRenderer = true;
            homeVisualizer.RebuildFromSave(save);

            var pass2LegacyPaths = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");
            var pass2Stones = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Stone_");
            var pass2Clearings = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Clearing_");
            var pass2GrownHuts = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Hut_");
            var pass2AuthoredPathsActive = (authoredPathMain.activeSelf ? 1 : 0) + (authoredPathCross.activeSelf ? 1 : 0);
            var pass2AuthoredHutsHearthActive = (authoredHutA.activeSelf ? 1 : 0) + (authoredHutB.activeSelf ? 1 : 0) + (authoredHearth.activeSelf ? 1 : 0);

            var renderer = homeVisualizer.ContinuousPathRenderer;
            var plan = homeVisualizer.LastContinuousPathPlan;
            var stats2 = renderer != null ? renderer.GetStats() : null;
            var build2 = renderer != null ? renderer.LastBuildReport : null;

            var starterAConnected = false;
            var starterBConnected = false;
            var duplicateStarterSkipped = true;
            var allHutsConnected = plan != null && plan.HutReports.Count > 0;
            var waterViolations = 0;
            var totalHutCount = 0;

            if (plan != null)
            {
                totalHutCount = plan.HutReports.Count;
                for (var i = 0; i < plan.HutReports.Count; i++)
                {
                    var h = plan.HutReports[i];
                    if (h.HutId == "Starter_Hut_A") starterAConnected = IsConnectedStatus(h.Status);
                    if (h.HutId == "Starter_Hut_B") starterBConnected = IsConnectedStatus(h.Status);
                    if (h.HutId.StartsWith("MaskHut_0_0_"))
                    {
                        // Verify duplicate mask hut near Starter_Hut_A (-1.6, 0.9) was deduplicated
                        var cellIdxStr = h.HutId.Substring("MaskHut_0_0_".Length);
                        if (int.TryParse(cellIdxStr, out var ci))
                        {
                            var cPos = GrowthWorldPlacement.CellCenterWorld(0, 0, ci);
                            if (Vector2.Distance(new Vector2(cPos.x, cPos.z), new Vector2(-1.6f, 0.9f)) <= 0.75f)
                            {
                                duplicateStarterSkipped = false;
                            }
                        }
                    }

                    if (!IsConnectedStatus(h.Status))
                    {
                        allHutsConnected = false;
                    }

                    if (h.HardViolations != null)
                    {
                        for (var v = 0; v < h.HardViolations.Count; v++)
                        {
                            if (h.HardViolations[v].IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                waterViolations++;
                            }
                        }
                    }
                }
            }

            // Count T-junctions (degree == 3) and 4-way junctions (degree >= 4)
            var tJunctions = 0;
            var fourWayJunctions = 0;
            if (plan != null)
            {
                for (var i = 0; i < plan.Nodes.Count; i++)
                {
                    var deg = plan.Nodes[i].Degree;
                    if (deg == 3) tJunctions++;
                    else if (deg >= 4) fourWayJunctions++;
                }
            }

            // Check zero core vertices inside the pond ellipse
            var pondCenter = WaterSourceLayout.GetWorldCenter();
            var pondVertexIntrusionCount = CountCoreVerticesInsidePond(renderer, pondCenter, WaterSourceLayout.WaterHalfExtentX, WaterSourceLayout.WaterHalfExtentZ);

            var pass2Ok =
                renderer != null &&
                plan != null &&
                build2 != null &&
                stats2 != null &&
                pass2LegacyPaths == 0 &&
                pass2AuthoredPathsActive == 0 &&
                pass2AuthoredHutsHearthActive == 3 &&
                pass2Stones == pass1Stones &&
                pass2Clearings == pass1Clearings &&
                pass2GrownHuts == pass1GrownHuts &&
                starterAConnected &&
                starterBConnected &&
                duplicateStarterSkipped &&
                allHutsConnected &&
                plan.StarterIssues.Count == 0 &&
                waterViolations == 0 &&
                pondVertexIntrusionCount == 0 &&
                tJunctions >= 2 &&
                fourWayJunctions >= 1 &&
                build2.DownwardTriangles == 0 &&
                build2.FoldbackTriangles == 0 &&
                build2.BorderSharedPointPairs >= 2 &&
                build2.MaxBorderPositionDelta <= 0.0001f &&
                build2.MaxBorderHeightDelta <= 0.0001f &&
                build2.MaxJunctionAttachDelta <= 0.0001f &&
                stats2.UsesSharedMaterialsOnly &&
                stats2.ActiveChunkCount == 4;

            var pass2PlanHash = plan != null ? plan.TopologyHash : "";
            var pass2MeshHash = build2 != null ? build2.MeshHash : "";
            var pass2ApplyCalls = stats2 != null ? stats2.TotalApplyCalls : 0;

            // Render review screenshots while Flag ON
            RenderCameraShot(cam, new Vector3(14f, 22f, -15f), new Vector3(4.5f, 1.0f, 3.5f), 42f, "c3e_02_flag_on_overview.png");
            RenderCameraShot(cam, new Vector3(-3.5f, 9.0f, -6.5f), new Vector3(0.4f, 1.85f, -0.3f), 38f, "c3e_03_starter_huts_and_pond.png");
            RenderCameraShot(cam, new Vector3(14.5f, 11.5f, -9.5f), new Vector3(13.5f, 0.7f, 0.5f), 38f, "c3e_04_4way_and_t_junctions.png");
            RenderCameraShot(cam, new Vector3(14.0f, 13.0f, -1.5f), new Vector3(8.0f, 0.8f, 7.5f), 42f, "c3e_05_chunk_borders_xz.png");
            RenderCameraShot(cam, new Vector3(4.0f, 4.4f, -8.5f), new Vector3(7.5f, 1.1f, 0.5f), 40f, "c3e_06_low_angle_home_to_grown.png");

            report.AppendLine();
            report.AppendLine("Pass 2 — Toggle OFF -> ON (Continuous Mode): ok=" + pass2Ok);
            report.AppendLine($"  legacyPathTiles={pass2LegacyPaths} (expected 0), authoredStarterPathsActive={pass2AuthoredPathsActive}/2 (expected 0), authoredHutsAndHearthActive={pass2AuthoredHutsHearthActive}/3");
            report.AppendLine($"  preserved props: clearings={pass2Clearings}, stones={pass2Stones}, grownHuts={pass2GrownHuts}");
            report.AppendLine($"  starterHuts: Hut_A={starterAConnected}, Hut_B={starterBConnected}, duplicateNearStarterSkipped={duplicateStarterSkipped}, totalHutsConnected={allHutsConnected} ({totalHutCount} huts)");
            report.AppendLine($"  topology: nodes={plan?.Nodes.Count}, edges={plan?.Edges.Count}, tJunctions={tJunctions}, fourWayJunctions={fourWayJunctions}, starterIssues={plan?.StarterIssues.Count}, planHash={pass2PlanHash}");
            report.AppendLine($"  pondCheck: waterViolations={waterViolations}, coreVertsInsidePond={pondVertexIntrusionCount}");
            if (build2 != null && stats2 != null)
            {
                report.AppendLine(string.Format(inv,
                    "  mesh: chunks={0}, core={1}v/{2}t, fringe={3}v/{4}t, meshHash={5}",
                    stats2.ActiveChunkCount, stats2.TotalCoreVertices, stats2.TotalCoreTriangles, stats2.TotalFringeVertices, stats2.TotalFringeTriangles, pass2MeshHash));
                report.AppendLine(string.Format(inv,
                    "  geometry: clearance=[{0:0.0000},{1:0.0000}]m, downwardTris={2}, foldbacks={3} (prevented={4})",
                    build2.MinCoreGroundClearance, build2.MaxCoreGroundClearance, build2.DownwardTriangles, build2.FoldbackTriangles, build2.FoldbacksPrevented));
                report.AppendLine(string.Format(inv,
                    "  continuity: junctionPairs={0} maxDelta={1:0.00000}m; borderPairs={2} maxPosDelta={3:0.00000}m maxHeightDelta={4:0.00000}m",
                    build2.JunctionAttachPairs, build2.MaxJunctionAttachDelta, build2.BorderSharedPointPairs, build2.MaxBorderPositionDelta, build2.MaxBorderHeightDelta));
                report.AppendLine($"  rendererStats: activeMeshes={stats2.ActiveMeshCount}, liveOwned={stats2.LiveOwnedMeshCount}, created={stats2.TotalMeshesCreated}, destroyed={stats2.TotalMeshesDestroyed}, sharedMaterialsOnly={stats2.UsesSharedMaterialsOnly}");
            }

            if (plan != null)
            {
                report.AppendLine("  Hut Branch Details:");
                foreach (var h in plan.HutReports)
                {
                    report.AppendLine(string.Format(inv,
                        "    {0}: status={1} origYaw={2:0.0} effYaw={3:0.0} len={4:0.00}m dot={5:0.00} violations=[{6}]",
                        h.HutId, h.Status, h.OriginalYawDegrees, h.EffectiveYawDegrees, h.RouteLength, h.StartAlignmentDot,
                        string.Join(",", h.HardViolations)));
                }
            }

            // =========================================================================
            // PASS 3: Idempotent No-Op Rebuild
            // =========================================================================
            homeVisualizer.RebuildFromSave(save);
            var stats3 = renderer != null ? renderer.GetStats() : null;
            var pass3Ok = stats3 != null && stats3.TotalApplyCalls == pass2ApplyCalls && stats3.LiveOwnedMeshCount == stats2.LiveOwnedMeshCount;
            report.AppendLine();
            report.AppendLine($"Pass 3 — Idempotent No-Op Rebuild: ok={pass3Ok} (applyCalls={stats3?.TotalApplyCalls} == {pass2ApplyCalls})");

            // =========================================================================
            // PASS 4: WaterSource Transform Invalidation
            // =========================================================================
            var origWaterPos = waterSource.transform.localPosition;
            var origWaterRot = waterSource.transform.localRotation;
            waterSource.transform.localPosition = origWaterPos + new Vector3(0.45f, 0f, -0.35f);
            waterSource.transform.localRotation = Quaternion.Euler(0f, 15f, 0f);
            homeVisualizer.RebuildFromSave(save);
            var stats4 = renderer != null ? renderer.GetStats() : null;
            var movedPondCenter = WaterSourceLayout.GetWorldCenter();
            var movedPondIntrusions = CountCoreVerticesInsidePond(renderer, movedPondCenter, WaterSourceLayout.WaterHalfExtentX, WaterSourceLayout.WaterHalfExtentZ);
            var pass4Ok = stats4 != null && stats4.TotalApplyCalls == pass2ApplyCalls + 1 && movedPondIntrusions == 0;

            // Restore WaterSource back to authored pose
            waterSource.transform.localPosition = origWaterPos;
            waterSource.transform.localRotation = origWaterRot;
            WaterSourceLayout.ClearCenterCache();

            report.AppendLine();
            report.AppendLine($"Pass 4 — WaterSource Transform Invalidation: ok={pass4Ok} (applyCalls={stats4?.TotalApplyCalls}, movedPondIntrusions={movedPondIntrusions})");

            // =========================================================================
            // PASS 5: Toggle ON -> OFF (Clean Rollback & Zero Mesh Leaks)
            // =========================================================================
            homeVisualizer.UseContinuousPathNetworkRenderer = false;
            homeVisualizer.RebuildFromSave(save);

            var statsAfterOff = homeVisualizer.LastClearedRendererStats;
            var pass5LegacyPaths = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");
            var pass5AuthoredPathsActive = (authoredPathMain.activeSelf ? 1 : 0) + (authoredPathCross.activeSelf ? 1 : 0);
            var pass5RootGone = GameObject.Find("SettlementPathNetwork") == null && homeVisualizer.ContinuousPathRenderer == null;
            var pass5ZeroLeaks = statsAfterOff != null &&
                                 statsAfterOff.LiveOwnedMeshCount == 0 &&
                                 statsAfterOff.TotalMeshesCreated == statsAfterOff.TotalMeshesDestroyed;
            var pass5Ok = pass5RootGone &&
                          pass5ZeroLeaks &&
                          pass5LegacyPaths == pass1LegacyPaths &&
                          pass5AuthoredPathsActive == 2;

            report.AppendLine();
            report.AppendLine($"Pass 5 — Toggle ON -> OFF (Rollback & Zero Mesh Leaks): ok={pass5Ok}");
            report.AppendLine($"  rootDestroyed={pass5RootGone}, zeroLeaks={pass5ZeroLeaks} (created={statsAfterOff?.TotalMeshesCreated}, destroyed={statsAfterOff?.TotalMeshesDestroyed}, liveOwned={statsAfterOff?.LiveOwnedMeshCount})");
            report.AppendLine($"  legacyPathTilesRestored={pass5LegacyPaths}/{pass1LegacyPaths}, authoredStarterPathsRestored={pass5AuthoredPathsActive}/2");

            // =========================================================================
            // PASS 6: Toggle OFF -> ON Again (Deterministic Re-enable)
            // =========================================================================
            homeVisualizer.UseContinuousPathNetworkRenderer = true;
            homeVisualizer.RebuildFromSave(save);

            var renderer6 = homeVisualizer.ContinuousPathRenderer;
            var plan6 = homeVisualizer.LastContinuousPathPlan;
            var stats6 = renderer6 != null ? renderer6.GetStats() : null;
            var build6 = renderer6 != null ? renderer6.LastBuildReport : null;
            var pass6LegacyPaths = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");
            var pass6AuthoredPathsActive = (authoredPathMain.activeSelf ? 1 : 0) + (authoredPathCross.activeSelf ? 1 : 0);
            var pass6Ok = renderer6 != null &&
                          plan6 != null &&
                          build6 != null &&
                          stats6 != null &&
                          plan6.TopologyHash == pass2PlanHash &&
                          build6.MeshHash == pass2MeshHash &&
                          pass6LegacyPaths == 0 &&
                          pass6AuthoredPathsActive == 0 &&
                          stats6.ActiveChunkCount == 4 &&
                          stats6.LiveOwnedMeshCount == 8 &&
                          stats6.UsesSharedMaterialsOnly;

            report.AppendLine();
            report.AppendLine($"Pass 6 — Toggle OFF -> ON Again (Deterministic Re-enable): ok={pass6Ok}");
            report.AppendLine($"  planHashMatch={plan6?.TopologyHash == pass2PlanHash}, meshHashMatch={build6?.MeshHash == pass2MeshHash}, legacyPathTiles={pass6LegacyPaths}, liveOwnedMeshes={stats6?.LiveOwnedMeshCount}");

            // Save isolated C-3E review scene cleanly
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            // Verify protected scenes are untouched
            var protectedOk = true;
            report.AppendLine();
            report.AppendLine("Protected Scenes Unchanged Check:");
            foreach (var p in ProtectedScenes)
            {
                var after = FileSha256Short(p);
                var same = string.Equals(protectedHashesBefore[p], after, StringComparison.Ordinal);
                if (!same) protectedOk = false;
                report.AppendLine($"  {p}: before={protectedHashesBefore[p]} after={after} unchanged={same}");
            }

            var allOk = defaultFlagIsFalse && pass1Ok && pass2Ok && pass3Ok && pass4Ok && pass5Ok && pass6Ok && protectedOk;
            report.AppendLine();
            report.AppendLine("OVERALL STAGE C-3E VERIFICATION: " + (allOk ? "PASS" : "FAIL"));

            var reportText = report.ToString();
            File.WriteAllText(BuildReportPath, reportText, Encoding.UTF8);
            File.WriteAllText(DocsReportPath, reportText, Encoding.UTF8);

            if (!allOk && throwOnFail)
            {
                throw new InvalidOperationException("Stage C-3E integration verification FAILED:\n" + reportText);
            }

            return allOk;
        }

        private static bool IsConnectedStatus(SettlementPathTopology.HutConnectionStatus status)
        {
            return status == SettlementPathTopology.HutConnectionStatus.Connected ||
                   status == SettlementPathTopology.HutConnectionStatus.ConnectedWithVisualDoorCorrection;
        }

        private static GameSaveData BuildIntegrationTestSave()
        {
            var save = new GameSaveData
            {
                worldGrowth = new WorldGrowthSaveData
                {
                    growthSeed = 314159,
                    chunks = new List<ChunkGrowthSaveData>()
                }
            };

            // -----------------------------------------------------------------
            // Chunk (0,0): Legacy home island (isLegacyBase = true)
            // -----------------------------------------------------------------
            var settle00 = new byte[ChunkMaskCodec.CellCount];
            var biome00 = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < biome00.Length; i++) biome00[i] = (byte)BiomeType.Mossy;

            // Starter settlement + roads exiting East (x = 8..15, z = 8) & North (x = 8, z = 8..15)
            // World cell (lx=8, lz=8) = world (+0.5, +0.5), inside SettlementYard
            for (var x = 8; x <= 15; x++) SetCell(settle00, x, 8, SettlementStage.Path);
            for (var z = 8; z <= 15; z++) SetCell(settle00, 8, z, SettlementStage.Path);
            // South branch from yard around the pond (west of pond)
            for (var z = 5; z <= 7; z++) SetCell(settle00, 7, z, SettlementStage.Path);
            SetCell(settle00, 6, 5, SettlementStage.Path);
            SetCell(settle00, 5, 5, SettlementStage.Clearing);
            SetCell(settle00, 11, 8, SettlementStage.Stone);
            // Cell inside WaterSource (1.8, -1.4) -> lx=9, lz=6 (verifies pond filtering)
            SetCell(settle00, 9, 6, SettlementStage.Path);
            // Duplicate-candidate hut cell at lx=6, lz=8 -> world (-1.5, +0.5), 0.41m from Starter_Hut_A (-1.6, 0.9)
            SetCell(settle00, 6, 8, SettlementStage.Hut);

            save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
            {
                chunkX = 0,
                chunkZ = 0,
                isLegacyBase = true,
                growthSeed = 1001,
                stageMaskBase64 = string.Empty,
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biome00),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle00)
            });

            // -----------------------------------------------------------------
            // Chunk (1,0): Grown chunk East of home (4-way crossroads + T-junction + 2 huts)
            // -----------------------------------------------------------------
            var land10 = FillIslandLand(1, 0);
            var biome10 = FillBiome(BiomeType.Mossy);
            var settle10 = new byte[ChunkMaskCodec.CellCount];

            // East-west road across X chunk border from (0,0): lx=0..11, lz=8
            for (var x = 0; x <= 11; x++) SetCell(settle10, x, 8, SettlementStage.Path);
            // North-south road crossing at (5, 8) -> creates 4-way crossroads at (5, 8) and crosses Z border into (1,1)!
            for (var z = 4; z <= 15; z++) SetCell(settle10, 5, z, SettlementStage.Path);
            // Second branch south from (9, 8) -> T-junction at (9, 8)
            for (var z = 5; z <= 8; z++) SetCell(settle10, 9, z, SettlementStage.Path);
            SetCell(settle10, 7, 8, SettlementStage.Stone);
            SetCell(settle10, 3, 6, SettlementStage.Clearing);
            // Two grown huts (even and odd indices -> tests both Hut_A and Hut_B variants)
            SetCell(settle10, 6, 4, SettlementStage.Hut); // index = 4*16+6 = 70 (even -> Hut_A)
            SetCell(settle10, 11, 5, SettlementStage.Hut); // index = 5*16+11 = 91 (odd -> Hut_B)

            save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
            {
                chunkX = 1,
                chunkZ = 0,
                isLegacyBase = false,
                growthSeed = 2002,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land10),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biome10),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle10)
            });

            // -----------------------------------------------------------------
            // Chunk (0,1): Grown chunk North of home (Z-border crossing + T-junction + X-border to (1,1))
            // -----------------------------------------------------------------
            var land01 = FillIslandLand(0, 1);
            var biome01 = FillBiome(BiomeType.Forest);
            var settle01 = new byte[ChunkMaskCodec.CellCount];

            // Continuation from (0,0) across Z border: lx=8, lz=0..9
            for (var z = 0; z <= 9; z++) SetCell(settle01, 8, z, SettlementStage.Path);
            // East branch across X border into (1,1): lx=8..15, lz=5 -> T-junction at (8,5)
            for (var x = 8; x <= 15; x++) SetCell(settle01, x, 5, SettlementStage.Path);
            SetCell(settle01, 11, 5, SettlementStage.Stone);
            // Grown hut near north end
            SetCell(settle01, 5, 9, SettlementStage.Hut);

            save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
            {
                chunkX = 0,
                chunkZ = 1,
                isLegacyBase = false,
                growthSeed = 3003,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land01),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biome01),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle01)
            });

            // -----------------------------------------------------------------
            // Chunk (1,1): Grown chunk NE (receives both X-border from (0,1) and Z-border from (1,0))
            // -----------------------------------------------------------------
            var land11 = FillIslandLand(1, 1);
            var biome11 = FillBiome(BiomeType.Mossy);
            var settle11 = new byte[ChunkMaskCodec.CellCount];

            // Meets (0,1) at (0,5) and (1,0) at (5,0) -> L/T junction at (5,5)
            for (var x = 0; x <= 8; x++) SetCell(settle11, x, 5, SettlementStage.Path);
            for (var z = 0; z <= 8; z++) SetCell(settle11, 5, z, SettlementStage.Path);
            SetCell(settle11, 8, 7, SettlementStage.Hut);

            save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
            {
                chunkX = 1,
                chunkZ = 1,
                isLegacyBase = false,
                growthSeed = 4004,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land11),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biome11),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle11)
            });

            return save;
        }

        private static byte[] FillIslandLand(int chunkX, int chunkZ)
        {
            var land = new byte[ChunkMaskCodec.CellCount];
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

        private static byte[] FillBiome(BiomeType biome)
        {
            var arr = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < arr.Length; i++) arr[i] = (byte)biome;
            return arr;
        }

        private static void SetCell(byte[] mask, int lx, int lz, SettlementStage stage)
        {
            mask[ChunkMaskCodec.CellIndex(lx, lz)] = (byte)stage;
        }

        private static GameObject BuildAuthoredHomeIsland(
            Transform world,
            Material matBaseRock,
            Material matMidPlateau,
            Material matYard,
            Material matPond,
            Material matAuthoredPath,
            Material matHearthStone,
            Material matHearthFire,
            out GameObject waterSource,
            out GameObject authoredPathMain,
            out GameObject authoredPathCross,
            out GameObject authoredHutA,
            out GameObject authoredHutB,
            out GameObject authoredHearth)
        {
            var island = new GameObject("Island");
            island.transform.SetParent(world, false);

            CreatePlatformCylinder(island.transform, "BaseRock", new Vector3(0f, 0.2f, 0f), new Vector3(17.2f, 0.6f, 13.6f), matBaseRock);
            CreatePlatformCylinder(island.transform, "MidPlateau", new Vector3(0f, 0.9f, 0f), new Vector3(12.0f, 0.5f, 10.0f), matMidPlateau);
            CreatePlatformCylinder(island.transform, "SettlementYard", new Vector3(0f, 1.55f, 0f), new Vector3(7.4f, 0.3f, 6.4f), matYard);

            waterSource = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            waterSource.name = "WaterSource";
            waterSource.transform.SetParent(island.transform, false);
            waterSource.transform.localPosition = WaterSourceLayout.LocalPosition;
            waterSource.transform.localScale = WaterSourceLayout.LocalScale;
            ApplyMatAndStripCollider(waterSource, matPond);

            var settlement = new GameObject("Settlement");
            settlement.transform.SetParent(island.transform, false);

            authoredPathMain = CreateBox(settlement.transform, "Path_Main", new Vector3(0f, 1.88f, 0.2f), new Vector3(1.1f, 0.06f, 4.2f), matAuthoredPath);
            authoredPathCross = CreateBox(settlement.transform, "Path_Cross", new Vector3(0.9f, 1.88f, -0.6f), new Vector3(2.4f, 0.05f, 0.7f), matAuthoredPath);

            authoredHutA = SpawnStarterHut(settlement.transform, "Hut_A", "AfkIsland/Settlement/Hut_A", new Vector3(-1.6f, 1.85f, 0.9f), 12f);
            authoredHutB = SpawnStarterHut(settlement.transform, "Hut_B", "AfkIsland/Settlement/Hut_B", new Vector3(1.8f, 1.85f, -0.2f), -25f);

            authoredHearth = CreateBox(settlement.transform, "Hearth", new Vector3(0.15f, 1.92f, -1.5f), new Vector3(0.9f, 0.18f, 0.9f), matHearthStone);
            CreateBox(settlement.transform, "HearthFire", new Vector3(0.15f, 2.05f, -1.5f), new Vector3(0.35f, 0.25f, 0.35f), matHearthFire);

            return island;
        }

        private static GameObject SpawnStarterHut(Transform parent, string name, string resourcePath, Vector3 localPos, float yaw)
        {
            var prefab = Resources.Load<GameObject>(resourcePath);
            GameObject go;
            if (prefab != null)
            {
                go = UnityEngine.Object.Instantiate(prefab, parent);
                go.name = name;
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
                go.transform.SetParent(parent, false);
            }

            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * SettlementCellQuery.HutVisualUniformScale;
            return go;
        }

        private static void PlaceBorderStakes(Transform parent, Material matX, Material matZ)
        {
            var root = new GameObject("ChunkBorderMarkers");
            root.transform.SetParent(parent, false);
            foreach (var z in new[] { -2.5f, 3.5f, 11.5f })
            {
                CreateStake(root.transform, $"BorderX_{z:0}", new Vector3(8.0f, 1.2f, z), matX);
            }

            foreach (var x in new[] { -2.0f, 4.0f, 12.5f })
            {
                CreateStake(root.transform, $"BorderZ_{x:0}", new Vector3(x, 1.2f, 8.0f), matZ);
            }
        }

        private static void CreateStake(Transform parent, string name, Vector3 pos, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(0.14f, 0.85f, 0.14f);
            ApplyMatAndStripCollider(go, mat);
        }

        private static void CreatePlatformCylinder(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            ApplyMatAndStripCollider(go, mat);
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
            if (r != null && mat != null)
            {
                r.sharedMaterial = mat;
                r.shadowCastingMode = ShadowCastingMode.On;
                r.receiveShadows = true;
            }

            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                UnityEngine.Object.DestroyImmediate(col);
            }
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
            ocean.transform.position = new Vector3(4f, -0.35f, 4f);
            ocean.transform.localScale = new Vector3(24f, 1f, 24f);
            ApplyMatAndStripCollider(ocean, matOcean);
        }

        private static Camera CreateReviewCamera()
        {
            var camGo = new GameObject("StageC3E_Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.34f, 0.68f, 0.82f);
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 200f;
            cam.fieldOfView = 42f;
            cam.transform.position = new Vector3(14f, 22f, -15f);
            cam.transform.LookAt(new Vector3(4.5f, 1.0f, 3.5f));
            return cam;
        }

        private static void RenderCameraShot(Camera cam, Vector3 pos, Vector3 target, float fov, string fileName)
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
                // Warmup render ensures async shader compilation completes cleanly in batchmode.
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
                    if (child.name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        count++;
                    }
                    else
                    {
                        stack.Push(child);
                    }
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
                if (!renderer.TryGetChunkMeshes(kv.Key, out var coreMesh, out _) || coreMesh == null)
                    continue;

                var chunkOrigin = GrowthWorldPlacement.ChunkOriginXZ(kv.Key.x, kv.Key.y);
                var verts = coreMesh.vertices;
                for (var i = 0; i < verts.Length; i++)
                {
                    var wx = chunkOrigin.x + verts[i].x;
                    var wz = chunkOrigin.y + verts[i].z;
                    var dx = (wx - pondCenter.x) / rx;
                    var dz = (wz - pondCenter.z) / rz;
                    if (dx * dx + dz * dz < 0.96f)
                    {
                        inside++;
                    }
                }
            }

            return inside;
        }

        private static Material CreateOrUpdateMaterial(string assetPath, Color color)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                         ?? Shader.Find("Standard");
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, assetPath);
            }
            else if (shader != null && mat.shader != shader)
            {
                mat.shader = shader;
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.12f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void EnsureFolder(string folder)
        {
            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
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
