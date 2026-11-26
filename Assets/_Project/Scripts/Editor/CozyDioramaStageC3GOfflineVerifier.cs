using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AfkIsland.EditorTools;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace AfkIsland.Editor
{
    /// <summary>
    /// Stage C-3G full verification suite in Unity 6:
    /// 1. Runs all 8 CozyDioramaStageC3GJunctionTests mask-level regression tests.
    /// 2. Runs deterministic +48h offline growth on two identical copies of the Save v8 fixture
    ///    and verifies byte-for-byte identical JSON SHA-256.
    /// 3. Audits every pre-existing Path junction cell (degree >= 3) across chunks and verifies
    ///    zero junctions were promoted to Hut, and that every new Hut grew on a non-junction cell.
    /// 4. Rebuilds the continuous path network after offline growth and verifies single connected
    ///    component (ConnectedComponents == 1), all huts connected, Save v8 immutability, zero
    ///    foldbacks/pond intrusions/leaks, and 0.00000m chunk border continuity.
    /// Does NOT touch MainIsland.unity, main, persistent saves, or the default feature flag (false).
    /// </summary>
    public static class CozyDioramaStageC3GOfflineVerifier
    {
        private const string BuildShotsDir = "Builds/cozy-diorama/stage-c3g-shots";
        private const string DocsShotsDir = "Docs/CozyDiorama/StageC3G-Shots";
        private const string BuildReportPath = "Builds/cozy-diorama/stage-c3g-report.txt";
        private const string DocsReportPath = "Docs/CozyDiorama/StageC3G-Shots/c3g-verification-report.txt";
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

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3G/Run Full Offline Growth & Connectivity Verification")]
        public static void RunVerificationMenu()
        {
            var ok = RunVerificationInternal();
            Debug.Log(ok
                ? "[AFK Island] Stage C-3G full verification PASSED. Report: " + DocsReportPath
                : "[AFK Island] Stage C-3G full verification FAILED. Report: " + DocsReportPath);
        }

        public static void RunBatchVerification()
        {
            var ok = RunVerificationInternal();
            Debug.Log("[AFK Island] Stage C-3G batch verification finished: ok=" + ok);
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
            report.AppendLine("Stage C-3G — Junction Preservation, Offline Determinism & Post-Growth Connectivity Report");
            report.AppendLine("Execution mode: Isolated empty editor scene + in-memory SaveClone copies (zero disk save writes)");

            // =========================================================================
            // CHECK 1: All 15 C-3G junction + bridge/cut-vertex + end-to-end Apply tests
            // =========================================================================
            var unitOk = CozyDioramaStageC3GJunctionTests.RunAll(out var unitPassed, out var unitFailed);
            var check1Ok = unitOk && unitPassed == 15 && unitFailed == 0;
            report.AppendLine();
            report.AppendLine($"Check 1 — C-3G 15 Junction, Bridge & End-to-End Regression Tests: ok={check1Ok} (passed={unitPassed}, failed={unitFailed})");

            // =========================================================================
            // CHECK 2: Deterministic +48h offline growth on TWO identical Save v8 copies
            // =========================================================================
            var baseSave = BuildSaveV8Fixture();

            // Enumerate all pre-existing Path junctions (degree >= 3) and existing huts before growth
            var preJunctions = FindPathJunctionCells(baseSave);
            var preHuts = FindHutCells(baseSave);

            var runA = RunOfflineGrowthCopy(baseSave, TimeSpan.FromHours(48), out var simA);
            var runB = RunOfflineGrowthCopy(baseSave, TimeSpan.FromHours(48), out var simB);

            var shaA = SaveJsonSha256(runA);
            var shaB = SaveJsonSha256(runB);
            var check2Ok =
                simA != null && simA.success && simA.appliedSeconds == 172800 &&
                simB != null && simB.success && simB.appliedSeconds == 172800 &&
                shaA == shaB;

            report.AppendLine();
            report.AppendLine($"Check 2 — Deterministic Offline Growth (+48h on 2 Identical Copies): ok={check2Ok}");
            report.AppendLine($"  appliedSecondsA={simA?.appliedSeconds}, appliedSecondsB={simB?.appliedSeconds}");
            report.AppendLine($"  shaCopyA={shaA}, shaCopyB={shaB}, identical={shaA == shaB}");

            // =========================================================================
            // CHECK 3: Junction preservation audit & new hut delta audit
            // =========================================================================
            var junctionsPreserved = 0;
            var junctionsTurnedIntoHut = new List<string>();
            for (var i = 0; i < preJunctions.Count; i++)
            {
                var j = preJunctions[i];
                var afterStage = SampleCellStage(runA, j.chunkX, j.chunkZ, j.cellIndex);
                if (afterStage == (byte)SettlementStage.Hut)
                {
                    junctionsTurnedIntoHut.Add($"chunk({j.chunkX},{j.chunkZ}) local({j.localX},{j.localZ}) preDeg={j.degree} -> Hut");
                }
                else if (afterStage == (byte)SettlementStage.Path || afterStage == (byte)SettlementStage.Stone)
                {
                    junctionsPreserved++;
                }
            }

            var postHuts = FindHutCells(runA);
            var newHuts = new List<string>();
            var newHutsOnPreJunction = 0;
            var preJunctionSet = new HashSet<string>();
            for (var i = 0; i < preJunctions.Count; i++)
            {
                var j = preJunctions[i];
                preJunctionSet.Add($"{j.chunkX}:{j.chunkZ}:{j.cellIndex}");
            }

            var preHutSet = new HashSet<string>();
            for (var i = 0; i < preHuts.Count; i++)
            {
                preHutSet.Add($"{preHuts[i].chunkX}:{preHuts[i].chunkZ}:{preHuts[i].cellIndex}");
            }

            for (var i = 0; i < postHuts.Count; i++)
            {
                var h = postHuts[i];
                var key = $"{h.chunkX}:{h.chunkZ}:{h.cellIndex}";
                if (preHutSet.Contains(key)) continue;

                var preStage = (SettlementStage)SampleCellStage(baseSave, h.chunkX, h.chunkZ, h.cellIndex);
                var preDeg = SettlementSimulation.CountCardinalRoadNeighbors(baseSave, h.chunkX, h.chunkZ, h.cellIndex);
                var wasJunction = preJunctionSet.Contains(key);
                if (wasJunction) newHutsOnPreJunction++;
                newHuts.Add($"chunk({h.chunkX},{h.chunkZ}) local({h.localX},{h.localZ}) preStage={preStage} preCardinalDeg={preDeg} wasJunction={wasJunction}");
            }

            // Explicitly verify key crossroads: (1,0)@(5,8), (1,0)@(9,8), (0,1)@(8,5), (1,1)@(5,5)
            var key10Cross = (SettlementStage)SampleCellStage(runA, 1, 0, ChunkMaskCodec.CellIndex(5, 8));
            var key10Tee = (SettlementStage)SampleCellStage(runA, 1, 0, ChunkMaskCodec.CellIndex(9, 8));
            var key01Tee = (SettlementStage)SampleCellStage(runA, 0, 1, ChunkMaskCodec.CellIndex(8, 5));
            var key11Cross = (SettlementStage)SampleCellStage(runA, 1, 1, ChunkMaskCodec.CellIndex(5, 5));

            var keyJunctionsIntact =
                key10Cross != SettlementStage.Hut &&
                key10Tee != SettlementStage.Hut &&
                key01Tee != SettlementStage.Hut &&
                key11Cross != SettlementStage.Hut;

            var check3Ok =
                preJunctions.Count >= 4 &&
                junctionsPreserved == preJunctions.Count &&
                junctionsTurnedIntoHut.Count == 0 &&
                newHutsOnPreJunction == 0 &&
                keyJunctionsIntact;

            report.AppendLine();
            report.AppendLine($"Check 3 — Pre-Existing Path Junctions Protected & Hut Growth Delta: ok={check3Ok}");
            report.AppendLine($"  preJunctionsTotal={preJunctions.Count}, preservedAsRoad={junctionsPreserved}, promotedToHut={junctionsTurnedIntoHut.Count}");
            report.AppendLine($"  keyJunctionsAfter48h: chunk(1,0)@(5,8)={key10Cross}, chunk(1,0)@(9,8)={key10Tee}, chunk(0,1)@(8,5)={key01Tee}, chunk(1,1)@(5,5)={key11Cross}");
            report.AppendLine($"  maskHuts: before={preHuts.Count} -> after={postHuts.Count} (newMaskHuts={newHuts.Count}, newHutsOnPreJunction={newHutsOnPreJunction})");
            for (var i = 0; i < newHuts.Count; i++)
            {
                report.AppendLine("    newHut: " + newHuts[i]);
            }

            // =========================================================================
            // CHECK 4: Post-growth continuous path network connectivity & visual verification
            // =========================================================================
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "StageC3G_TempVerification";

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
            BuildAuthoredHomeIsland(world.transform, matBaseRock, matMidPlateau, matYard, matPond, matAuthoredPath, matHearthStone, matHearthFire);
            PlaceBorderStakes(world.transform, matBorderX, matBorderZ);

            var chunkManager = world.AddComponent<ChunkManager>();
            var islandMeshVisualizer = world.AddComponent<IslandMeshVisualizer>();
            islandMeshVisualizer.Configure(null, chunkManager);
            var homeVisualizer = world.AddComponent<HomeSettlementVisualizer>();
            var defaultFlagFalse = !homeVisualizer.UseContinuousPathNetworkRenderer;
            homeVisualizer.Configure(null, chunkManager);

            // Measure pre-growth baseline topology on unmodified baseSave first
            var preSaveCopy = SaveClone.DeepClone(baseSave);
            islandMeshVisualizer.RebuildFromSave(preSaveCopy);
            homeVisualizer.UseContinuousPathNetworkRenderer = true;
            homeVisualizer.RebuildFromSave(preSaveCopy);
            var prePlan = homeVisualizer.LastContinuousPathPlan;
            var preConnectedComponents = prePlan != null ? prePlan.ConnectedComponents : -1;
            var preTopologyHuts = prePlan != null ? prePlan.HutReports.Count : 0;

            islandMeshVisualizer.RebuildFromSave(runA);
            homeVisualizer.UseContinuousPathNetworkRenderer = true;
            homeVisualizer.RebuildFromSave(runA);
            var shaAfterVisual = SaveJsonSha256(runA);

            var plan = homeVisualizer.LastContinuousPathPlan;
            var renderer = homeVisualizer.ContinuousPathRenderer;
            var stats = renderer != null ? renderer.GetStats() : null;
            var build = renderer != null ? renderer.LastBuildReport : null;
            var legacyPaths = CountNamedChildren(homeVisualizer.GrownSettlementRoot, "Path_");
            var pondIntrusions = CountCoreVerticesInsidePond(renderer, WaterSourceLayout.GetWorldCenter(), WaterSourceLayout.WaterHalfExtentX, WaterSourceLayout.WaterHalfExtentZ);

            var connectedHuts = 0;
            var totalHuts = plan != null ? plan.HutReports.Count : 0;
            var tJunctions = 0;
            var fourWayJunctions = 0;
            if (plan != null)
            {
                for (var i = 0; i < plan.HutReports.Count; i++)
                {
                    var st = plan.HutReports[i].Status;
                    if (st == SettlementPathTopology.HutConnectionStatus.Connected ||
                        st == SettlementPathTopology.HutConnectionStatus.ConnectedWithVisualDoorCorrection)
                    {
                        connectedHuts++;
                    }
                }

                for (var i = 0; i < plan.Nodes.Count; i++)
                {
                    if (plan.Nodes[i].Degree == 3) tJunctions++;
                    else if (plan.Nodes[i].Degree >= 4) fourWayJunctions++;
                }
            }

            var cam = CreateCamera();
            RenderShot(cam, new Vector3(14f, 22f, -15f), new Vector3(4.5f, 1.0f, 3.5f), 42f, "c3g_01_after_48h_offline_overview.png");
            RenderShot(cam, new Vector3(14.5f, 11.5f, -9.5f), new Vector3(13.5f, 0.7f, 0.5f), 38f, "c3g_02_chunk10_4way_preserved.png");
            RenderShot(cam, new Vector3(16.0f, 13.5f, 2.0f), new Vector3(13.0f, 0.7f, 13.0f), 40f, "c3g_03_chunk11_junction_preserved.png");

            // Rollback OFF and verify zero mesh leaks
            homeVisualizer.UseContinuousPathNetworkRenderer = false;
            homeVisualizer.RebuildFromSave(runA);
            var cleared = homeVisualizer.LastClearedRendererStats;
            var zeroLeaks = cleared != null && cleared.LiveOwnedMeshCount == 0 && cleared.TotalMeshesCreated == cleared.TotalMeshesDestroyed;

            var visualHealthOk =
                defaultFlagFalse &&
                shaAfterVisual == shaA &&
                plan != null &&
                stats != null &&
                build != null &&
                connectedHuts == totalHuts &&
                totalHuts >= 6 &&
                tJunctions >= 2 &&
                fourWayJunctions >= 2 &&
                plan.StarterIssues.Count == 0 &&
                legacyPaths == 0 &&
                pondIntrusions == 0 &&
                build.DownwardTriangles == 0 &&
                build.FoldbackTriangles == 0 &&
                build.MaxBorderPositionDelta <= 0.0001f &&
                build.MaxBorderHeightDelta <= 0.0001f &&
                stats.UsesSharedMaterialsOnly &&
                stats.LiveOwnedMeshCount == stats.ActiveChunkCount * 2 &&
                zeroLeaks;

            var singleComponentOk = plan != null && plan.ConnectedComponents == 1;
            var check4Ok = visualHealthOk && singleComponentOk;

            report.AppendLine();
            report.AppendLine($"Check 4a — Post-Growth Visual, Geometry, Immutability & Hut Health: ok={visualHealthOk}");
            report.AppendLine($"  defaultFlagFalse={defaultFlagFalse}, saveImmutabilityAfterVisual={shaAfterVisual == shaA}");
            report.AppendLine($"  huts: before={preTopologyHuts} -> after={totalHuts}, connectedHuts={connectedHuts}/{totalHuts}");
            report.AppendLine($"  topology: nodes={plan?.Nodes.Count}, edges={plan?.Edges.Count}, tJunctions={tJunctions}, fourWayJunctions={fourWayJunctions}, starterIssues={plan?.StarterIssues.Count}");
            report.AppendLine(string.Format(inv,
                "  geometry: chunks={0}, legacyPaths={1}, pondIntrusions={2}, downwardTris={3}, foldbacks={4}, maxBorderPos={5:0.00000}m, maxBorderHeight={6:0.00000}m, sharedMaterialsOnly={7}, rollbackZeroLeaks={8}",
                stats?.ActiveChunkCount, legacyPaths, pondIntrusions, build?.DownwardTriangles, build?.FoldbackTriangles,
                build != null ? build.MaxBorderPositionDelta : -1f, build != null ? build.MaxBorderHeightDelta : -1f,
                stats?.UsesSharedMaterialsOnly, zeroLeaks));
            report.AppendLine();
            report.AppendLine($"Check 4b — Post-Growth Single-Component Path Network Connectivity: ok={singleComponentOk}");
            report.AppendLine($"  connectedComponents: before48h={preConnectedComponents} -> after48h={plan?.ConnectedComponents} (expected 1)");
            if (plan != null && !singleComponentOk)
            {
                AppendComponentDiagnostics(report, plan);
            }

            // =========================================================================
            // CHECK 5: Protected scenes unchanged
            // =========================================================================
            var protectedOk = true;
            report.AppendLine();
            report.AppendLine("Check 5 — Protected Scenes Unchanged:");
            foreach (var p in ProtectedScenes)
            {
                var after = FileSha256Short(p);
                var same = string.Equals(protectedBefore[p], after, StringComparison.Ordinal);
                if (!same) protectedOk = false;
                report.AppendLine($"  {p}: before={protectedBefore[p]} after={after} unchanged={same}");
            }

            var allOk = check1Ok && check2Ok && check3Ok && check4Ok && protectedOk;
            report.AppendLine();
            report.AppendLine("OVERALL STAGE C-3G VERIFICATION: " + (allOk ? "PASS" : "FAIL"));

            var text = report.ToString();
            File.WriteAllText(BuildReportPath, text, Encoding.UTF8);
            File.WriteAllText(DocsReportPath, text, Encoding.UTF8);

            UnityEngine.Object.DestroyImmediate(world);
            return allOk;
        }

        private struct CellRecord
        {
            public int chunkX;
            public int chunkZ;
            public int cellIndex;
            public int localX;
            public int localZ;
            public int degree;
        }

        private static List<CellRecord> FindPathJunctionCells(GameSaveData save)
        {
            var list = new List<CellRecord>();
            foreach (var g in save.worldGrowth.chunks)
            {
                if (g == null || !ChunkMaskCodec.TryDecodeBase64(g.settlementMaskBase64, out var mask, out _))
                    continue;
                for (var i = 0; i < mask.Length; i++)
                {
                    if (mask[i] != (byte)SettlementStage.Path) continue;
                    var deg = SettlementSimulation.CountCardinalRoadNeighbors(save, g.chunkX, g.chunkZ, i);
                    if (deg >= 3)
                    {
                        ChunkMaskCodec.IndexToLocal(i, out var lx, out var lz);
                        list.Add(new CellRecord { chunkX = g.chunkX, chunkZ = g.chunkZ, cellIndex = i, localX = lx, localZ = lz, degree = deg });
                    }
                }
            }
            return list;
        }

        private static List<CellRecord> FindHutCells(GameSaveData save)
        {
            var list = new List<CellRecord>();
            foreach (var g in save.worldGrowth.chunks)
            {
                if (g == null || !ChunkMaskCodec.TryDecodeBase64(g.settlementMaskBase64, out var mask, out _))
                    continue;
                for (var i = 0; i < mask.Length; i++)
                {
                    if (mask[i] != (byte)SettlementStage.Hut) continue;
                    ChunkMaskCodec.IndexToLocal(i, out var lx, out var lz);
                    list.Add(new CellRecord { chunkX = g.chunkX, chunkZ = g.chunkZ, cellIndex = i, localX = lx, localZ = lz });
                }
            }
            return list;
        }

        private static byte SampleCellStage(GameSaveData save, int cx, int cz, int cellIndex)
        {
            foreach (var g in save.worldGrowth.chunks)
            {
                if (g != null && g.chunkX == cx && g.chunkZ == cz &&
                    ChunkMaskCodec.TryDecodeBase64(g.settlementMaskBase64, out var mask, out _) &&
                    cellIndex >= 0 && cellIndex < mask.Length)
                {
                    return mask[cellIndex];
                }
            }
            return (byte)SettlementStage.None;
        }

        private static GameSaveData RunOfflineGrowthCopy(GameSaveData source, TimeSpan elapsed, out GrowthSimulationResult simResult)
        {
            var copy = SaveClone.DeepClone(source);
            var sim = new WorldGrowthSimulation();
            simResult = sim.Simulate(copy, elapsed, WorldGrowthConfig.CreateDefault());
            var working = simResult != null && simResult.success && simResult.updatedSave != null ? simResult.updatedSave : copy;
            if (simResult != null && simResult.success)
            {
                SaveMigrationService.EnsureEcosystemWeatherAnimals(working);
                var rng = new DeterministicRng(unchecked((uint)(working.worldGrowth.growthSeed ^ (int)simResult.appliedSeconds ^ 0xEC05)));
                SettlementSimulation.Apply(working, simResult.appliedSeconds, rng);
                SettlementSimulation.RebalanceExcessStones(working);
                working.worldGrowth.growthSeed = unchecked((int)rng.State);
            }
            return working;
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
            for (var x = 8; x <= 15; x++) SetCell(settle00, x, 8, SettlementStage.Path);
            for (var z = 8; z <= 15; z++) SetCell(settle00, 8, z, SettlementStage.Path);
            for (var z = 5; z <= 7; z++) SetCell(settle00, 7, z, SettlementStage.Path);
            SetCell(settle00, 6, 5, SettlementStage.Path);
            SetCell(settle00, 5, 5, SettlementStage.Clearing);
            SetCell(settle00, 11, 8, SettlementStage.Stone);
            SetCell(settle00, 6, 8, SettlementStage.Hut);

            var land00 = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < land00.Length; i++) land00[i] = (byte)CellStage.Moss;

            save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
            {
                chunkX = 0, chunkZ = 0, isLegacyBase = true, growthSeed = 1001,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land00),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(FillBiome(BiomeType.Mossy)),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle00)
            });
            save.chunks.Add(new ChunkSaveData { chunkX = 0, chunkZ = 0, isUnlocked = true, biomeId = "starter_island" });

            var settle10 = new byte[ChunkMaskCodec.CellCount];
            for (var x = 0; x <= 15; x++) SetCell(settle10, x, 8, SettlementStage.Path);
            for (var z = 4; z <= 15; z++) SetCell(settle10, 5, z, SettlementStage.Path);
            for (var z = 5; z <= 8; z++) SetCell(settle10, 9, z, SettlementStage.Path);
            SetCell(settle10, 7, 8, SettlementStage.Stone);
            SetCell(settle10, 3, 6, SettlementStage.Clearing);
            SetCell(settle10, 6, 4, SettlementStage.Hut);
            SetCell(settle10, 11, 5, SettlementStage.Hut);

            save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
            {
                chunkX = 1, chunkZ = 0, isLegacyBase = false, growthSeed = 2002,
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
                chunkX = 0, chunkZ = 1, isLegacyBase = false, growthSeed = 3003,
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
                chunkX = 1, chunkZ = 1, isLegacyBase = false, growthSeed = 4004,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(FillLand(1, 1, fullLand: false)),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(FillBiome(BiomeType.Mossy)),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle11)
            });
            save.chunks.Add(new ChunkSaveData { chunkX = 1, chunkZ = 1, isUnlocked = true, biomeId = "mossy" });

            return save;
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
            Transform world, Material matBaseRock, Material matMidPlateau, Material matYard,
            Material matPond, Material matAuthoredPath, Material matHearthStone, Material matHearthFire)
        {
            var island = new GameObject("Island");
            island.transform.SetParent(world, false);
            CreateCylinder(island.transform, "BaseRock", new Vector3(0f, 0.2f, 0f), new Vector3(17.2f, 0.6f, 13.6f), matBaseRock);
            CreateCylinder(island.transform, "MidPlateau", new Vector3(0f, 0.9f, 0f), new Vector3(12.0f, 0.5f, 10.0f), matMidPlateau);
            CreateCylinder(island.transform, "SettlementYard", new Vector3(0f, 1.55f, 0f), new Vector3(7.4f, 0.3f, 6.4f), matYard);
            CreateCylinder(island.transform, "WaterSource", WaterSourceLayout.LocalPosition, WaterSourceLayout.LocalScale, matPond);

            var settlement = new GameObject("Settlement");
            settlement.transform.SetParent(island.transform, false);
            CreateBox(settlement.transform, "Path_Main", new Vector3(0f, 1.88f, 0.2f), new Vector3(1.1f, 0.06f, 4.2f), matAuthoredPath);
            CreateBox(settlement.transform, "Path_Cross", new Vector3(0.9f, 1.88f, -0.6f), new Vector3(2.4f, 0.05f, 0.7f), matAuthoredPath);
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
            var camGo = new GameObject("StageC3G_Camera");
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
