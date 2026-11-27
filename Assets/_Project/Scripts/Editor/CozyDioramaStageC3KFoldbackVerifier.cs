using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.Editor
{
    /// <summary>
    /// Stage C-3K: geometry-only foldback verification of the continuous path renderer.
    /// Pure in-memory: synthetic road shapes (U-turns, zigzags, T/X with bends, dense legacy blobs, chunk-seam shapes)
    /// + real grown settlements from the C-3J growth rules are fed through SettlementPathTopology.Build and
    /// SettlementPathMeshBuilder.BuildPerChunk on flat and hilly terrain. No scene, no save file, no Play Mode.
    /// Usage: -executeMethod AfkIsland.Editor.CozyDioramaStageC3KFoldbackVerifier.RunBatchVerification -reportTag baseline|fixed
    /// A case is clean when foldbacks == 0, ribbon/cap/junction winding flips == 0 and downward triangles == 0.
    /// </summary>
    public static class CozyDioramaStageC3KFoldbackVerifier
    {
        private const string ReportDir = "Docs/CozyDiorama/StageC3K-Shots";
        private static bool _quick;

        private sealed class ShapeCase
        {
            public string Name;
            public List<Vector2Int> Cells = new List<Vector2Int>();
            public List<Vector2Int> Huts = new List<Vector2Int>();
        }

        private sealed class CaseResult
        {
            public string Name;
            public string Terrain;
            public int Cells, Edges, Junctions;
            public int Foldbacks, FlipsRibbon, FlipsCap, FlipsJunction, Downward, Degenerate;
            public float MinUpDot;
            public int BorderPairs;
            public float MaxBorderPos, MaxBorderEdge, MaxAttach;
            public string Hash;
            public List<string> Sites;
            public bool Clean => Foldbacks == 0 && FlipsRibbon == 0 && FlipsCap == 0 && FlipsJunction == 0 && Downward == 0
                                 && MaxBorderPos <= 1e-4f && MaxBorderEdge <= 1e-4f && MaxAttach <= 1e-4f;
        }

        public static void RunBatchVerification()
        {
            var tag = "run";
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++) if (args[i] == "-reportTag") tag = args[i + 1];

            _quick = Array.IndexOf(args, "-quick") >= 0;
            bool ok;
            try { ok = Run(tag); }
            catch (Exception ex)
            {
                Debug.LogError("[C-3K] verifier exception: " + ex);
                ok = false;
            }

            EditorApplication.Exit(tag == "baseline" || ok ? 0 : 1);
        }

        private static bool Run(string tag)
        {
            var report = new StringBuilder();
            report.AppendLine($"Stage C-3K foldback verification [{tag}] — geometry only, in-memory (no scene, no save, no Play Mode)");

            var terrains = new (string name, Func<float, float, float> fn)[]
            {
                ("flat", (x, z) => 0f),
                ("hilly", (x, z) => 0.35f * Mathf.Sin(x * 0.9f) * Mathf.Cos(z * 0.7f) + 0.02f * x),
            };

            var all = new List<CaseResult>();

            // ---------- A. Hand-made shapes ----------
            var shapes = BuildShapes();
            report.AppendLine();
            report.AppendLine($"A. Synthetic shapes: {shapes.Count} shapes x {terrains.Length} terrains");
            var aDirty = 0;
            foreach (var s in shapes)
            {
                foreach (var t in terrains)
                {
                    var r = Evaluate(s, t.name, t.fn);
                    all.Add(r);
                    if (!r.Clean)
                    {
                        aDirty++;
                        AppendResult(report, r);
                    }
                }
            }

            report.AppendLine($"   dirty={aDirty} / {shapes.Count * terrains.Length}");

            // ---------- B. Random dense / legacy blobs (also across chunk seams) ----------
            var randomCount = 160;
            report.AppendLine();
            report.AppendLine($"B. Random road sets (walks incl. dense blobs, shifted over chunk seams): {randomCount} sets x {terrains.Length} terrains");
            var bDirty = 0;
            var bTotals = new CaseResult { Name = "B-total" };
            for (var n = 0; n < randomCount; n++)
            {
                var s = RandomShape(n);
                foreach (var t in terrains)
                {
                    var r = Evaluate(s, t.name, t.fn);
                    all.Add(r);
                    Accumulate(bTotals, r);
                    if (!r.Clean)
                    {
                        bDirty++;
                        if (bDirty <= 12) AppendResult(report, r);
                    }
                }
            }

            report.AppendLine($"   dirty={bDirty} / {randomCount * terrains.Length}; totals foldbacks={bTotals.Foldbacks} flipsRibbon={bTotals.FlipsRibbon} flipsCap={bTotals.FlipsCap} flipsJunction={bTotals.FlipsJunction} downward={bTotals.Downward}");

            // ---------- C. Real grown settlements (C-3J rules) ----------
            var growthSeeds = _quick ? 0 : 16;
            report.AppendLine();
            report.AppendLine($"C. Grown settlements (C-3J growth, 12 x 7d): {growthSeeds} seeds x {terrains.Length} terrains");
            var cDirty = 0;
            var cTotals = new CaseResult { Name = "C-total" };
            for (var seed = 301; seed < 301 + growthSeeds; seed++)
            {
                var grown = CozyDioramaStageC3IStarterSettlementVerifier.GrowSilently(
                    CozyDioramaStageC3IStarterSettlementVerifier.StarterSeeded(seed), 12);
                var s = new ShapeCase { Name = $"grown-seed{seed}" };
                foreach (var k in CozyDioramaStageC3IStarterSettlementVerifier.RoadSet(grown)) s.Cells.Add(Unkey(k));
                foreach (var k in CozyDioramaStageC3IStarterSettlementVerifier.HutSet(grown)) s.Huts.Add(Unkey(k));
                foreach (var t in terrains)
                {
                    var r = Evaluate(s, t.name, t.fn);
                    all.Add(r);
                    Accumulate(cTotals, r);
                    if (!r.Clean)
                    {
                        cDirty++;
                        AppendResult(report, r);
                    }
                }
            }

            report.AppendLine($"   dirty={cDirty} / {growthSeeds * terrains.Length}; totals foldbacks={cTotals.Foldbacks} flipsRibbon={cTotals.FlipsRibbon} flipsCap={cTotals.FlipsCap} flipsJunction={cTotals.FlipsJunction} downward={cTotals.Downward}");

            // ---------- D. Determinism ----------
            report.AppendLine();
            var detOk = true;
            foreach (var s in shapes)
            {
                var a = Evaluate(s, "hilly", terrains[1].fn);
                var b = Evaluate(s, "hilly", terrains[1].fn);
                if (a.Hash != b.Hash) { detOk = false; report.AppendLine($"   NON-DETERMINISTIC: {s.Name} {a.Hash} vs {b.Hash}"); }
            }

            report.AppendLine($"D. Determinism (each shape built twice, same hash): ok={detOk}");

            // ---------- E. Renderer lifecycle (no scene is saved; temp GameObject only) ----------
            report.AppendLine();
            var lifeOk = RunLifecycle(report, terrains[1].fn);

            // ---------- F. Build cost A/B (legacy path = InnerOffsetRadiusFraction 0, new path = default) ----------
            if (!_quick)
            {
                var timingShapes = new List<SettlementPathTopology.TopologyPlan>();
                for (var n = 0; n < 16; n++) timingShapes.Add(MakePlan(RandomShape(n * 5 + 1)));
                double Time(float fraction)
                {
                    var cfgT = new SettlementPathMeshBuilder.MeshBuildConfig { InnerOffsetRadiusFraction = fraction };
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (var rep = 0; rep < 10; rep++)
                    {
                        foreach (var plan in timingShapes)
                        {
                            var build = SettlementPathMeshBuilder.BuildPerChunk(plan, terrains[1].fn, cfgT);
                            foreach (var kv in build.Chunks)
                            {
                                if (kv.Value.CoreMesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.CoreMesh);
                                if (kv.Value.FringeMesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.FringeMesh);
                            }
                        }
                    }

                    sw.Stop();
                    return sw.Elapsed.TotalMilliseconds / (10 * timingShapes.Count);
                }

                Time(0f); // warm-up
                var legacyMs = Time(0f);
                var newMs = Time(0.8f);
                report.AppendLine();
                report.AppendLine($"F. Build cost (Editor Mono JIT, 16 random networks x 10 repeats): legacy={legacyMs:F2} ms/plan, C-3K={newMs:F2} ms/plan, ratio={(legacyMs > 0 ? newMs / legacyMs : 0):F2}x");
            }

            // ---------- Summary ----------
            var total = new CaseResult { Name = "ALL" };
            var minUp = 1f;
            var cleanCount = 0;
            foreach (var r in all)
            {
                Accumulate(total, r);
                if (r.MinUpDot < minUp) minUp = r.MinUpDot;
                if (r.Clean) cleanCount++;
            }

            report.AppendLine();
            report.AppendLine($"SUMMARY [{tag}]: cases={all.Count} clean={cleanCount} dirty={all.Count - cleanCount}; foldbacks={total.Foldbacks} flipsRibbon={total.FlipsRibbon} flipsCap={total.FlipsCap} flipsJunction={total.FlipsJunction} downward={total.Downward} degenerateSkipped={total.Degenerate} minUpwardNormalDot={minUp:F4}");
            report.AppendLine($"   seams: sharedBorderPointPairs={total.BorderPairs} maxBorderCentreDelta={total.MaxBorderPos:F6} maxBorderLeftRightDelta={total.MaxBorderEdge:F6} maxJunctionAttachDelta={total.MaxAttach:F6}");
            var ok = cleanCount == all.Count && detOk && lifeOk;
            report.AppendLine($"ACCEPTANCE (zero foldbacks, zero winding flips, zero downward triangles, deterministic) [{tag}]: {(ok ? "PASS" : "FAIL")}");

            var dir = Path.Combine(Directory.GetCurrentDirectory(), ReportDir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"c3k-foldback-{tag}-report.txt"), report.ToString());
            Debug.Log("[C-3K] finished tag=" + tag + " acceptance=" + ok + " clean=" + cleanCount + "/" + all.Count);
            return ok;
        }

        // ------------------------------------------------------------ renderer lifecycle

        private static bool RunLifecycle(StringBuilder report, Func<float, float, float> terrain)
        {
            var root = new GameObject("C3K_LifecycleProbe") { hideFlags = HideFlags.HideAndDontSave };
            var coreMat = new Material(Shader.Find("Sprites/Default"));
            var fringeMat = new Material(Shader.Find("Sprites/Default"));
            var ok = true;
            try
            {
                var renderer = root.AddComponent<SettlementPathNetworkRenderer>();
                renderer.ConfigureMaterials(coreMat, fringeMat);

                // Two disconnected roads in two different chunks: (0,0) and (1,1).
                var a = Shape("A", 3,3, 4,3, 5,3, 6,3, 7,3);
                var b = Shape("B", 20,20, 21,20, 22,20, 22,21, 22,22);
                var bGrown = Shape("B2", 20,20, 21,20, 22,20, 22,21, 22,22, 23,22);
                var planBase = MakePlan(Merge(a, b));
                var planChanged = MakePlan(Merge(a, bGrown));
                var cfg = new SettlementPathMeshBuilder.MeshBuildConfig();

                renderer.ApplyPlan(planBase, terrain, cfg);
                var s1 = renderer.GetStats();
                var p1 = s1.ActiveChunkCount == 2 && s1.LastRebuiltChunkCount == 2 && s1.LastReusedChunkCount == 0 && s1.LiveOwnedMeshCount == s1.ActiveMeshCount;

                var createdBefore = s1.TotalMeshesCreated;
                renderer.ApplyPlan(planBase, terrain, cfg);
                var s2 = renderer.GetStats();
                var p2 = s2.LastRebuiltChunkCount == 0 && s2.LastReusedChunkCount == 2 && s2.TotalMeshesCreated == createdBefore;

                renderer.ApplyPlan(planChanged, terrain, cfg);
                var s3 = renderer.GetStats();
                var p3 = s3.LastRebuiltChunkCount == 1 && s3.LastReusedChunkCount == 1 && s3.LiveOwnedMeshCount == s3.ActiveMeshCount;

                var forced = renderer.ApplyPlan(planChanged, terrain, cfg, forceFullRebuild: true);
                var s4 = renderer.GetStats();
                var fresh = SettlementPathMeshBuilder.BuildPerChunk(planChanged, terrain, cfg);
                var p4 = s4.LastRebuiltChunkCount == 2 && s4.LiveOwnedMeshCount == s4.ActiveMeshCount && forced.MeshHash == fresh.MeshHash;
                foreach (var kv in fresh.Chunks)
                {
                    if (kv.Value.CoreMesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.CoreMesh);
                    if (kv.Value.FringeMesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.FringeMesh);
                }

                renderer.ApplyPlan(planChanged, terrain, cfg, terrainVersion: 7);
                var s4b = renderer.GetStats();
                var p4b = s4b.LastRebuiltChunkCount == 2 && s4b.LiveOwnedMeshCount == s4b.ActiveMeshCount;

                var shared = s4b.UsesSharedMaterialsOnly;
                var foldbacks = renderer.LastBuildReport != null ? renderer.LastBuildReport.FoldbackTriangles : -1;

                renderer.Clear();
                var s5 = renderer.GetStats();
                var p5 = s5.ActiveChunkCount == 0 && s5.ActiveMeshCount == 0 && s5.LiveOwnedMeshCount == 0 && s5.TotalMeshesCreated == s5.TotalMeshesDestroyed;

                ok = p1 && p2 && p3 && p4 && p4b && p5 && shared && foldbacks == 0 && root.transform.childCount == 0;
                report.AppendLine($"E. Renderer lifecycle: ok={ok}");
                report.AppendLine($"   full build={p1}; identical re-apply reuses all chunks, no new meshes={p2}; one changed chunk -> rebuilt 1, reused 1={p3}");
                report.AppendLine($"   force full rebuild equals fresh build hash={p4}; terrainVersion bump rebuilds all={p4b}; sharedMaterialsOnly={shared}; foldbacks={foldbacks}");
                report.AppendLine($"   Clear(): chunks=0, live meshes=0, created==destroyed ({s5.TotalMeshesCreated}/{s5.TotalMeshesDestroyed}), root children=0 -> {p5 && root.transform.childCount == 0}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(coreMat);
                UnityEngine.Object.DestroyImmediate(fringeMat);
            }

            return ok;
        }

        private static ShapeCase Merge(ShapeCase x, ShapeCase y)
        {
            var s = new ShapeCase { Name = x.Name + "+" + y.Name };
            s.Cells.AddRange(x.Cells);
            s.Cells.AddRange(y.Cells);
            s.Huts.AddRange(x.Huts);
            s.Huts.AddRange(y.Huts);
            return s;
        }

        // ------------------------------------------------------------ evaluation

        private static SettlementPathTopology.TopologyPlan MakePlan(ShapeCase s)
        {
            var cfg = new SettlementPathTopology.TopologyConfig { ChunkSizeMeters = 16f, GridSize = 16 };
            var input = new SettlementPathTopology.TopologyInput
            {
                Config = cfg,
                IsInsideLand = (x, z) => true,
                IsBlockedByWater = (x, z) => false
            };
            foreach (var c in s.Cells)
            {
                input.RoadCells.Add(new SettlementPathTopology.MaskCellInput { WorldCellX = c.x, WorldCellZ = c.y, Stage = (byte)SettlementStage.Path });
            }

            foreach (var h in s.Huts)
            {
                var center = CellCenter(h.x, h.y, cfg);
                input.Huts.Add(new SettlementPathTopology.HutInput
                {
                    Id = $"H_{h.x}_{h.y}",
                    Center = center,
                    YawDegrees = (h.x * 37 + h.y * 91) % 360,
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(0.675f, 0.575f),
                    DoorLocalZ = 0.598f,
                    AllowVisualDoorCorrection = true
                });
            }

            return SettlementPathTopology.Build(input);
        }

        private static CaseResult Evaluate(ShapeCase s, string terrainName, Func<float, float, float> terrain)
        {
            var plan = MakePlan(s);
            var meshCfg = new SettlementPathMeshBuilder.MeshBuildConfig();
            var build = SettlementPathMeshBuilder.BuildPerChunk(plan, terrain, meshCfg);

            var r = new CaseResult
            {
                Name = s.Name,
                Terrain = terrainName,
                Cells = s.Cells.Count,
                Edges = plan.Edges.Count,
                Junctions = plan.Junctions.Count,
                Foldbacks = build.FoldbackTriangles,
                FlipsRibbon = build.WindingFlipsRibbon,
                FlipsCap = build.WindingFlipsCap,
                FlipsJunction = build.WindingFlipsJunction,
                Downward = build.DownwardTriangles,
                Degenerate = build.DegenerateSkipped,
                MinUpDot = build.MinUpwardNormalDot,
                BorderPairs = build.BorderSharedPointPairs,
                MaxBorderPos = build.MaxBorderPositionDelta,
                MaxBorderEdge = build.MaxBorderEdgeDelta,
                MaxAttach = build.MaxJunctionAttachDelta,
                Hash = build.MeshHash,
                Sites = build.DefectSites
            };

            foreach (var kv in build.Chunks)
            {
                if (kv.Value.CoreMesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.CoreMesh);
                if (kv.Value.FringeMesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.FringeMesh);
            }

            return r;
        }

        private static void Accumulate(CaseResult total, CaseResult r)
        {
            total.Foldbacks += r.Foldbacks;
            total.FlipsRibbon += r.FlipsRibbon;
            total.FlipsCap += r.FlipsCap;
            total.FlipsJunction += r.FlipsJunction;
            total.Downward += r.Downward;
            total.Degenerate += r.Degenerate;
            total.BorderPairs += r.BorderPairs;
            total.MaxBorderPos = Mathf.Max(total.MaxBorderPos, r.MaxBorderPos);
            total.MaxBorderEdge = Mathf.Max(total.MaxBorderEdge, r.MaxBorderEdge);
            total.MaxAttach = Mathf.Max(total.MaxAttach, r.MaxAttach);
        }

        private static void AppendResult(StringBuilder sb, CaseResult r)
        {
            sb.AppendLine($"   DIRTY {r.Name} [{r.Terrain}] cells={r.Cells} edges={r.Edges} junctions={r.Junctions} foldbacks={r.Foldbacks} flips(ribbon/cap/junction)={r.FlipsRibbon}/{r.FlipsCap}/{r.FlipsJunction} downward={r.Downward} minUpDot={r.MinUpDot:F3} borderPos={r.MaxBorderPos:F5} borderEdge={r.MaxBorderEdge:F5} attach={r.MaxAttach:F5}");
            if (r.Sites != null)
            {
                for (var i = 0; i < r.Sites.Count && i < 6; i++) sb.AppendLine("        " + r.Sites[i]);
            }
        }

        // ------------------------------------------------------------ fixtures

        private static Vector2 CellCenter(int wx, int wz, SettlementPathTopology.TopologyConfig cfg)
        {
            var o = cfg.WorldGridOrigin;
            return new Vector2(o + (wx + 0.5f) * cfg.CellSizeMeters, o + (wz + 0.5f) * cfg.CellSizeMeters);
        }

        private static Vector2Int Unkey(long k) => new Vector2Int((int)(k >> 32), (int)(uint)(k & 0xFFFFFFFFL));

        private static ShapeCase Shape(string name, params int[] xz)
        {
            var s = new ShapeCase { Name = name };
            for (var i = 0; i + 1 < xz.Length; i += 2) s.Cells.Add(new Vector2Int(xz[i], xz[i + 1]));
            return s;
        }

        private static ShapeCase Shift(ShapeCase src, string suffix, int dx, int dz)
        {
            var s = new ShapeCase { Name = src.Name + suffix };
            foreach (var c in src.Cells) s.Cells.Add(new Vector2Int(c.x + dx, c.y + dz));
            foreach (var h in src.Huts) s.Huts.Add(new Vector2Int(h.x + dx, h.y + dz));
            return s;
        }

        private static List<ShapeCase> BuildShapes()
        {
            var baseShapes = new List<ShapeCase>
            {
                Shape("straight6", 5,5, 6,5, 7,5, 8,5, 9,5, 10,5),
                Shape("L-bend", 5,5, 6,5, 7,5, 7,6, 7,7),
                Shape("U-gap1", 5,5, 5,6, 5,7, 6,7, 7,7, 7,6, 7,5),
                Shape("U-gap2", 5,5, 5,6, 5,7, 6,7, 7,7, 8,7, 8,6, 8,5),
                Shape("U-gap1-long", 5,3, 5,4, 5,5, 5,6, 5,7, 6,7, 7,7, 7,6, 7,5, 7,4, 7,3),
                Shape("S-curve", 3,3, 4,3, 5,3, 5,4, 5,5, 6,5, 7,5),
                Shape("staircase", 3,3, 4,3, 4,4, 5,4, 5,5, 6,5, 6,6, 7,6, 7,7),
                Shape("zigzag-sharp", 3,3, 4,3, 4,4, 5,4, 5,3, 6,3, 6,4, 7,4),
                Shape("hook-dead-end", 5,5, 6,5, 7,5, 7,6, 7,7, 6,7),
                Shape("hairpin-dead-end", 5,5, 6,5, 7,5, 7,6, 6,6),
                Shape("T-straight", 4,5, 5,5, 6,5, 5,6, 5,7),
                Shape("T-bend-arm", 4,5, 5,5, 6,5, 5,6, 5,7, 6,7, 7,7),
                Shape("T-adjacent-bend", 4,5, 5,5, 6,5, 6,6, 6,7),
                Shape("X-cross", 4,5, 5,5, 6,5, 5,4, 5,6),
                Shape("X-bent-arms", 3,5, 4,5, 5,5, 6,5, 5,4, 5,3, 4,3, 5,6, 5,7, 6,7),
                Shape("ring3x3", 4,4, 5,4, 6,4, 6,5, 6,6, 5,6, 4,6, 4,5),
                Shape("block2x2", 5,5, 6,5, 5,6, 6,6),
                Shape("block3x3", 4,4, 5,4, 6,4, 4,5, 5,5, 6,5, 4,6, 5,6, 6,6),
                Shape("comb", 3,3, 4,3, 5,3, 6,3, 7,3, 3,4, 5,4, 7,4, 3,5, 5,5, 7,5),
                Shape("snake", 3,3, 4,3, 5,3, 5,4, 5,5, 4,5, 3,5, 3,6, 3,7, 4,7, 5,7),
                Shape("spiral", 3,3, 4,3, 5,3, 6,3, 6,4, 6,5, 6,6, 5,6, 4,6, 3,6, 3,5, 3,4, 4,4)
            };

            var list = new List<ShapeCase>();
            foreach (var b in baseShapes)
            {
                list.Add(b);
                list.Add(Shift(b, "@seamX", 12, 0));      // crosses chunk border x = +8 (cell 15|16)
                list.Add(Shift(b, "@seamZ", 0, 12));
                list.Add(Shift(b, "@seamXZ", 12, 12));
                list.Add(Shift(b, "@neg", -14, -14));
            }

            // Hut-attached variants (door branch geometry joins the ribbon network)
            var huts = new ShapeCase { Name = "hut-on-U" };
            huts.Cells.AddRange(Shape("u", 5,5, 5,6, 5,7, 6,7, 7,7, 7,6, 7,5).Cells);
            huts.Huts.Add(new Vector2Int(6, 6));
            list.Add(huts);
            var huts2 = new ShapeCase { Name = "hut-at-bend" };
            huts2.Cells.AddRange(Shape("l", 5,5, 6,5, 7,5, 7,6, 7,7).Cells);
            huts2.Huts.Add(new Vector2Int(8, 5));
            list.Add(huts2);
            return list;
        }

        private static ShapeCase RandomShape(int n)
        {
            var rng = new System.Random(900 + n * 7919);
            var s = new ShapeCase { Name = $"rand{n:D3}" };
            var set = new HashSet<long>();
            var shiftX = new[] { 0, 12, -14, 12, 0 }[n % 5];
            var shiftZ = new[] { 0, 0, -14, 12, 12 }[n % 5];
            var x = 6;
            var z = 6;
            var steps = 30 + rng.Next(50);
            var dense = n % 4 == 0; // every 4th set also fills random neighbours (legacy dense blobs)
            for (var i = 0; i < steps; i++)
            {
                set.Add(((long)x << 32) ^ (uint)z);
                var d = rng.Next(4);
                if (d == 0) x++; else if (d == 1) x--; else if (d == 2) z++; else z--;
                if (dense && rng.Next(100) < 25)
                {
                    set.Add(((long)(x + 1) << 32) ^ (uint)z);
                }
            }

            foreach (var k in set)
            {
                var c = Unkey(k);
                s.Cells.Add(new Vector2Int(c.x + shiftX, c.y + shiftZ));
            }

            return s;
        }
    }
}
