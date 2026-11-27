using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using AfkIsland.Core;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AfkIsland.Editor
{
    /// <summary>
    /// Stage C-3J growth-shape verifier. Grows the C-3I starter settlement for 12 x 7 days with 8 fixed growth seeds
    /// and measures the topology of the resulting road network: components, isolated cells, road cells inside 2x2 squares,
    /// branch ends, T/X junctions, parallel lanes separated by a one-cell gap, cycles. Writes an ASCII map per seed and
    /// screenshots. Usage: -executeMethod AfkIsland.Editor.CozyDioramaStageC3JGrowthShapeVerifier.RunBatchVerification
    /// -reportTag baseline|branching. In-memory saves only; MainIsland.unity is opened and never saved.
    /// </summary>
    public static class CozyDioramaStageC3JGrowthShapeVerifier
    {
        private const string MainIslandScenePath = "Assets/_Project/Scenes/MainIsland.unity";
        private const string OutDir = "Docs/CozyDiorama/StageC3J-Shots";
        private const int Sessions = 12;
        private static readonly int[] Seeds = { 101, 102, 103, 104, 105, 106, 107, 108 };

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3J/Run Growth Shape Verification")]
        public static void RunMenu()
        {
            var ok = Run("manual");
            Debug.Log("[C-3J] growth shape " + (ok ? "PASS" : "FAIL"));
        }

        public static void RunBatchVerification()
        {
            var tag = "untagged";
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++) if (args[i] == "-reportTag") tag = args[i + 1];

            bool ok;
            try { ok = Run(tag); }
            catch (Exception ex) { Debug.LogError("[C-3J] FAILED with exception: " + ex); ok = false; }

            Debug.Log("[C-3J] finished tag=" + tag + " acceptance=" + ok);
            // The baseline is a recorded control, not a gate.
            EditorApplication.Exit(tag == "baseline" || ok ? 0 : 1);
        }

        private sealed class Shape
        {
            public int Cells, Components, Isolated, In2x2, Endpoints, Tees, Crosses, Bends, Straights, GapPairs, CycleRank;
            public bool StarterHutKept, StarterHutAttached;
        }

        private static bool Run(string tag)
        {
            Directory.CreateDirectory(OutDir);
            var report = new StringBuilder();
            report.AppendLine($"Stage C-3J growth shape report [{tag}]");
            report.AppendLine($"{Seeds.Length} seeds x {Sessions} x 7d offline sessions from the C-3I starter settlement (in-memory).");
            report.AppendLine();

            var grownBySeed = new Dictionary<int, GameSaveData>();
            var allOk = true;
            var sum = new Shape();
            foreach (var seed in Seeds)
            {
                var grown = CozyDioramaStageC3IStarterSettlementVerifier.GrowSilently(
                    CozyDioramaStageC3IStarterSettlementVerifier.StarterSeeded(seed), Sessions);
                grownBySeed[seed] = grown;
                var shape = Measure(grown);
                // Acceptance per seed.
                var ok = shape.Components == 1 && shape.Isolated == 0 && shape.In2x2 == 0 &&
                         shape.Cells >= 12 && shape.StarterHutKept && shape.StarterHutAttached;
                allOk &= ok;
                sum.Cells += shape.Cells; sum.In2x2 += shape.In2x2; sum.Endpoints += shape.Endpoints;
                sum.Tees += shape.Tees; sum.Crosses += shape.Crosses; sum.Bends += shape.Bends;
                sum.Straights += shape.Straights; sum.GapPairs += shape.GapPairs; sum.CycleRank += shape.CycleRank;
                sum.Isolated += shape.Isolated; sum.Components += shape.Components;

                report.AppendLine($"seed {seed}: {(ok ? "ok" : "FAIL")}  roadCells={shape.Cells} components={shape.Components} isolated={shape.Isolated} " +
                                  $"cellsIn2x2={shape.In2x2} endpoints={shape.Endpoints} T={shape.Tees} X={shape.Crosses} bends={shape.Bends} straights={shape.Straights} " +
                                  $"parallelGapPairs={shape.GapPairs} cycles={shape.CycleRank} starterHutKept={shape.StarterHutKept} starterHutAttached={shape.StarterHutAttached}");
                report.AppendLine(AsciiMap(grown));
            }

            report.AppendLine($"TOTAL over {Seeds.Length} seeds: roadCells={sum.Cells} components(sum)={sum.Components} isolated={sum.Isolated} cellsIn2x2={sum.In2x2} " +
                              $"endpoints={sum.Endpoints} T={sum.Tees} X={sum.Crosses} bends={sum.Bends} straights={sum.Straights} parallelGapPairs={sum.GapPairs} cycles={sum.CycleRank}");
            report.AppendLine($"share of road cells inside 2x2 squares: {(sum.Cells > 0 ? 100.0 * sum.In2x2 / sum.Cells : 0):0.0}%");
            report.AppendLine();
            report.AppendLine($"ACCEPTANCE (1 component, no isolated, no 2x2 fill, growth alive >=12 cells, starter intact) [{tag}]: " + (allOk ? "PASS" : "FAIL"));

            ShootScene(report, grownBySeed, tag);

            File.WriteAllText(Path.Combine(OutDir, $"c3j-growth-shape-{tag}-report.txt"), report.ToString(), new UTF8Encoding(false));
            return allOk;
        }

        private static Shape Measure(GameSaveData save)
        {
            Func<int, int, long> V = CozyDioramaStageC3IStarterSettlementVerifier.Key;
            var roads = CozyDioramaStageC3IStarterSettlementVerifier.RoadSet(save);
            var s = new Shape
            {
                Cells = roads.Count,
                Components = CozyDioramaStageC3IStarterSettlementVerifier.Components(roads),
                In2x2 = CozyDioramaStageC3IStarterSettlementVerifier.CountCellsInRoadSquares(roads)
            };

            var edges = 0;
            foreach (var k in roads)
            {
                var x = (int)(k >> 32);
                var z = (int)(uint)(k & 0xFFFFFFFFL);
                bool e = roads.Contains(V(x + 1, z)), w = roads.Contains(V(x - 1, z));
                bool n = roads.Contains(V(x, z + 1)), so = roads.Contains(V(x, z - 1));
                var deg = (e ? 1 : 0) + (w ? 1 : 0) + (n ? 1 : 0) + (so ? 1 : 0);
                if (deg == 0) s.Isolated++;
                else if (deg == 1) s.Endpoints++;
                else if (deg == 2) { if ((e && w) || (n && so)) s.Straights++; else s.Bends++; }
                else if (deg == 3) s.Tees++;
                else s.Crosses++;

                if (e) edges++;
                if (n) edges++;
                // Parallel lanes with exactly one empty cell between them.
                if (!e && roads.Contains(V(x + 2, z))) s.GapPairs++;
                if (!n && roads.Contains(V(x, z + 2))) s.GapPairs++;
            }

            s.CycleRank = edges - s.Cells + s.Components;
            var hutCell = new Vector2Int(7, 6);
            s.StarterHutKept = SettlementSimulation.SampleSettlementStage(save, hutCell.x, hutCell.y) == (byte)SettlementStage.Hut;
            s.StarterHutAttached = CozyDioramaStageC3IStarterSettlementVerifier.Degree(roads, V(hutCell.x, hutCell.y)) > 0;
            return s;
        }

        private static string AsciiMap(GameSaveData save)
        {
            var roads = CozyDioramaStageC3IStarterSettlementVerifier.RoadSet(save);
            var huts = CozyDioramaStageC3IStarterSettlementVerifier.HutSet(save);
            var all = new List<long>(roads);
            all.AddRange(huts);
            if (all.Count == 0) return "   (empty)";
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (var k in all)
            {
                var x = (int)(k >> 32);
                var z = (int)(uint)(k & 0xFFFFFFFFL);
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"   map x {minX - 1}..{maxX + 1}, z {minZ - 1}..{maxZ + 1} (top = high z): R=road H=hut");
            for (var z = maxZ + 1; z >= minZ - 1; z--)
            {
                sb.Append("   ");
                for (var x = minX - 1; x <= maxX + 1; x++)
                {
                    var k = CozyDioramaStageC3IStarterSettlementVerifier.Key(x, z);
                    sb.Append(roads.Contains(k) ? 'R' : huts.Contains(k) ? 'H' : '.');
                }

                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private static void ShootScene(StringBuilder report, Dictionary<int, GameSaveData> grownBySeed, string tag)
        {
            EditorSceneManager.OpenScene(MainIslandScenePath, OpenSceneMode.Single);
            var builder = UnityEngine.Object.FindAnyObjectByType<IslandPrototypeBuilder>();
            if (builder != null)
            {
                typeof(IslandPrototypeBuilder).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(builder, null);
            }

            var bootstrap = UnityEngine.Object.FindAnyObjectByType<GameBootstrap>();
            var chunkManager = UnityEngine.Object.FindAnyObjectByType<ChunkManager>();
            var island = UnityEngine.Object.FindAnyObjectByType<IslandMeshVisualizer>();
            var home = UnityEngine.Object.FindAnyObjectByType<HomeSettlementVisualizer>();
            if (bootstrap != null)
            {
                chunkManager ??= bootstrap.GetComponent<ChunkManager>() ?? bootstrap.gameObject.AddComponent<ChunkManager>();
                island ??= bootstrap.GetComponent<IslandMeshVisualizer>() ?? bootstrap.gameObject.AddComponent<IslandMeshVisualizer>();
                home ??= bootstrap.GetComponent<HomeSettlementVisualizer>() ?? bootstrap.gameObject.AddComponent<HomeSettlementVisualizer>();
            }

            if (island == null || home == null)
            {
                report.AppendLine("screenshots skipped: visualizers missing");
                return;
            }

            island.Configure(null, chunkManager);
            home.Configure(null, chunkManager);
            var cam = CozyDioramaStageC3IStarterSettlementVerifier.CreateCamera();
            foreach (var seed in new[] { 101, 104, 106 })
            {
                if (!grownBySeed.TryGetValue(seed, out var save)) continue;
                island.RebuildFromSave(save);
                home.UseContinuousPathNetworkRenderer = true;
                home.RebuildFromSave(save);
                var plan = home.LastContinuousPathPlan;
                var build = home.ContinuousPathRenderer != null ? home.ContinuousPathRenderer.LastBuildReport : null;
                report.AppendLine($"scene ON seed {seed}: components={plan?.ConnectedComponents} hutsConnected={CozyDioramaStageC3IStarterSettlementVerifier.CountConnectedHuts(plan)}/{plan?.HutReports.Count} foldbacks={build?.FoldbackTriangles} downwardTris={build?.DownwardTriangles}");
                CozyDioramaStageC3IStarterSettlementVerifier.Shot(cam, new Vector3(5.5f, 8.5f, -7.5f), new Vector3(-0.8f, 1.8f, 0.8f), 42f, $"../StageC3J-Shots/c3j_{tag}_seed{seed}_ON.png");
            }

            home.UseContinuousPathNetworkRenderer = false;
            UnityEngine.Object.DestroyImmediate(cam.gameObject);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }
}
