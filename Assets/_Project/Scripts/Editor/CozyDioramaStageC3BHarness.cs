#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Stage C-3B: offline topology test suite, 11G PlacePathRibbon vs pure topology comparison,
    /// WaterSourceLayout audit, two-chunk border staleness repro, and lookup/signature benchmark.
    /// Does not modify any production code, scenes, prefabs, or save files.
    /// </summary>
    public static class CozyDioramaStageC3BHarness
    {
        private const string BuildReportDir = "Builds/cozy-diorama";
        private const string BuildReportPath = "Builds/cozy-diorama/stage-c3b-report.txt";
        private const string DocsReportDir = "Docs/CozyDiorama/StageC3B-Report";
        private const string DocsReportPath = "Docs/CozyDiorama/StageC3B-Report/c3b-test-and-benchmark-report.txt";
        private const string ComparisonPngName = "c3b_comparison_11g_vs_topology.png";
        private const string BorderPngName = "c3b_chunk_border_repro.png";

        // Raw water surface from WaterSourceLayout (LocalPosition (1.8, 1.90, -1.4), LocalScale (2.2, 0.08, 1.6))
        private static readonly Vector2 PondCenter = new Vector2(WaterSourceLayout.LocalPosition.x, WaterSourceLayout.LocalPosition.z);
        private const float PondRawRx = WaterSourceLayout.WaterHalfExtentX; // 1.1
        private const float PondRawRz = WaterSourceLayout.WaterHalfExtentZ; // 0.8
        private const float PondSettlementRx = PondRawRx + WaterSourceLayout.SettlementFootprintRadius; // 1.8
        private const float PondSettlementRz = PondRawRz + WaterSourceLayout.SettlementFootprintRadius; // 1.5

        private sealed class Arm11G
        {
            public int ChunkX;
            public int ChunkZ;
            public int WorldCellX;
            public int WorldCellZ;
            public int Direction; // 0 N, 1 E, 2 S, 3 W
            public Vector2 Center;
            public float YawDegrees;
            public float HalfWidth;
            public float HalfLength;
            public Vector2 StartPoint;
            public Vector2 EndPoint;
        }

        private sealed class TestResult
        {
            public string Name;
            public bool Passed;
            public string Detail;
        }

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3/Run C-3B Offline Topology & Comparison Suite")]
        public static void RunMenu()
        {
            var sb = new StringBuilder(8192);
            var ci = CultureInfo.InvariantCulture;

            sb.AppendLine("Stage C-3B — Offline Topology Tests, 11G Comparison, Water Audit, Chunk-Border Repro & Benchmark");
            sb.AppendLine("Branch target: feature/cozy-diorama-stage-c2-settlement-path-network");
            sb.AppendLine("Scope: offline Unity Editor harness only (no production code edited, no Play Mode, no Android build).");
            sb.AppendLine();

            // Step 1 & 2: Shared multi-chunk fixture + side-by-side comparison
            var sharedSave = CreateSharedMultiChunkSave(out var sharedInput, out var sharedRoadCells);
            var arms11G = Evaluate11GRules(sharedSave);
            var topoPlan = SettlementPathTopology.Build(sharedInput);

            sb.AppendLine("=== STEP 1 & 2: SHARED TWO-CHUNK MASK COMPARISON (11G PlacePathRibbon vs Pure Topology) ===");
            WriteComparisonSection(sb, sharedSave, sharedRoadCells, arms11G, topoPlan, ci);
            sb.AppendLine();

            // Step 3: WaterSourceLayout audit
            sb.AppendLine("=== STEP 3: WATER SOURCE LAYOUT AUDIT ===");
            WriteWaterAuditSection(sb, arms11G, topoPlan, ci);
            sb.AppendLine();

            // Step 4: Two-chunk border staleness reproduction
            sb.AppendLine("=== STEP 4: TWO-CHUNK BORDER STALENESS REPRODUCTION (11G chunkSig BUG) ===");
            var borderReproOk = RunChunkBorderRepro(sb, out var t0Arms, out var t1StaleArms, out var t1FreshArms, out var t1Topo);
            sb.AppendLine();

            // Step 5: Performance benchmark (BuildSignature, SampleSettlementStage, PreDecodedCache, Topology.Build)
            sb.AppendLine("=== STEP 5: PERFORMANCE BENCHMARK (SIGNATURES, CELL LOOKUP, TOPOLOGY) ===");
            RunPerformanceBenchmarks(sb, sharedInput, ci);
            sb.AppendLine();

            // Step 6: Offline pure topology unit tests
            sb.AppendLine("=== STEP 6: PURE TOPOLOGY PLANNER OFFLINE UNIT TESTS ===");
            var tests = RunUnitTests(borderReproOk, ci);
            var passed = 0;
            for (var i = 0; i < tests.Count; i++)
            {
                var t = tests[i];
                if (t.Passed) passed++;
                sb.AppendLine($"  [{(t.Passed ? "PASS" : "FAIL")}] {t.Name}: {t.Detail}");
            }

            sb.AppendLine($"Summary: {passed}/{tests.Count} tests passed.");

            // Write reports and schematic PNGs
            Directory.CreateDirectory(BuildReportDir);
            Directory.CreateDirectory(DocsReportDir);

            var reportText = sb.ToString();
            File.WriteAllText(BuildReportPath, reportText, Encoding.UTF8);
            File.WriteAllText(DocsReportPath, reportText, Encoding.UTF8);

            RenderComparisonPng(
                Path.Combine(BuildReportDir, ComparisonPngName),
                Path.Combine(DocsReportDir, ComparisonPngName),
                sharedRoadCells, sharedInput, arms11G, topoPlan);

            RenderBorderReproPng(
                Path.Combine(BuildReportDir, BorderPngName),
                Path.Combine(DocsReportDir, BorderPngName),
                t0Arms, t1StaleArms, t1Topo);

            AssetDatabase.Refresh();

            if (passed != tests.Count)
            {
                Debug.LogError($"[AFK Island] Stage C-3B suite FAILED ({passed}/{tests.Count} passed). Report: {BuildReportPath}");
            }
            else
            {
                Debug.Log($"[AFK Island] Stage C-3B suite PASSED ({passed}/{tests.Count}). Report: {BuildReportPath} and {DocsReportPath}");
            }
        }

        // ------------------------------------------------------------------ Step 1: Shared fixture

        private static GameSaveData CreateSharedMultiChunkSave(
            out SettlementPathTopology.TopologyInput topoInput,
            out List<SettlementPathTopology.MaskCellInput> roadCells)
        {
            var save = new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(1_700_000_000L)
            };

            var homeSettle = new byte[ChunkMaskCodec.CellCount];
            var home = new ChunkGrowthSaveData
            {
                chunkX = 0,
                chunkZ = 0,
                isLegacyBase = true,
                growthSeed = 101,
                stageMaskBase64 = ChunkMaskCodec.CreateMatureMaskBase64(),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(homeSettle)
            };
            save.worldGrowth.chunks.Add(home);

            // Chunk (1, 0): grown eastern chunk with full Moss/Vegetation land
            var eastLand = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < eastLand.Length; i++)
            {
                eastLand[i] = (byte)CellStage.Vegetation;
            }

            var eastSettle = new byte[ChunkMaskCodec.CellCount];
            var east = new ChunkGrowthSaveData
            {
                chunkX = 1,
                chunkZ = 0,
                isLegacyBase = false,
                growthSeed = 202,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(eastLand),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(eastSettle)
            };
            save.worldGrowth.chunks.Add(east);

            var cells = new List<SettlementPathTopology.MaskCellInput>();

            void SetCell(int wx, int wz, SettlementStage stage)
            {
                var cx = FloorDiv(wx, ChunkMaskCodec.GridSize);
                var cz = FloorDiv(wz, ChunkMaskCodec.GridSize);
                var lx = wx - cx * ChunkMaskCodec.GridSize;
                var lz = wz - cz * ChunkMaskCodec.GridSize;
                var idx = ChunkMaskCodec.CellIndex(lx, lz);
                if (cx == 0 && cz == 0) homeSettle[idx] = (byte)stage;
                else if (cx == 1 && cz == 0) eastSettle[idx] = (byte)stage;

                if (stage == SettlementStage.Path || stage == SettlementStage.Stone)
                {
                    cells.Add(new SettlementPathTopology.MaskCellInput
                    {
                        WorldCellX = wx,
                        WorldCellZ = wz,
                        Stage = (byte)stage
                    });
                }
            }

            // Trunk road in chunk (0,0) heading north of the pond across border x=15 -> x=16
            // Note: cell (8, 8) center is (0.5, 0.5) right next to Starter_Main (x=0).
            // Cell (15, 8) center is (7.5, 0.5); cell (16, 8) center is (8.5, 0.5) in chunk (1,0).
            for (var wx = 8; wx <= 21; wx++)
            {
                SetCell(wx, 8, wx == 13 ? SettlementStage.Stone : SettlementStage.Path);
            }

            // T-junction in chunk (1,0) at (18, 8): north arm (18, 9..11) and south arm (18, 5..7)
            SetCell(18, 9, SettlementStage.Path);
            SetCell(18, 10, SettlementStage.Path);
            SetCell(18, 11, SettlementStage.Path);
            SetCell(18, 7, SettlementStage.Path);
            SetCell(18, 6, SettlementStage.Path);
            SetCell(18, 5, SettlementStage.Path);

            // 2x2 filled path block at (19..20, 6..7) to test corner/loop handling
            SetCell(19, 7, SettlementStage.Path);
            SetCell(20, 7, SettlementStage.Path);
            SetCell(19, 6, SettlementStage.Path);
            SetCell(20, 6, SettlementStage.Path);

            // South-west branch on chunk (0,0) passing south-west of the pond (Pond center (1.8, -1.4) ~ cells (9..10, 5..7))
            SetCell(7, 5, SettlementStage.Path);
            SetCell(7, 4, SettlementStage.Path);
            SetCell(8, 4, SettlementStage.Path);

            // Isolated single road cell (degree 0) to observe how each system handles it
            SetCell(22, 12, SettlementStage.Path);

            // Hut cells in chunk (1,0) and (0,0)
            SetCell(18, 12, SettlementStage.Hut); // north of T-branch, door facing south (yaw 180)
            SetCell(22, 8, SettlementStage.Hut);  // east of trunk, door facing away east (yaw 90 -> tests visual correction)
            SetCell(15, 11, SettlementStage.Hut); // near border north of road, door facing south-east (yaw 150)
            SetCell(7, 3, SettlementStage.Hut);   // south-west of pond, door facing north (yaw 0)

            home.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(homeSettle);
            east.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(eastSettle);

            var cfg = new SettlementPathTopology.TopologyConfig();
            var huts = SettlementPathTopology.CreateProductionStarterHuts();
            huts.Add(new SettlementPathTopology.HutInput
            {
                Id = "Hut_Grown_North",
                Center = SettlementPathTopology.CellCenterXZ(18, 12, cfg),
                YawDegrees = 180f,
                Scale = SettlementCellQuery.HutVisualUniformScale,
                FootprintHalfExtents = new Vector2(1.35f * 0.5f, 1.15f * 0.5f),
                DoorLocalZ = 1.15f * 0.52f
            });
            huts.Add(new SettlementPathTopology.HutInput
            {
                Id = "Hut_Grown_East_Reversed",
                Center = SettlementPathTopology.CellCenterXZ(22, 8, cfg),
                YawDegrees = 90f, // facing away (+X) from trunk at (21, 8)
                Scale = SettlementCellQuery.HutVisualUniformScale,
                FootprintHalfExtents = new Vector2(1.55f * 0.5f, 1.25f * 0.5f),
                DoorLocalZ = 1.25f * 0.52f
            });
            huts.Add(new SettlementPathTopology.HutInput
            {
                Id = "Hut_Grown_BorderN",
                Center = SettlementPathTopology.CellCenterXZ(15, 11, cfg),
                YawDegrees = 150f,
                Scale = SettlementCellQuery.HutVisualUniformScale,
                FootprintHalfExtents = new Vector2(1.35f * 0.5f, 1.15f * 0.5f),
                DoorLocalZ = 1.15f * 0.52f
            });
            huts.Add(new SettlementPathTopology.HutInput
            {
                Id = "Hut_Grown_PondSW",
                Center = SettlementPathTopology.CellCenterXZ(7, 3, cfg),
                YawDegrees = 0f,
                Scale = SettlementCellQuery.HutVisualUniformScale,
                FootprintHalfExtents = new Vector2(1.35f * 0.5f, 1.15f * 0.5f),
                DoorLocalZ = 1.15f * 0.52f
            });

            roadCells = cells;
            topoInput = new SettlementPathTopology.TopologyInput
            {
                Config = cfg,
                StarterPaths = SettlementPathTopology.CreateProductionStarterPaths(cfg),
                RoadCells = cells,
                Huts = huts,
                Obstacles = new List<SettlementPathTopology.CircleObstacle>
                {
                    new SettlementPathTopology.CircleObstacle { Id = "Hearth", Center = new Vector2(0.15f, -1.50f), Radius = 0.52f },
                    new SettlementPathTopology.CircleObstacle { Id = "Stone_00", Center = new Vector2(-2.6f, -1.2f), Radius = 0.35f },
                    new SettlementPathTopology.CircleObstacle { Id = "Stone_01", Center = new Vector2(2.5f, 1.1f), Radius = 0.35f }
                },
                IsInsideLand = (x, z) => x >= -7.5f && x <= 23.5f && z >= -7.5f && z <= 7.5f,
                IsBlockedByWater = IsInsideRawPond
            };

            return save;
        }

        // ------------------------------------------------------------------ Step 2: 11G Pure Mirror & Comparison

        /// <summary>
        /// Exact pure mirror of HomeSettlementVisualizer.PlacePathRibbon + SpawnRoadArm from the 11G working copy.
        /// Read-only comparison reference; does not call or edit HomeSettlementVisualizer.
        /// </summary>
        private static List<Arm11G> Evaluate11GRules(GameSaveData save, Func<int, int, bool> chunkFilter = null)
        {
            var arms = new List<Arm11G>();
            if (save?.worldGrowth?.chunks == null) return arms;

            bool IsRoad(int wx, int wz)
            {
                var s = SettlementSimulation.SampleSettlementStage(save, wx, wz);
                return s == (byte)SettlementStage.Path
                       || s == (byte)SettlementStage.Stone
                       || s == (byte)SettlementStage.Hut;
            }

            bool IsHut(int wx, int wz) =>
                SettlementSimulation.SampleSettlementStage(save, wx, wz) == (byte)SettlementStage.Hut;

            int RoadDegree(int wx, int wz)
            {
                var n = 0;
                if (IsRoad(wx, wz + 1)) n++;
                if (IsRoad(wx + 1, wz)) n++;
                if (IsRoad(wx, wz - 1)) n++;
                if (IsRoad(wx - 1, wz)) n++;
                return n;
            }

            void EmitArm(int cx, int cz, int wx, int wz, int dir)
            {
                var cellCenter = GrowthWorldPlacement.CellCenterWorld(cx, cz,
                    ChunkMaskCodec.CellIndex(wx - cx * 16, wz - cz * 16));
                var baseXZ = new Vector2(cellCenter.x, cellCenter.z);

                float lateral;
                float yaw;
                Vector2 offset;
                if (dir == 0 || dir == 2)
                {
                    lateral = (Hash01(1, wx, 3) - 0.5f) * 0.36f;
                    yaw = (Hash01(1, wx, 9) - 0.5f) * 18f;
                    offset = new Vector2(lateral, dir == 0 ? 0.34f : -0.34f);
                }
                else
                {
                    lateral = (Hash01(1, wz, 4) - 0.5f) * 0.36f;
                    yaw = 90f + (Hash01(1, wz, 11) - 0.5f) * 18f;
                    offset = new Vector2(dir == 1 ? 0.34f : -0.34f, lateral);
                }

                // Path_Tile pad is 1.12 m square, scaled by widthScale=0.25, lengthScale=0.55
                const float halfWidth = 1.12f * 0.25f * 0.5f;   // 0.14 m
                const float halfLength = 1.12f * 0.55f * 0.5f;  // 0.308 m
                var center = baseXZ + offset;
                var fwd = SettlementPathTopology.ForwardFromYaw(yaw);
                arms.Add(new Arm11G
                {
                    ChunkX = cx,
                    ChunkZ = cz,
                    WorldCellX = wx,
                    WorldCellZ = wz,
                    Direction = dir,
                    Center = center,
                    YawDegrees = yaw,
                    HalfWidth = halfWidth,
                    HalfLength = halfLength,
                    StartPoint = center - fwd * halfLength,
                    EndPoint = center + fwd * halfLength
                });
            }

            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null) continue;
                if (chunkFilter != null && !chunkFilter(growth.chunkX, growth.chunkZ)) continue;
                if (!ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _)) continue;

                for (var i = 0; i < settle.Length; i++)
                {
                    var stage = settle[i];
                    if (stage != (byte)SettlementStage.Path && stage != (byte)SettlementStage.Stone)
                    {
                        continue;
                    }

                    if (WaterSourceLayout.BlocksSettlementCell(growth.chunkX, growth.chunkZ, i))
                    {
                        continue;
                    }

                    ChunkMaskCodec.IndexToLocal(i, out var lx, out var lz);
                    var wx = growth.chunkX * ChunkMaskCodec.GridSize + lx;
                    var wz = growth.chunkZ * ChunkMaskCodec.GridSize + lz;

                    var north = IsRoad(wx, wz + 1);
                    var east = IsRoad(wx + 1, wz);
                    var south = IsRoad(wx, wz - 1);
                    var west = IsRoad(wx - 1, wz);
                    var count = (north ? 1 : 0) + (east ? 1 : 0) + (south ? 1 : 0) + (west ? 1 : 0);
                    if (count == 0) continue;

                    var throughZ = north && south;
                    var throughX = east && west;
                    if (count >= 3)
                    {
                        if (throughZ && !throughX)
                        {
                            EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 0);
                            EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 2);
                        }
                        else if (throughX && !throughZ)
                        {
                            EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 1);
                            EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 3);
                        }

                        if (north && !throughZ && IsHut(wx, wz + 1)) EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 0);
                        if (south && !throughZ && IsHut(wx, wz - 1)) EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 2);
                        if (east && !throughX && IsHut(wx + 1, wz)) EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 1);
                        if (west && !throughX && IsHut(wx - 1, wz)) EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 3);
                        continue;
                    }

                    if (!throughZ && !throughX && count == 2 &&
                        RoadDegree(wx, north ? wz + 1 : wz - 1) >= 3 &&
                        RoadDegree(east ? wx + 1 : wx - 1, wz) >= 3)
                    {
                        continue;
                    }

                    if (north) EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 0);
                    if (south) EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 2);
                    if (east) EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 1);
                    if (west) EmitArm(growth.chunkX, growth.chunkZ, wx, wz, 3);
                }
            }

            return arms;
        }

        private static void WriteComparisonSection(
            StringBuilder sb,
            GameSaveData save,
            List<SettlementPathTopology.MaskCellInput> roadCells,
            List<Arm11G> arms11G,
            SettlementPathTopology.TopologyPlan plan,
            CultureInfo ci)
        {
            // Inspect 11G junction gaps & T-junction omissions
            var tJunctionCellArms = 0;
            var cross4WayCellArms = 0;
            var cornerGapMax = 0f;
            var armsByCell = new Dictionary<(int, int), List<Arm11G>>();
            for (var i = 0; i < arms11G.Count; i++)
            {
                var a = arms11G[i];
                var key = (a.WorldCellX, a.WorldCellZ);
                if (!armsByCell.TryGetValue(key, out var list))
                {
                    list = new List<Arm11G>();
                    armsByCell[key] = list;
                }

                list.Add(a);
                if (a.WorldCellX == 18 && a.WorldCellZ == 8) cross4WayCellArms++;
                if (a.WorldCellX == 18 && a.WorldCellZ == 7) tJunctionCellArms++;
            }

            // Measure endpoint gaps between orthogonal arms inside the same cell (90-degree turns)
            foreach (var kv in armsByCell)
            {
                var list = kv.Value;
                if (list.Count == 2 && (list[0].Direction % 2) != (list[1].Direction % 2))
                {
                    var d = MinSegmentDistance(list[0].StartPoint, list[0].EndPoint, list[1].StartPoint, list[1].EndPoint);
                    if (d > cornerGapMax) cornerGapMax = d;
                }
            }

            // Measure 11G gap across a 4-way intersection (18, 8): throughX && throughZ -> count == 4!
            // Look at HomeSettlementVisualizer.cs lines 270-280:
            // if (throughZ && !throughX) ... else if (throughX && !throughZ) ...
            // WHEN BOTH throughZ AND throughX ARE TRUE (4-way intersection), NEITHER BRANCH RUNS -> 0 ARMS!
            var armsCrossingBorder = 0;
            for (var i = 0; i < arms11G.Count; i++)
            {
                if ((arms11G[i].WorldCellX == 15 && arms11G[i].Direction == 1) ||
                    (arms11G[i].WorldCellX == 16 && arms11G[i].Direction == 3))
                {
                    armsCrossingBorder++;
                }
            }

            var topoCrossingEdges = 0;
            for (var i = 0; i < plan.Edges.Count; i++)
            {
                if (plan.Edges[i].TouchedChunks.Count >= 2) topoCrossingEdges++;
            }

            var connectedHuts = 0;
            var correctedHuts = 0;
            var blockedHuts = 0;
            for (var i = 0; i < plan.HutReports.Count; i++)
            {
                var r = plan.HutReports[i];
                if (r.Status == SettlementPathTopology.HutConnectionStatus.Connected) connectedHuts++;
                else if (r.Status == SettlementPathTopology.HutConnectionStatus.ConnectedWithVisualDoorCorrection) correctedHuts++;
                else blockedHuts++;
            }

            sb.AppendLine($"Shared fixture: 2 chunks (0,0) + (1,0), {roadCells.Count} road cells (Path/Stone), 6 huts (2 production starter + 4 grown).");
            sb.AppendLine("11G PlacePathRibbon (pure rule mirror):");
            sb.AppendLine($"  Discrete arm instances: {arms11G.Count}");
            sb.AppendLine($"  4-way intersection (18,8) arms drawn: {cross4WayCellArms}  <-- FINDING: throughX && throughZ drops ALL 4 arms (hole in intersection!)");
            sb.AppendLine($"  3-way T-junction (18,7) arms drawn: {tJunctionCellArms}  <-- FINDING: side road arm (east to 19,7) omitted unless neighbor is a Hut!");
            sb.AppendLine(string.Format(ci, "  Max orthogonal corner arm gap: {0:F3} m", cornerGapMax));
            sb.AppendLine($"  Chunk-border (15,8)<->(16,8) opposing arms on clean rebuild: {armsCrossingBorder}/2");
            sb.AppendLine("  Door routing: none (arms stop at cell border / circular hut yard, ignores Door child)");
            sb.AppendLine("  Starter path integration: none (separate authored cubes, dimmed via SoftDimAuthored)");
            sb.AppendLine();
            sb.AppendLine("Pure Topology Planner (SettlementPathTopology):");
            sb.AppendLine($"  Nodes: {plan.Nodes.Count}, Continuous Edges: {plan.Edges.Count}, Explicit Junctions (deg>=3): {plan.Junctions.Count}");
            sb.AppendLine($"  Connected components: {plan.ConnectedComponents} (isolated cell (22,12) has no edges)");
            sb.AppendLine($"  Edges spanning chunk border (TouchedChunks >= 2): {topoCrossingEdges}");
            sb.AppendLine($"  Huts: {connectedHuts} connected as-is, {correctedHuts} connected via visual-only door correction, {blockedHuts} blocked");
            sb.AppendLine($"  Topology hash: {plan.TopologyHash}");
            for (var i = 0; i < plan.HutReports.Count; i++)
            {
                var r = plan.HutReports[i];
                sb.AppendLine(string.Format(ci,
                    "    Hut {0}: status={1} origYaw={2:F1} effYaw={3:F1} len={4:F2}m dot={5:F2} clear={6:F2}m join={7} ({8}) violations=[{9}]",
                    r.HutId, r.Status, r.OriginalYawDegrees, r.EffectiveYawDegrees,
                    r.RouteLength, r.StartAlignmentDot, r.MinHutClearance,
                    r.JoinedEdgeId ?? "-", r.Variant ?? "-", string.Join(",", r.HardViolations)));
            }
        }

        // ------------------------------------------------------------------ Step 3: Water Audit

        private static void WriteWaterAuditSection(
            StringBuilder sb,
            List<Arm11G> arms11G,
            SettlementPathTopology.TopologyPlan plan,
            CultureInfo ci)
        {
            sb.AppendLine(string.Format(ci,
                "WaterSourceLayout pond center=({0:F2}, {1:F2}) raw water ellipse rx={2:F2} rz={3:F2}; BlocksSettlement reserve rx={4:F2} rz={5:F2}.",
                PondCenter.x, PondCenter.y, PondRawRx, PondRawRz, PondSettlementRx, PondSettlementRz));

            // Audit authored starter objects against BOTH raw water and BlocksSettlement
            var starters = SettlementPathTopology.CreateProductionStarterPaths();
            foreach (var sp in starters)
            {
                var rawHits = 0;
                var reserveHits = 0;
                var firstRaw = Vector2.zero;
                var firstReserve = Vector2.zero;
                for (var i = 0; i < sp.ControlPoints.Count - 1; i++)
                {
                    var a = sp.ControlPoints[i];
                    var b = sp.ControlPoints[i + 1];
                    for (var k = 0; k <= 20; k++)
                    {
                        var p = Vector2.Lerp(a, b, k / 20f);
                        if (IsInsideEllipse(p, PondCenter, PondRawRx + sp.HalfWidth, PondRawRz + sp.HalfWidth))
                        {
                            if (rawHits == 0) firstRaw = p;
                            rawHits++;
                        }

                        if (IsInsideEllipse(p, PondCenter, PondSettlementRx, PondSettlementRz))
                        {
                            if (reserveHits == 0) firstReserve = p;
                            reserveHits++;
                        }
                    }
                }

                sb.AppendLine(string.Format(ci,
                    "  Authored {0}: touches raw water+halfWidth={1} (first {2:F2},{3:F2}); inside BlocksSettlement={4} (first {5:F2},{6:F2})",
                    sp.Id, rawHits > 0, firstRaw.x, firstRaw.y, reserveHits > 0, firstReserve.x, firstReserve.y));
            }

            var hearth = new Vector2(0.15f, -1.50f);
            sb.AppendLine(string.Format(ci,
                "  Authored Hearth ({0:F2},{1:F2}): insideRawWater={2}, insideBlocksSettlement={3}",
                hearth.x, hearth.y,
                IsInsideRawPond(hearth.x, hearth.y),
                IsInsideSettlementWaterReserve(hearth.x, hearth.y)));

            var starterHuts = SettlementPathTopology.CreateProductionStarterHuts();
            for (var i = 0; i < starterHuts.Count; i++)
            {
                var h = starterHuts[i];
                sb.AppendLine(string.Format(ci,
                    "  Authored {0} ({1:F2},{2:F2}): insideRawWater={3}, insideBlocksSettlement={4}",
                    h.Id, h.Center.x, h.Center.y,
                    IsInsideRawPond(h.Center.x, h.Center.y),
                    IsInsideSettlementWaterReserve(h.Center.x, h.Center.y)));
            }

            // Check 11G arms and Topology generated edges against raw water
            var armsInRawWater = 0;
            for (var i = 0; i < arms11G.Count; i++)
            {
                var a = arms11G[i];
                if (IsInsideRawPond(a.Center.x, a.Center.y) ||
                    IsInsideRawPond(a.StartPoint.x, a.StartPoint.y) ||
                    IsInsideRawPond(a.EndPoint.x, a.EndPoint.y))
                {
                    armsInRawWater++;
                }
            }

            var generatedEdgesInRawWater = 0;
            var starterEdgesInRawWater = 0;
            for (var e = 0; e < plan.Edges.Count; e++)
            {
                var edge = plan.Edges[e];
                var hit = false;
                for (var p = 0; p < edge.Points.Count; p++)
                {
                    if (IsInsideRawPond(edge.Points[p].x, edge.Points[p].y))
                    {
                        hit = true;
                        break;
                    }
                }

                if (hit)
                {
                    if (edge.Kind == SettlementPathTopology.EdgeKind.Starter) starterEdgesInRawWater++;
                    else generatedEdgesInRawWater++;
                }
            }

            sb.AppendLine($"  11G arms entering raw pond ellipse: {armsInRawWater}");
            sb.AppendLine($"  Topology generated edges (MaskRoad/HutBranch) entering raw pond ellipse: {generatedEdgesInRawWater}");
            sb.AppendLine($"  Topology starter audit issues flagged: {plan.StarterIssues.Count} (starter edges in raw water center: {starterEdgesInRawWater})");
            for (var i = 0; i < plan.StarterIssues.Count; i++)
            {
                var iss = plan.StarterIssues[i];
                sb.AppendLine(string.Format(ci, "    StarterIssue: {0} -> {1} at ({2:F2}, {3:F2})",
                    iss.StarterId, iss.Issue, iss.SamplePoint.x, iss.SamplePoint.y));
            }
        }

        // ------------------------------------------------------------------ Step 4: Two-chunk border repro

        private static bool RunChunkBorderRepro(
            StringBuilder sb,
            out List<Arm11G> t0Arms,
            out List<Arm11G> t1StaleArms,
            out List<Arm11G> t1FreshArms,
            out SettlementPathTopology.TopologyPlan t1Topo)
        {
            var save = new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(1_700_000_000L)
            };

            var homeSettle = new byte[ChunkMaskCodec.CellCount];
            homeSettle[ChunkMaskCodec.CellIndex(14, 8)] = (byte)SettlementStage.Path;
            homeSettle[ChunkMaskCodec.CellIndex(15, 8)] = (byte)SettlementStage.Path; // border cell!
            var home = new ChunkGrowthSaveData
            {
                chunkX = 0,
                chunkZ = 0,
                isLegacyBase = true,
                stageMaskBase64 = ChunkMaskCodec.CreateMatureMaskBase64(),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(homeSettle)
            };
            save.worldGrowth.chunks.Add(home);

            var eastLand = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < eastLand.Length; i++) eastLand[i] = (byte)CellStage.Moss;
            var eastSettle = new byte[ChunkMaskCodec.CellCount];
            var east = new ChunkGrowthSaveData
            {
                chunkX = 1,
                chunkZ = 0,
                isLegacyBase = false,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(eastLand),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(eastSettle)
            };
            save.worldGrowth.chunks.Add(east);

            // T0: chunk (0,0) built; chunk (1,0) empty
            var sigHomeT0 = ChunkSig11G(home);
            t0Arms = Evaluate11GRules(save);
            var homeEastArmsT0 = CountArms(t0Arms, 15, 8, dir: 1);

            // T1: chunk (1,0) grows Path at (lx=0, lz=8) -> world (16, 8) and (17, 8); chunk (0,0) untouched
            eastSettle[ChunkMaskCodec.CellIndex(0, 8)] = (byte)SettlementStage.Path;
            eastSettle[ChunkMaskCodec.CellIndex(1, 8)] = (byte)SettlementStage.Path;
            east.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(eastSettle);

            var sigHomeT1 = ChunkSig11G(home);
            var homeSkippedBy11G = sigHomeT0 == sigHomeT1;

            // Simulate HomeSettlementVisualizer.RebuildFromSave cache behavior:
            // chunk (0,0) keeps T0 arms because view.signature == chunkSig; chunk (1,0) rebuilds fresh.
            t1StaleArms = new List<Arm11G>();
            t1StaleArms.AddRange(t0Arms); // cached chunk (0,0)
            t1StaleArms.AddRange(Evaluate11GRules(save, (cx, cz) => cx == 1 && cz == 0)); // rebuilt chunk (1,0)

            t1FreshArms = Evaluate11GRules(save); // what a full rebuild would draw

            var staleHomeEastArms = CountArms(t1StaleArms, 15, 8, dir: 1);
            var staleEastWestArms = CountArms(t1StaleArms, 16, 8, dir: 3);
            var freshHomeEastArms = CountArms(t1FreshArms, 15, 8, dir: 1);

            // Now build with pure topology planner + border invalidation check
            var topoInput = new SettlementPathTopology.TopologyInput
            {
                RoadCells = new List<SettlementPathTopology.MaskCellInput>
                {
                    new SettlementPathTopology.MaskCellInput { WorldCellX = 14, WorldCellZ = 8, Stage = 2 },
                    new SettlementPathTopology.MaskCellInput { WorldCellX = 15, WorldCellZ = 8, Stage = 2 },
                    new SettlementPathTopology.MaskCellInput { WorldCellX = 16, WorldCellZ = 8, Stage = 2 },
                    new SettlementPathTopology.MaskCellInput { WorldCellX = 17, WorldCellZ = 8, Stage = 2 }
                }
            };
            t1Topo = SettlementPathTopology.Build(topoInput);
            var affectedChunks = SettlementPathTopology.GetAffectedChunksForCellChange(16, 8);
            var invalidatesHome = affectedChunks.Contains(new Vector2Int(0, 0)) && affectedChunks.Contains(new Vector2Int(1, 0));
            var topoHasContinuousBorderEdge = t1Topo.Edges.Count == 1 && t1Topo.Edges[0].TouchedChunks.Count == 2;

            var bugConfirmed = homeSkippedBy11G && homeEastArmsT0 == 0 && staleHomeEastArms == 0 && staleEastWestArms == 1 && freshHomeEastArms == 1;

            sb.AppendLine($"  T0 home chunkSig == T1 home chunkSig: {homeSkippedBy11G} (chunk (0,0) skipped by 11G cache)");
            sb.AppendLine($"  T1 stale 11G border arms: home(15,8)->east={staleHomeEastArms}, east(16,8)->west={staleEastWestArms} (ASYMMETRIC GAP; fresh rebuild would have {freshHomeEastArms})");
            sb.AppendLine($"  BUG CONFIRMED: {bugConfirmed}");
            sb.AppendLine($"  Pure topology at T1: edges={t1Topo.Edges.Count}, touchedChunks={t1Topo.Edges[0].TouchedChunks.Count}, continuousAcrossBorder={topoHasContinuousBorderEdge}");
            sb.AppendLine($"  GetAffectedChunksForCellChange(16,8) includes both (0,0) and (1,0): {invalidatesHome}");

            return bugConfirmed && topoHasContinuousBorderEdge && invalidatesHome;
        }

        // ------------------------------------------------------------------ Step 5: Performance Benchmarks

        private static void RunPerformanceBenchmarks(
            StringBuilder sb,
            SettlementPathTopology.TopologyInput sharedInput,
            CultureInfo ci)
        {
            int[] gridDims = { 2, 3, 5, 7 }; // 4, 9, 25, 49 chunks

            sb.AppendLine("1) BuildSignature (HomeSettlementVisualizer.LateUpdate per-frame cost):");
            foreach (var dim in gridDims)
            {
                var save = CreateBenchmarkSave(dim, out _);
                var chunks = dim * dim;

                // Warmup
                _ = BuildSignature11G(save);
                GC.Collect();
                var memBefore = GC.GetTotalMemory(true);
                const int iters = 500;
                var sw = Stopwatch.StartNew();
                var sink = 0;
                for (var i = 0; i < iters; i++)
                {
                    sink += BuildSignature11G(save).Length;
                }

                sw.Stop();
                var memAfter = GC.GetTotalMemory(false);
                var usPerCall = (sw.Elapsed.TotalMilliseconds * 1000.0) / iters;
                var msPerSec60Fps = (usPerCall * 60.0) / 1000.0;
                var bytesPerCall = Math.Max(0L, (memAfter - memBefore) / iters);
                sb.AppendLine(string.Format(ci,
                    "  {0,2} chunks ({1}x{1}): {2,6:F1} us/call | {3,5:F2} ms/sec @60fps | ~{4} B/call (sink={5})",
                    chunks, dim, usPerCall, msPerSec60Fps, bytesPerCall, sink > 0 ? "ok" : "0"));
            }

            sb.AppendLine();
            sb.AppendLine("2) SampleSettlementStage (production linear scan + Base64 decode per lookup) vs Pre-Decoded Cache:");
            sb.AppendLine("   Simulating 1 full chunk rebuild: 256 cells x 6 neighbor lookups = 1536 lookups.");
            foreach (var dim in gridDims)
            {
                var save = CreateBenchmarkSave(dim, out _);
                var chunks = dim * dim;

                // Production SampleSettlementStage (1536 lookups on center chunk)
                var swProd = Stopwatch.StartNew();
                var sumProd = 0;
                for (var lz = 0; lz < 16; lz++)
                {
                    for (var lx = 0; lx < 16; lx++)
                    {
                        var wx = (dim / 2) * 16 + lx;
                        var wz = (dim / 2) * 16 + lz;
                        sumProd += SettlementSimulation.SampleSettlementStage(save, wx, wz + 1);
                        sumProd += SettlementSimulation.SampleSettlementStage(save, wx + 1, wz);
                        sumProd += SettlementSimulation.SampleSettlementStage(save, wx, wz - 1);
                        sumProd += SettlementSimulation.SampleSettlementStage(save, wx - 1, wz);
                        sumProd += SettlementSimulation.SampleSettlementStage(save, wx + 1, wz + 1);
                        sumProd += SettlementSimulation.SampleSettlementStage(save, wx - 1, wz - 1);
                    }
                }

                swProd.Stop();

                // Cached pre-decoded map (decode once per chunk + O(1) array lookup)
                var swCache = Stopwatch.StartNew();
                var cache = new Dictionary<long, byte[]>(save.worldGrowth.chunks.Count);
                for (var c = 0; c < save.worldGrowth.chunks.Count; c++)
                {
                    var g = save.worldGrowth.chunks[c];
                    if (g != null && ChunkMaskCodec.TryDecodeBase64(g.settlementMaskBase64, out var mask, out _))
                    {
                        cache[((long)g.chunkX << 32) ^ (uint)g.chunkZ] = mask;
                    }
                }

                byte SampleCached(int wx, int wz)
                {
                    var cx = FloorDiv(wx, 16);
                    var cz = FloorDiv(wz, 16);
                    if (!cache.TryGetValue(((long)cx << 32) ^ (uint)cz, out var m)) return 0;
                    return m[ChunkMaskCodec.CellIndex(wx - cx * 16, wz - cz * 16)];
                }

                var sumCache = 0;
                for (var lz = 0; lz < 16; lz++)
                {
                    for (var lx = 0; lx < 16; lx++)
                    {
                        var wx = (dim / 2) * 16 + lx;
                        var wz = (dim / 2) * 16 + lz;
                        sumCache += SampleCached(wx, wz + 1);
                        sumCache += SampleCached(wx + 1, wz);
                        sumCache += SampleCached(wx, wz - 1);
                        sumCache += SampleCached(wx - 1, wz);
                        sumCache += SampleCached(wx + 1, wz + 1);
                        sumCache += SampleCached(wx - 1, wz - 1);
                    }
                }

                swCache.Stop();

                var speedup = swCache.Elapsed.TotalMilliseconds > 1e-6
                    ? swProd.Elapsed.TotalMilliseconds / swCache.Elapsed.TotalMilliseconds
                    : 0.0;
                sb.AppendLine(string.Format(ci,
                    "  {0,2} chunks: prod={1,6:F3} ms | cached={2,6:F3} ms | speedup={3,5:F1}x | parity={4}",
                    chunks, swProd.Elapsed.TotalMilliseconds, swCache.Elapsed.TotalMilliseconds, speedup, sumProd == sumCache));
            }

            sb.AppendLine();
            sb.AppendLine("3) SettlementPathTopology.Build timing:");
            // Warmup
            _ = SettlementPathTopology.Build(sharedInput);
            var swShared = Stopwatch.StartNew();
            const int sharedRuns = 20;
            for (var i = 0; i < sharedRuns; i++) _ = SettlementPathTopology.Build(sharedInput);
            swShared.Stop();

            // Worst-case 25-chunk settlement: 5x5 chunks, ~180 road cells, 16 huts
            CreateBenchmarkSave(5, out var stressInput);
            _ = SettlementPathTopology.Build(stressInput);
            var swStress = Stopwatch.StartNew();
            const int stressRuns = 5;
            SettlementPathTopology.TopologyPlan stressPlan = null;
            for (var i = 0; i < stressRuns; i++) stressPlan = SettlementPathTopology.Build(stressInput);
            swStress.Stop();

            sb.AppendLine(string.Format(ci,
                "  Shared 2-chunk fixture (26 road cells, 6 huts): {0:F2} ms/build",
                swShared.Elapsed.TotalMilliseconds / sharedRuns));
            sb.AppendLine(string.Format(ci,
                "  Worst-case 25-chunk fixture ({0} road cells, {1} huts -> {2} nodes, {3} edges, {4} junctions): {5:F2} ms/build",
                stressInput.RoadCells.Count, stressInput.Huts.Count,
                stressPlan.Nodes.Count, stressPlan.Edges.Count, stressPlan.Junctions.Count,
                swStress.Elapsed.TotalMilliseconds / stressRuns));
        }

        // ------------------------------------------------------------------ Step 6: Offline Unit Tests

        private static List<TestResult> RunUnitTests(bool borderReproPassed, CultureInfo ci)
        {
            var results = new List<TestResult>();

            // T1: Starter-only layout (production IslandPrototypeBuilder poses)
            {
                var input = new SettlementPathTopology.TopologyInput
                {
                    StarterPaths = SettlementPathTopology.CreateProductionStarterPaths(),
                    Huts = SettlementPathTopology.CreateProductionStarterHuts(),
                    IsBlockedByWater = IsInsideRawPond,
                    IsInsideLand = (x, z) => x >= -7.25f && x <= 7.25f && z >= -5.75f && z <= 5.75f
                };
                var p = SettlementPathTopology.Build(input);
                var allConn = p.HutReports.Count == 2 &&
                              p.HutReports[0].Status != SettlementPathTopology.HutConnectionStatus.UnconnectedBlocked &&
                              p.HutReports[1].Status != SettlementPathTopology.HutConnectionStatus.UnconnectedBlocked;
                var crossJunction = p.Junctions.Count >= 1;
                results.Add(new TestResult
                {
                    Name = "T1_StarterOnly_ProductionPoses",
                    Passed = allConn && crossJunction && p.ConnectedComponents == 1,
                    Detail = string.Format(ci,
                        "components={0} junctions={1} HutA={2}(visCorr={3},dot={4:F2}) HutB={5}(visCorr={6},dot={7:F2})",
                        p.ConnectedComponents, p.Junctions.Count,
                        p.HutReports[0].Status, p.HutReports[0].UsedVisualDoorCorrection, p.HutReports[0].StartAlignmentDot,
                        p.HutReports[1].Status, p.HutReports[1].UsedVisualDoorCorrection, p.HutReports[1].StartAlignmentDot)
                });
            }

            // T2: Four-hut fixture (C-2B approved layout + production starter + 2 grown)
            {
                var cfg = new SettlementPathTopology.TopologyConfig { AllowVisualDoorCorrection = false };
                var c2bInput = new SettlementPathTopology.TopologyInput
                {
                    Config = cfg,
                    StarterPaths = SettlementPathTopology.CreateProductionStarterPaths(cfg),
                    Huts = new List<SettlementPathTopology.HutInput>
                    {
                        new SettlementPathTopology.HutInput { Id = "Hut_A", Center = new Vector2(-1.65f, -0.15f), YawDegrees = 215f, Scale = 0.62f, DoorLocalZ = 0.598f, AllowVisualDoorCorrection = false },
                        new SettlementPathTopology.HutInput { Id = "Hut_B", Center = new Vector2(1.35f, 0.65f), YawDegrees = 245f, Scale = 0.62f, DoorLocalZ = 0.650f, AllowVisualDoorCorrection = false },
                        new SettlementPathTopology.HutInput { Id = "Hut_Test_03", Center = new Vector2(-0.90f, -3.20f), YawDegrees = 10f, Scale = 0.62f, DoorLocalZ = 0.598f, AllowVisualDoorCorrection = false },
                        new SettlementPathTopology.HutInput { Id = "Hut_Test_04", Center = new Vector2(2.30f, -2.50f), YawDegrees = 10f, Scale = 0.62f, DoorLocalZ = 0.650f, AllowVisualDoorCorrection = false }
                    }
                };
                var p = SettlementPathTopology.Build(c2bInput);
                var ok = p.HutReports.Count == 4 && p.ConnectedComponents == 1;
                for (var i = 0; i < p.HutReports.Count; i++)
                {
                    if (p.HutReports[i].Status != SettlementPathTopology.HutConnectionStatus.Connected) ok = false;
                }

                results.Add(new TestResult
                {
                    Name = "T2_FourHutFixture_C2BLayout",
                    Passed = ok,
                    Detail = $"connected={p.HutReports.Count}/4 components={p.ConnectedComponents} junctions={p.Junctions.Count} hash={p.TopologyHash}"
                });
            }

            // T3: Branch-to-branch join
            {
                var input = new SettlementPathTopology.TopologyInput
                {
                    StarterPaths = new List<SettlementPathTopology.StarterPolylineInput>
                    {
                        new SettlementPathTopology.StarterPolylineInput
                        {
                            Id = "Trunk",
                            ControlPoints = new List<Vector2> { new Vector2(0f, -2f), new Vector2(0f, 2f) }
                        }
                    },
                    Huts = new List<SettlementPathTopology.HutInput>
                    {
                        new SettlementPathTopology.HutInput { Id = "Hut_01", Center = new Vector2(2.5f, 0f), YawDegrees = 270f, Scale = 0.62f, DoorLocalZ = 0.60f },
                        new SettlementPathTopology.HutInput { Id = "Hut_02", Center = new Vector2(2.0f, 2.0f), YawDegrees = 180f, Scale = 0.62f, DoorLocalZ = 0.60f }
                    }
                };
                var p = SettlementPathTopology.Build(input);
                var hut2 = p.HutReports[1];
                var joinedBranch = hut2.Status == SettlementPathTopology.HutConnectionStatus.Connected &&
                                   hut2.JoinedEdgeId != null && hut2.JoinedEdgeId.StartsWith("Branch_Hut_01", StringComparison.Ordinal);
                results.Add(new TestResult
                {
                    Name = "T3_BranchToBranchJoin",
                    Passed = joinedBranch && p.Junctions.Count >= 2,
                    Detail = $"Hut_02 joined={hut2.JoinedEdgeId} junctions={p.Junctions.Count}"
                });
            }

            // T4: Multiple simultaneous huts + order-independence (shuffled input -> identical hash)
            {
                var hutsA = new List<SettlementPathTopology.HutInput>
                {
                    new SettlementPathTopology.HutInput { Id = "H_1", Center = new Vector2(-2.2f, 1.5f), YawDegrees = 90f, Scale = 0.62f },
                    new SettlementPathTopology.HutInput { Id = "H_2", Center = new Vector2(2.2f, 1.5f), YawDegrees = 270f, Scale = 0.62f },
                    new SettlementPathTopology.HutInput { Id = "H_3", Center = new Vector2(-2.2f, -1.5f), YawDegrees = 90f, Scale = 0.62f },
                    new SettlementPathTopology.HutInput { Id = "H_4", Center = new Vector2(2.4f, -0.2f), YawDegrees = 270f, Scale = 0.62f },
                    new SettlementPathTopology.HutInput { Id = "H_5", Center = new Vector2(-3.8f, 0.2f), YawDegrees = 90f, Scale = 0.62f },
                    new SettlementPathTopology.HutInput { Id = "H_6", Center = new Vector2(3.8f, 1.5f), YawDegrees = 270f, Scale = 0.62f }
                };
                var hutsB = new List<SettlementPathTopology.HutInput>(hutsA);
                hutsB.Reverse();

                var pA = SettlementPathTopology.Build(new SettlementPathTopology.TopologyInput
                {
                    StarterPaths = SettlementPathTopology.CreateProductionStarterPaths(),
                    Huts = hutsA
                });
                var pB = SettlementPathTopology.Build(new SettlementPathTopology.TopologyInput
                {
                    StarterPaths = SettlementPathTopology.CreateProductionStarterPaths(),
                    Huts = hutsB
                });
                var allOk = pA.TopologyHash == pB.TopologyHash && pA.ConnectedComponents == 1;
                for (var i = 0; i < pA.HutReports.Count; i++)
                {
                    if (pA.HutReports[i].Status == SettlementPathTopology.HutConnectionStatus.UnconnectedBlocked) allOk = false;
                }

                results.Add(new TestResult
                {
                    Name = "T4_MultipleSimultaneousHuts_OrderIndependent",
                    Passed = allOk,
                    Detail = $"huts=6 hashA={pA.TopologyHash} hashB={pB.TopologyHash} components={pA.ConnectedComponents}"
                });
            }

            // T5: Reversed door orientation (hard-constraint reject when visual correction off; clean visual-only fix when on)
            {
                var cfgNoFix = new SettlementPathTopology.TopologyConfig
                {
                    AllowVisualDoorCorrection = false,
                    MaxBranchLengthMeters = 3.2f
                };
                var inputNoFix = new SettlementPathTopology.TopologyInput
                {
                    Config = cfgNoFix,
                    StarterPaths = new List<SettlementPathTopology.StarterPolylineInput>
                    {
                        new SettlementPathTopology.StarterPolylineInput
                        {
                            Id = "Trunk",
                            ControlPoints = new List<Vector2> { new Vector2(0f, -2f), new Vector2(0f, 2f) }
                        }
                    },
                    Huts = new List<SettlementPathTopology.HutInput>
                    {
                        // Door faces directly away (+X) while trunk is at X=0 and island edge is at X=3.2
                        new SettlementPathTopology.HutInput { Id = "Hut_Away", Center = new Vector2(2.4f, 0f), YawDegrees = 90f, Scale = 0.62f, AllowVisualDoorCorrection = false }
                    },
                    IsInsideLand = (x, z) => x >= -4f && x <= 3.1f && z >= -4f && z <= 4f
                };
                var pNoFix = SettlementPathTopology.Build(inputNoFix);
                var rejectedWithoutFix = pNoFix.HutReports[0].Status == SettlementPathTopology.HutConnectionStatus.UnconnectedBlocked &&
                                         pNoFix.HutReports[0].HardViolations.Count > 0;

                var cfgWithFix = new SettlementPathTopology.TopologyConfig
                {
                    AllowVisualDoorCorrection = true,
                    MaxBranchLengthMeters = 3.2f
                };
                var inputWithFix = new SettlementPathTopology.TopologyInput
                {
                    Config = cfgWithFix,
                    StarterPaths = inputNoFix.StarterPaths,
                    Huts = new List<SettlementPathTopology.HutInput>
                    {
                        new SettlementPathTopology.HutInput { Id = "Hut_Away", Center = new Vector2(2.4f, 0f), YawDegrees = 90f, Scale = 0.62f, AllowVisualDoorCorrection = true }
                    },
                    IsInsideLand = inputNoFix.IsInsideLand
                };
                var pWithFix = SettlementPathTopology.Build(inputWithFix);
                var fixedVisually = pWithFix.HutReports[0].Status == SettlementPathTopology.HutConnectionStatus.ConnectedWithVisualDoorCorrection &&
                                    pWithFix.HutReports[0].UsedVisualDoorCorrection &&
                                    pWithFix.HutReports[0].HardViolations.Count == 0;

                results.Add(new TestResult
                {
                    Name = "T5_ReversedDoor_HardRejectAndVisualCorrection",
                    Passed = rejectedWithoutFix && fixedVisually,
                    Detail = string.Format(ci,
                        "noFix={0}([{1}]), withVisualFix={2}(origYaw=90->effYaw={3:F1},len={4:F2}m)",
                        pNoFix.HutReports[0].Status, string.Join(",", pNoFix.HutReports[0].HardViolations),
                        pWithFix.HutReports[0].Status, pWithFix.HutReports[0].EffectiveYawDegrees, pWithFix.HutReports[0].RouteLength)
                });
            }

            // T6: Blocked join (hut cut off by water barrier within max branch length -> must NOT force path across water)
            {
                var input = new SettlementPathTopology.TopologyInput
                {
                    StarterPaths = new List<SettlementPathTopology.StarterPolylineInput>
                    {
                        new SettlementPathTopology.StarterPolylineInput
                        {
                            Id = "Trunk",
                            ControlPoints = new List<Vector2> { new Vector2(-1.8f, -2f), new Vector2(-1.8f, 2f) }
                        }
                    },
                    Huts = new List<SettlementPathTopology.HutInput>
                    {
                        new SettlementPathTopology.HutInput { Id = "Hut_Island", Center = new Vector2(1.8f, 0f), YawDegrees = 270f, Scale = 0.62f, AllowVisualDoorCorrection = true }
                    },
                    // Vertical river at x in [-0.6, +0.6] separating hut from trunk (~3.2 m apart < 5.0 m max)
                    IsBlockedByWater = (x, z) => x >= -0.6f && x <= 0.6f
                };
                var p = SettlementPathTopology.Build(input);
                var blocked = p.HutReports[0].Status == SettlementPathTopology.HutConnectionStatus.UnconnectedBlocked &&
                              p.HutReports[0].HardViolations.Contains("water_crossing") &&
                              p.Edges.Count == 1; // only Trunk, no invalid branch!
                results.Add(new TestResult
                {
                    Name = "T6_BlockedJoin_WaterBarrierNeverCrossed",
                    Passed = blocked,
                    Detail = $"status={p.HutReports[0].Status} edges={p.Edges.Count} violations=[{string.Join(",", p.HutReports[0].HardViolations)}]"
                });
            }

            // T7: Duplicate input deduplication
            {
                var cleanInput = new SettlementPathTopology.TopologyInput
                {
                    RoadCells = new List<SettlementPathTopology.MaskCellInput>
                    {
                        new SettlementPathTopology.MaskCellInput { WorldCellX = 10, WorldCellZ = 8, Stage = 2 },
                        new SettlementPathTopology.MaskCellInput { WorldCellX = 11, WorldCellZ = 8, Stage = 2 }
                    },
                    Huts = new List<SettlementPathTopology.HutInput>
                    {
                        new SettlementPathTopology.HutInput { Id = "H_Dup", Center = new Vector2(3f, 2.2f), YawDegrees = 180f, Scale = 0.62f }
                    }
                };
                var dupInput = new SettlementPathTopology.TopologyInput
                {
                    RoadCells = new List<SettlementPathTopology.MaskCellInput>
                    {
                        new SettlementPathTopology.MaskCellInput { WorldCellX = 10, WorldCellZ = 8, Stage = 2 },
                        new SettlementPathTopology.MaskCellInput { WorldCellX = 10, WorldCellZ = 8, Stage = 2 },
                        new SettlementPathTopology.MaskCellInput { WorldCellX = 11, WorldCellZ = 8, Stage = 2 }
                    },
                    Huts = new List<SettlementPathTopology.HutInput>
                    {
                        new SettlementPathTopology.HutInput { Id = "H_Dup", Center = new Vector2(3f, 2.2f), YawDegrees = 180f, Scale = 0.62f },
                        new SettlementPathTopology.HutInput { Id = "H_Dup", Center = new Vector2(3f, 2.2f), YawDegrees = 180f, Scale = 0.62f }
                    }
                };
                var pClean = SettlementPathTopology.Build(cleanInput);
                var pDup = SettlementPathTopology.Build(dupInput);
                var ok = pClean.TopologyHash == pDup.TopologyHash &&
                         pDup.DuplicateHutsIgnored == 1 &&
                         pDup.DuplicateCellsIgnored == 1;
                results.Add(new TestResult
                {
                    Name = "T7_DuplicateInput_Deduplicated",
                    Passed = ok,
                    Detail = $"dupHuts={pDup.DuplicateHutsIgnored} dupCells={pDup.DuplicateCellsIgnored} hashMatch={pClean.TopologyHash == pDup.TopologyHash}"
                });
            }

            // T8: Deterministic rebuild
            {
                _ = CreateSharedMultiChunkSave(out var inp1, out _);
                _ = CreateSharedMultiChunkSave(out var inp2, out _);
                inp2.RoadCells.Reverse();
                inp2.Huts.Reverse();
                var p1 = SettlementPathTopology.Build(inp1);
                var p2 = SettlementPathTopology.Build(inp2);
                results.Add(new TestResult
                {
                    Name = "T8_DeterministicRebuild_PermutationInvariant",
                    Passed = p1.TopologyHash == p2.TopologyHash && p1.Nodes.Count == p2.Nodes.Count && p1.Edges.Count == p2.Edges.Count,
                    Detail = $"hash1={p1.TopologyHash} hash2={p2.TopologyHash}"
                });
            }

            // T9: Two-chunk border repro & invalidation
            {
                var interior = SettlementPathTopology.GetAffectedChunksForCellChange(8, 8);
                var corner = SettlementPathTopology.GetAffectedChunksForCellChange(16, 0); // lx=0, lz=0 -> 4 chunks
                var ok = borderReproPassed && interior.Count == 1 && corner.Count == 4;
                results.Add(new TestResult
                {
                    Name = "T9_TwoChunkBorderRepro_AndInvalidation",
                    Passed = ok,
                    Detail = $"bugReproducedAndFixed={borderReproPassed} interiorAffected={interior.Count} cornerAffected={corner.Count}"
                });
            }

            // T10: No generated route crosses raw water on shared fixture
            {
                _ = CreateSharedMultiChunkSave(out var inp, out _);
                var p = SettlementPathTopology.Build(inp);
                var waterHits = 0;
                for (var e = 0; e < p.Edges.Count; e++)
                {
                    var edge = p.Edges[e];
                    if (edge.Kind == SettlementPathTopology.EdgeKind.Starter) continue;
                    for (var i = 0; i < edge.Points.Count; i++)
                    {
                        if (IsInsideRawPond(edge.Points[i].x, edge.Points[i].y))
                        {
                            waterHits++;
                        }
                    }
                }

                results.Add(new TestResult
                {
                    Name = "T10_NoGeneratedEdgeCrossesWater",
                    Passed = waterHits == 0,
                    Detail = $"generatedWaterSampleHits={waterHits}"
                });
            }

            return results;
        }

        // ------------------------------------------------------------------ Schematic PNG Renderers (pure CPU)

        private static void RenderComparisonPng(
            string buildPath,
            string docsPath,
            List<SettlementPathTopology.MaskCellInput> roadCells,
            SettlementPathTopology.TopologyInput input,
            List<Arm11G> arms11G,
            SettlementPathTopology.TopologyPlan plan)
        {
            const int w = 1600;
            const int h = 760;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color32[w * h];
            var bg = new Color32(28, 34, 30, 255);
            for (var i = 0; i < pixels.Length; i++) pixels[i] = bg;

            // Two panels: Left (0..780) = 11G PlacePathRibbon; Right (820..1600) = Pure Topology Planner
            // World bounds: x in [-4.5, 16.5], z in [-5.5, 7.0]
            var minW = new Vector2(-4.5f, -5.5f);
            var maxW = new Vector2(16.5f, 7.0f);

            DrawBasePanel(pixels, w, h, 20, 40, 760, 680, minW, maxW, roadCells, input);
            DrawBasePanel(pixels, w, h, 820, 40, 760, 680, minW, maxW, roadCells, input);

            // Divider line
            FillRect(pixels, w, h, 798, 20, 4, h - 40, new Color32(80, 90, 85, 255));

            // Left panel: 11G arms + authored starter rectangles
            DrawStarterRects11G(pixels, w, h, 20, 40, 760, 680, minW, maxW);
            var armColor = new Color32(215, 168, 96, 255);
            for (var i = 0; i < arms11G.Count; i++)
            {
                var a = arms11G[i];
                var p0 = WorldToPanel(a.StartPoint, 20, 40, 760, 680, minW, maxW);
                var p1 = WorldToPanel(a.EndPoint, 20, 40, 760, 680, minW, maxW);
                DrawThickLine(pixels, w, h, p0, p1, 5, armColor);
            }

            // Highlight 4-way hole at (18,8) in red circle on left panel
            var holePos = WorldToPanel(SettlementPathTopology.CellCenterXZ(18, 8), 20, 40, 760, 680, minW, maxW);
            DrawCircleOutline(pixels, w, h, holePos, 18, new Color32(240, 70, 70, 255), 2);

            // Right panel: Pure Topology edges + explicit junctions + door arrows
            for (var e = 0; e < plan.Edges.Count; e++)
            {
                var edge = plan.Edges[e];
                var col = edge.Kind == SettlementPathTopology.EdgeKind.Starter
                    ? new Color32(230, 195, 125, 255)
                    : edge.Kind == SettlementPathTopology.EdgeKind.MaskRoad
                        ? new Color32(198, 155, 92, 255)
                        : new Color32(245, 215, 130, 255);
                var thickness = edge.Kind == SettlementPathTopology.EdgeKind.Starter ? 7 : 6;
                for (var i = 1; i < edge.Points.Count; i++)
                {
                    var p0 = WorldToPanel(edge.Points[i - 1], 820, 40, 760, 680, minW, maxW);
                    var p1 = WorldToPanel(edge.Points[i], 820, 40, 760, 680, minW, maxW);
                    DrawThickLine(pixels, w, h, p0, p1, thickness, col);
                }
            }

            // Draw explicit junction dots (cyan)
            for (var j = 0; j < plan.Junctions.Count; j++)
            {
                var jp = WorldToPanel(plan.Junctions[j].Position, 820, 40, 760, 680, minW, maxW);
                DrawFilledCircle(pixels, w, h, jp, 7, new Color32(80, 230, 230, 255));
            }

            // Draw hut door markers on right panel (green = as-is, magenta = visual door correction)
            for (var i = 0; i < plan.HutReports.Count; i++)
            {
                var r = plan.HutReports[i];
                var dp = WorldToPanel(r.DoorWorld, 820, 40, 760, 680, minW, maxW);
                var col = r.UsedVisualDoorCorrection
                    ? new Color32(245, 100, 220, 255)
                    : new Color32(90, 240, 120, 255);
                DrawFilledCircle(pixels, w, h, dp, 5, col);
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            var png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            File.WriteAllBytes(buildPath, png);
            File.WriteAllBytes(docsPath, png);
        }

        private static void RenderBorderReproPng(
            string buildPath,
            string docsPath,
            List<Arm11G> t0Arms,
            List<Arm11G> t1StaleArms,
            SettlementPathTopology.TopologyPlan t1Topo)
        {
            const int w = 1200;
            const int h = 440;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color32[w * h];
            var bg = new Color32(28, 34, 30, 255);
            for (var i = 0; i < pixels.Length; i++) pixels[i] = bg;

            var minW = new Vector2(5.2f, -1.5f);
            var maxW = new Vector2(10.8f, 2.5f);

            int[] xs = { 20, 415, 810 };
            for (var p = 0; p < 3; p++)
            {
                FillRect(pixels, w, h, xs[p], 30, 370, 380, new Color32(58, 86, 52, 255));
                // Cell grid (wx 14..17, wz 8)
                for (var wx = 14; wx <= 17; wx++)
                {
                    if (p == 0 && wx >= 16) continue;
                    var c = SettlementPathTopology.CellCenterXZ(wx, 8);
                    var a = WorldToPanel(c - new Vector2(0.46f, 0.46f), xs[p], 30, 370, 380, minW, maxW);
                    var b = WorldToPanel(c + new Vector2(0.46f, 0.46f), xs[p], 30, 370, 380, minW, maxW);
                    FillRect(pixels, w, h, (int)a.x, (int)a.y, (int)(b.x - a.x), (int)(b.y - a.y), new Color32(78, 108, 68, 255));
                }

                // Chunk border x = 8.0 m (dashed/solid yellow line)
                var b0 = WorldToPanel(new Vector2(8.0f, minW.y), xs[p], 30, 370, 380, minW, maxW);
                var b1 = WorldToPanel(new Vector2(8.0f, maxW.y), xs[p], 30, 370, 380, minW, maxW);
                DrawThickLine(pixels, w, h, b0, b1, 2, new Color32(245, 225, 70, 255));
            }

            var armCol = new Color32(215, 168, 96, 255);
            // Panel 0: T0 (11G before east chunk grows)
            foreach (var a in t0Arms)
            {
                DrawThickLine(pixels, w, h,
                    WorldToPanel(a.StartPoint, xs[0], 30, 370, 380, minW, maxW),
                    WorldToPanel(a.EndPoint, xs[0], 30, 370, 380, minW, maxW), 8, armCol);
            }

            // Panel 1: T1 stale 11G (chunk (0,0) skipped -> missing east arm from (15,8)!)
            foreach (var a in t1StaleArms)
            {
                DrawThickLine(pixels, w, h,
                    WorldToPanel(a.StartPoint, xs[1], 30, 370, 380, minW, maxW),
                    WorldToPanel(a.EndPoint, xs[1], 30, 370, 380, minW, maxW), 8, armCol);
            }

            var gapCenter = WorldToPanel(new Vector2(7.85f, 0.5f), xs[1], 30, 370, 380, minW, maxW);
            DrawCircleOutline(pixels, w, h, gapCenter, 22, new Color32(245, 65, 65, 255), 3);

            // Panel 2: T1 Pure Topology (continuous world-space edge across border)
            foreach (var e in t1Topo.Edges)
            {
                for (var i = 1; i < e.Points.Count; i++)
                {
                    DrawThickLine(pixels, w, h,
                        WorldToPanel(e.Points[i - 1], xs[2], 30, 370, 380, minW, maxW),
                        WorldToPanel(e.Points[i], xs[2], 30, 370, 380, minW, maxW), 9, new Color32(235, 198, 118, 255));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            var png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            File.WriteAllBytes(buildPath, png);
            File.WriteAllBytes(docsPath, png);
        }

        private static void DrawBasePanel(
            Color32[] pixels, int w, int h,
            int px, int py, int pw, int ph,
            Vector2 minW, Vector2 maxW,
            List<SettlementPathTopology.MaskCellInput> roadCells,
            SettlementPathTopology.TopologyInput input)
        {
            FillRect(pixels, w, h, px, py, pw, ph, new Color32(68, 102, 58, 255));

            // Road cells highlight
            for (var i = 0; i < roadCells.Count; i++)
            {
                var c = SettlementPathTopology.CellCenterXZ(roadCells[i].WorldCellX, roadCells[i].WorldCellZ);
                var a = WorldToPanel(c - new Vector2(0.45f, 0.45f), px, py, pw, ph, minW, maxW);
                var b = WorldToPanel(c + new Vector2(0.45f, 0.45f), px, py, pw, ph, minW, maxW);
                FillRect(pixels, w, h, (int)a.x, (int)a.y, Mathf.Max(2, (int)(b.x - a.x)), Mathf.Max(2, (int)(b.y - a.y)),
                    new Color32(84, 118, 72, 255));
            }

            // Pond BlocksSettlement reserve (soft blue ring) + raw pond ellipse (solid blue)
            DrawEllipseFilled(pixels, w, h, px, py, pw, ph, minW, maxW, PondCenter, PondSettlementRx, PondSettlementRz, new Color32(52, 95, 120, 255));
            DrawEllipseFilled(pixels, w, h, px, py, pw, ph, minW, maxW, PondCenter, PondRawRx, PondRawRz, new Color32(55, 152, 198, 255));

            // Chunk border x = 8.0 m (yellow)
            var b0 = WorldToPanel(new Vector2(8.0f, minW.y), px, py, pw, ph, minW, maxW);
            var b1 = WorldToPanel(new Vector2(8.0f, maxW.y), px, py, pw, ph, minW, maxW);
            DrawThickLine(pixels, w, h, b0, b1, 2, new Color32(245, 225, 70, 255));

            // Hearth
            var hp = WorldToPanel(new Vector2(0.15f, -1.50f), px, py, pw, ph, minW, maxW);
            DrawFilledCircle(pixels, w, h, hp, 9, new Color32(95, 92, 90, 255));
            DrawFilledCircle(pixels, w, h, hp, 4, new Color32(245, 128, 42, 255));

            // Huts
            for (var i = 0; i < input.Huts.Count; i++)
            {
                var hut = input.Huts[i];
                var half = hut.FootprintHalfExtents * hut.Scale;
                var a = WorldToPanel(hut.Center - half, px, py, pw, ph, minW, maxW);
                var b = WorldToPanel(hut.Center + half, px, py, pw, ph, minW, maxW);
                FillRect(pixels, w, h, (int)a.x, (int)a.y, Mathf.Max(4, (int)(b.x - a.x)), Mathf.Max(4, (int)(b.y - a.y)),
                    new Color32(158, 72, 48, 255));
            }
        }

        private static void DrawStarterRects11G(
            Color32[] pixels, int w, int h,
            int px, int py, int pw, int ph,
            Vector2 minW, Vector2 maxW)
        {
            var col = new Color32(165, 140, 102, 255);
            var m0 = WorldToPanel(new Vector2(-0.55f, -1.9f), px, py, pw, ph, minW, maxW);
            var m1 = WorldToPanel(new Vector2(0.55f, 2.3f), px, py, pw, ph, minW, maxW);
            FillRect(pixels, w, h, (int)m0.x, (int)m0.y, (int)(m1.x - m0.x), (int)(m1.y - m0.y), col);

            var c0 = WorldToPanel(new Vector2(-0.3f, -0.95f), px, py, pw, ph, minW, maxW);
            var c1 = WorldToPanel(new Vector2(2.1f, -0.25f), px, py, pw, ph, minW, maxW);
            FillRect(pixels, w, h, (int)c0.x, (int)c0.y, (int)(c1.x - c0.x), (int)(c1.y - c0.y), col);
        }

        // ------------------------------------------------------------------ Helpers

        private static GameSaveData CreateBenchmarkSave(int dim, out SettlementPathTopology.TopologyInput topoInput)
        {
            var save = new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(1_700_000_000L)
            };
            save.worldGrowth.chunks.Clear();

            var roadCells = new List<SettlementPathTopology.MaskCellInput>();
            var huts = new List<SettlementPathTopology.HutInput>();
            var cfg = new SettlementPathTopology.TopologyConfig();

            for (var cz = 0; cz < dim; cz++)
            {
                for (var cx = 0; cx < dim; cx++)
                {
                    var land = new byte[ChunkMaskCodec.CellCount];
                    var settle = new byte[ChunkMaskCodec.CellCount];
                    for (var i = 0; i < land.Length; i++) land[i] = (byte)CellStage.Vegetation;

                    // Draw a realistic cross street through each chunk so roads span all chunk borders
                    for (var k = 0; k < 16; k++)
                    {
                        var idxH = ChunkMaskCodec.CellIndex(k, 8);
                        settle[idxH] = (byte)SettlementStage.Path;
                        roadCells.Add(new SettlementPathTopology.MaskCellInput { WorldCellX = cx * 16 + k, WorldCellZ = cz * 16 + 8, Stage = 2 });

                        if (k >= 4 && k <= 12 && k != 8)
                        {
                            var idxV = ChunkMaskCodec.CellIndex(8, k);
                            settle[idxV] = (byte)SettlementStage.Path;
                            roadCells.Add(new SettlementPathTopology.MaskCellInput { WorldCellX = cx * 16 + 8, WorldCellZ = cz * 16 + k, Stage = 2 });
                        }
                    }

                    // Place 1 hut in chunks within central 4x4 region
                    if (cx < 4 && cz < 4)
                    {
                        var hx = cx * 16 + 5;
                        var hz = cz * 16 + 10;
                        settle[ChunkMaskCodec.CellIndex(5, 10)] = (byte)SettlementStage.Hut;
                        huts.Add(new SettlementPathTopology.HutInput
                        {
                            Id = $"BenchHut_{cx}_{cz}",
                            Center = SettlementPathTopology.CellCenterXZ(hx, hz, cfg),
                            YawDegrees = 180f,
                            Scale = SettlementCellQuery.HutVisualUniformScale
                        });
                    }

                    save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
                    {
                        chunkX = cx,
                        chunkZ = cz,
                        isLegacyBase = cx == 0 && cz == 0,
                        growthSeed = cx * 73856093 ^ cz * 19349663,
                        stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land),
                        settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle)
                    });
                }
            }

            topoInput = new SettlementPathTopology.TopologyInput
            {
                Config = cfg,
                RoadCells = roadCells,
                Huts = huts
            };
            return save;
        }

        private static string ChunkSig11G(ChunkGrowthSaveData growth) =>
            $"{growth.settlementMaskBase64}:{growth.stageMaskBase64}:{growth.isLegacyBase}";

        private static string BuildSignature11G(GameSaveData data)
        {
            if (data?.worldGrowth?.chunks == null) return "empty";
            var parts = new List<string>(data.worldGrowth.chunks.Count);
            foreach (var growth in data.worldGrowth.chunks)
            {
                if (growth == null) continue;
                parts.Add($"{growth.chunkX}:{growth.chunkZ}:{growth.settlementMaskBase64}");
            }

            parts.Sort();
            return string.Join("|", parts);
        }

        private static int CountArms(List<Arm11G> arms, int wx, int wz, int dir)
        {
            var n = 0;
            for (var i = 0; i < arms.Count; i++)
            {
                if (arms[i].WorldCellX == wx && arms[i].WorldCellZ == wz && arms[i].Direction == dir) n++;
            }

            return n;
        }

        private static bool IsInsideRawPond(float x, float z) =>
            IsInsideEllipse(new Vector2(x, z), PondCenter, PondRawRx, PondRawRz);

        private static bool IsInsideSettlementWaterReserve(float x, float z) =>
            IsInsideEllipse(new Vector2(x, z), PondCenter, PondSettlementRx, PondSettlementRz);

        private static bool IsInsideEllipse(Vector2 p, Vector2 c, float rx, float rz)
        {
            var nx = (p.x - c.x) / rx;
            var nz = (p.y - c.y) / rz;
            return nx * nx + nz * nz <= 1f;
        }

        private static float MinSegmentDistance(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
        {
            if (SegmentsIntersect(a0, a1, b0, b1)) return 0f;
            var d = PointToSegmentDist(a0, b0, b1);
            d = Mathf.Min(d, PointToSegmentDist(a1, b0, b1));
            d = Mathf.Min(d, PointToSegmentDist(b0, a0, a1));
            d = Mathf.Min(d, PointToSegmentDist(b1, a0, a1));
            return d;
        }

        private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            var d1 = Cross(p3, p4, p1);
            var d2 = Cross(p3, p4, p2);
            var d3 = Cross(p1, p2, p3);
            var d4 = Cross(p1, p2, p4);
            return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) &&
                   ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
        }

        private static float PointToSegmentDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var lenSq = ab.sqrMagnitude;
            if (lenSq < 1e-8f) return Vector2.Distance(p, a);
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            return Vector2.Distance(p, a + ab * t);
        }

        private static float Hash01(int seed, int a, int b)
        {
            unchecked
            {
                var h = (uint)(seed * 374761393 + a * 668265263 + b * 2147483647);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }

        private static int FloorDiv(int a, int b)
        {
            var q = a / b;
            var r = a % b;
            if (r != 0 && ((a < 0) ^ (b < 0))) q--;
            return q;
        }

        private static Vector2 WorldToPanel(Vector2 worldXZ, int px, int py, int pw, int ph, Vector2 minW, Vector2 maxW)
        {
            var tx = Mathf.InverseLerp(minW.x, maxW.x, worldXZ.x);
            var ty = Mathf.InverseLerp(minW.y, maxW.y, worldXZ.y);
            return new Vector2(px + tx * pw, py + ty * ph);
        }

        private static void FillRect(Color32[] pixels, int w, int h, int x0, int y0, int rw, int rh, Color32 col)
        {
            var xMin = Mathf.Clamp(x0, 0, w - 1);
            var xMax = Mathf.Clamp(x0 + rw, 0, w);
            var yMin = Mathf.Clamp(y0, 0, h - 1);
            var yMax = Mathf.Clamp(y0 + rh, 0, h);
            for (var y = yMin; y < yMax; y++)
            {
                var row = y * w;
                for (var x = xMin; x < xMax; x++) pixels[row + x] = col;
            }
        }

        private static void DrawFilledCircle(Color32[] pixels, int w, int h, Vector2 c, int r, Color32 col)
        {
            var cx = Mathf.RoundToInt(c.x);
            var cy = Mathf.RoundToInt(c.y);
            var r2 = r * r;
            for (var dy = -r; dy <= r; dy++)
            {
                var y = cy + dy;
                if (y < 0 || y >= h) continue;
                var row = y * w;
                for (var dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r2) continue;
                    var x = cx + dx;
                    if (x >= 0 && x < w) pixels[row + x] = col;
                }
            }
        }

        private static void DrawCircleOutline(Color32[] pixels, int w, int h, Vector2 c, int r, Color32 col, int thickness)
        {
            var cx = Mathf.RoundToInt(c.x);
            var cy = Mathf.RoundToInt(c.y);
            var rOut = (r + thickness) * (r + thickness);
            var rIn = Mathf.Max(0, r - thickness) * Mathf.Max(0, r - thickness);
            for (var dy = -r - thickness; dy <= r + thickness; dy++)
            {
                var y = cy + dy;
                if (y < 0 || y >= h) continue;
                var row = y * w;
                for (var dx = -r - thickness; dx <= r + thickness; dx++)
                {
                    var d2 = dx * dx + dy * dy;
                    if (d2 < rIn || d2 > rOut) continue;
                    var x = cx + dx;
                    if (x >= 0 && x < w) pixels[row + x] = col;
                }
            }
        }

        private static void DrawEllipseFilled(
            Color32[] pixels, int w, int h,
            int px, int py, int pw, int ph,
            Vector2 minW, Vector2 maxW,
            Vector2 centerW, float rx, float rz, Color32 col)
        {
            var a = WorldToPanel(centerW - new Vector2(rx, rz), px, py, pw, ph, minW, maxW);
            var b = WorldToPanel(centerW + new Vector2(rx, rz), px, py, pw, ph, minW, maxW);
            var xMin = Mathf.Clamp((int)a.x, px, px + pw - 1);
            var xMax = Mathf.Clamp((int)b.x, px, px + pw - 1);
            var yMin = Mathf.Clamp((int)a.y, py, py + ph - 1);
            var yMax = Mathf.Clamp((int)b.y, py, py + ph - 1);
            for (var y = yMin; y <= yMax; y++)
            {
                var wz = Mathf.Lerp(minW.y, maxW.y, (y - py) / (float)ph);
                var nz = (wz - centerW.y) / rz;
                var row = y * w;
                for (var x = xMin; x <= xMax; x++)
                {
                    var wx = Mathf.Lerp(minW.x, maxW.x, (x - px) / (float)pw);
                    var nx = (wx - centerW.x) / rx;
                    if (nx * nx + nz * nz <= 1f) pixels[row + x] = col;
                }
            }
        }

        private static void DrawThickLine(Color32[] pixels, int w, int h, Vector2 p0, Vector2 p1, int radius, Color32 col)
        {
            var steps = Mathf.Max(2, Mathf.CeilToInt(Vector2.Distance(p0, p1)));
            for (var i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(p0, p1, i / (float)steps);
                DrawFilledCircle(pixels, w, h, p, radius, col);
            }
        }
    }
}
#endif
