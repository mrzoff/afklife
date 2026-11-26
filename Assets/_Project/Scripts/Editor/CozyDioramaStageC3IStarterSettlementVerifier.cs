using System;
using System.Collections.Generic;
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

namespace AfkIsland.Editor
{
    /// <summary>
    /// Stage C-3I verification of the authored MainIsland starter settlement (StarterSettlementSeed).
    /// Everything runs on in-memory Save v8 copies. MainIsland.unity is opened and never saved; the real save
    /// file is only read. Writes Docs/CozyDiorama/StageC3I-Shots/c3i-verification-report.txt and screenshots.
    /// </summary>
    public static class CozyDioramaStageC3IStarterSettlementVerifier
    {
        private const string MainIslandScenePath = "Assets/_Project/Scenes/MainIsland.unity";
        private const string ShotsDir = "Docs/CozyDiorama/StageC3I-Shots";
        private const string ReportPath = "Docs/CozyDiorama/StageC3I-Shots/c3i-verification-report.txt";
        private const int Grid = ChunkMaskCodec.GridSize;

        // Must match StarterSettlementSeed (home chunk 0,0).
        private static readonly Vector2Int HutCell = new Vector2Int(7, 6);
        private static readonly Vector2Int[] PathCells = { new Vector2Int(7, 7), new Vector2Int(7, 8), new Vector2Int(7, 9) };
        private static readonly Vector2Int[] PadCells =
            { new Vector2Int(6, 5), new Vector2Int(7, 5), new Vector2Int(6, 6), new Vector2Int(6, 7) };

        private static readonly string[] ProtectedScenes =
        {
            "Assets/_Project/Scenes/MainIsland.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageB2.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC2.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3C.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3D.unity",
            "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3E.unity"
        };

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3I/Run Starter Settlement Verification")]
        public static void RunMenu()
        {
            var ok = Run();
            Debug.Log("[C-3I] verification " + (ok ? "PASS" : "FAIL") + " report=" + ReportPath);
        }

        public static void RunBatchVerification()
        {
            bool ok;
            try
            {
                ok = Run();
            }
            catch (Exception ex)
            {
                Debug.LogError("[C-3I] FAILED with exception: " + ex);
                ok = false;
            }

            Debug.Log("[C-3I] batch verification finished: ok=" + ok);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool Run()
        {
            var inv = CultureInfo.InvariantCulture;
            var report = new StringBuilder();
            Directory.CreateDirectory(ShotsDir);

            var protectedBefore = new Dictionary<string, string>();
            foreach (var p in ProtectedScenes) protectedBefore[p] = FileSha256Short(p);
            var realSavePath = Path.Combine(Application.persistentDataPath, "afk_island_save.json");
            var realSaveExists = File.Exists(realSavePath);
            var realShaBefore = realSaveExists ? FileSha256Short(realSavePath) : "MISSING";

            report.AppendLine("Stage C-3I — MainIsland starter settlement verification");
            report.AppendLine("Mode: in-memory Save v8 copies; MainIsland.unity opened read-only; real save file only read.");
            report.AppendLine($"Real save: {realSavePath} exists={realSaveExists} diskSha={realShaBefore}");
            report.AppendLine();

            // ---------------- Part A: save-level checks ----------------
            var fresh = CreateFreshLikeSave();
            var seededFresh = StarterSettlementSeed.SeedIfEmpty(fresh);
            var layout = DescribeLayout(fresh);
            var a1 = seededFresh && layout.HutOk && layout.PathOk && layout.PadOk && layout.Roads == 3 && layout.Components == 1 && layout.HutTouchesRoad;
            report.AppendLine($"A1 fresh save is seeded exactly once with hut/pad/road: ok={a1}");
            report.AppendLine($"   seeded={seededFresh}, hutCellIsHut={layout.HutOk}, pathCells={layout.PathOk}, padCells={layout.PadOk}, roadCells={layout.Roads}, roadComponents={layout.Components}, hutTouchesRoad={layout.HutTouchesRoad}");

            var shaBefore = MaskSha(fresh);
            var seededAgain = StarterSettlementSeed.SeedIfEmpty(fresh);
            var a2 = !seededAgain && MaskSha(fresh) == shaBefore;
            report.AppendLine($"A2 second seeding is a no-op (idempotent): ok={a2} (secondCallSeeded={seededAgain}, maskUnchanged={MaskSha(fresh) == shaBefore})");

            var a3 = true;
            var partialDetails = new StringBuilder();
            foreach (var partial in new[]
                     {
                         (name: "single Clearing far from starter", x: 2, z: 2, stage: SettlementStage.Clearing),
                         (name: "single Path only", x: 12, z: 12, stage: SettlementStage.Path),
                         (name: "single Hut only", x: 3, z: 12, stage: SettlementStage.Hut)
                     })
            {
                var partialSave = CreateFreshLikeSave();
                var home = FindChunk(partialSave, 0, 0);
                var mask = new byte[ChunkMaskCodec.CellCount];
                mask[ChunkMaskCodec.CellIndex(partial.x, partial.z)] = (byte)partial.stage;
                home.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(mask);
                var before = home.settlementMaskBase64;
                var seeded = StarterSettlementSeed.SeedIfEmpty(partialSave);
                var unchanged = home.settlementMaskBase64 == before;
                a3 &= !seeded && unchanged;
                partialDetails.Append($"[{partial.name}: seeded={seeded}, unchanged={unchanged}] ");
            }

            report.AppendLine($"A3 any existing settlement cell blocks seeding (no partial/duplicate starter): ok={a3}");
            report.AppendLine("   " + partialDetails.ToString().TrimEnd());

            // Seeded cells never fall inside the pond scrub zone: Apply must not erase the starter.
            var scrubProbe = SaveClone.DeepClone(fresh);
            var scrubbed = WaterSourceLayout.ScrubSettlementInPond(scrubProbe);
            var a4 = scrubbed == 0;
            report.AppendLine($"A4 starter is outside the pond scrub zone: ok={a4} (cellsScrubbed={scrubbed})");

            // Growth after seeding on the fresh-like save, 12 x 7d.
            var growthFresh = RunGrowth(report, "A5 growth from starter (fresh-like save)", fresh, 12, out var a5);

            // Real save copy (read-only).
            GameSaveData realCopy = null;
            var a6 = true;
            var a7 = true;
            if (realSaveExists)
            {
                var json = File.ReadAllText(realSavePath, Encoding.UTF8);
                realCopy = SaveMigrationService.MigrateIfNeeded(JsonUtility.FromJson<GameSaveData>(json));
                SaveMigrationService.EnsureAllSettlementMasks(realCopy);
                SettlementSimulation.CountStages(realCopy, out var cr, out var pr, out var sr, out var hr);
                var hadSettlement = StarterSettlementSeed.HasAnySettlement(realCopy);
                var seededReal = StarterSettlementSeed.SeedIfEmpty(realCopy);
                var realLayout = DescribeLayout(realCopy);
                a6 = seededReal == !hadSettlement &&
                     (!seededReal || (realLayout.HutOk && realLayout.PathOk && realLayout.Components == 1 && realLayout.HutTouchesRoad));
                report.AppendLine($"A6 real-save copy: ok={a6}");
                report.AppendLine($"   before: clearings={cr} paths={pr} stones={sr} huts={hr}; hadSettlement={hadSettlement}; seeded={seededReal}; after: roadCells={realLayout.Roads} components={realLayout.Components} hutTouchesRoad={realLayout.HutTouchesRoad}");
                RunGrowth(report, "A7 growth from starter (real-save copy)", realCopy, 12, out a7);
            }
            else
            {
                report.AppendLine("A6/A7 skipped: no real save on disk.");
            }

            // ---------------- Part B: MainIsland scene, both renderer modes ----------------
            report.AppendLine();
            var bOk = RunSceneChecks(report, inv, seededFreshCopy: fresh, grownFresh: growthFresh, realCopy: realCopy);

            // ---------------- Disk integrity ----------------
            var protectedOk = true;
            report.AppendLine();
            report.AppendLine("C Disk integrity:");
            foreach (var p in ProtectedScenes)
            {
                var after = FileSha256Short(p);
                var same = string.Equals(protectedBefore[p], after, StringComparison.Ordinal);
                protectedOk &= same;
                report.AppendLine($"   {p}: unchanged={same}");
            }

            var realShaAfter = realSaveExists && File.Exists(realSavePath) ? FileSha256Short(realSavePath) : "MISSING";
            var realSame = realShaBefore == realShaAfter;
            protectedOk &= realSame;
            report.AppendLine($"   real save: before={realShaBefore} after={realShaAfter} unchanged={realSame}");

            var all = a1 && a2 && a3 && a4 && a5 && a6 && a7 && bOk && protectedOk;
            report.AppendLine();
            report.AppendLine("OVERALL STAGE C-3I VERIFICATION: " + (all ? "PASS" : "FAIL"));
            File.WriteAllText(ReportPath, report.ToString(), new UTF8Encoding(false));
            return all;
        }

        // ---------------- growth ----------------

        private static GameSaveData RunGrowth(StringBuilder report, string title, GameSaveData seeded, int sessions, out bool ok)
        {
            const long sevenDays = 7L * 24 * 3600;
            var working = SaveClone.DeepClone(seeded);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            working.worldGrowth.lastSimulationUtc = now - sevenDays * 2;
            working.worldGrowth.settlementWatermarkUtc = 0;

            ok = true;
            var totalPaths = 0;
            var totalStones = 0;
            var totalHuts = 0;
            var maxComponents = 0;
            var growthStartedSession = -1;
            var starterHutKept = true;
            var starterRoadKept = true;
            var newHutsDisconnected = 0;
            var firstRoads = RoadSet(working);
            var preHuts = HutSet(working);
            var lines = new List<string>();

            for (var s = 1; s <= sessions; s++)
            {
                working = GrowthSession(working, TimeSpan.FromSeconds(sevenDays), out var res);
                totalPaths += res?.pathsAdded ?? 0;
                totalStones += res?.stonesAdded ?? 0;
                totalHuts += res?.hutsAdded ?? 0;
                if (growthStartedSession < 0 && (res?.pathsAdded ?? 0) > 0) growthStartedSession = s;

                var roads = RoadSet(working);
                var components = Components(roads);
                maxComponents = Math.Max(maxComponents, components);
                if (HomeStage(working, HutCell) != (byte)SettlementStage.Hut) starterHutKept = false;

                // Starter road: each starter Path cell must still be a road or a Hut attached to the network.
                foreach (var c in PathCells)
                {
                    var st = HomeStage(working, c);
                    if (st != (byte)SettlementStage.Path && st != (byte)SettlementStage.Stone && st != (byte)SettlementStage.Hut)
                        starterRoadKept = false;
                }

                var disconnected = 0;
                foreach (var h in HutSet(working))
                {
                    if (!preHuts.Contains(h) && Degree(roads, h) == 0) disconnected++;
                }

                newHutsDisconnected += disconnected;
                lines.Add($"   session {s:D2}: +paths={res?.pathsAdded} +stones={res?.stonesAdded} +huts={res?.hutsAdded} +clearings={res?.clearingsAdded} roadCells={roads.Count} components={components} newHutsDisconnected={disconnected}");
            }

            // The starter must be the root of the growth: roads grew and stayed one network.
            ok = totalPaths > 0 && maxComponents <= 1 && starterHutKept && starterRoadKept && newHutsDisconnected == 0;
            report.AppendLine($"{title}: ok={ok}");
            report.AppendLine($"   starting roadCells={firstRoads.Count}, growthStartedAtSession={growthStartedSession}, totalPathsAdded={totalPaths}, totalStonesAdded={totalStones}, totalHutsAdded={totalHuts}, maxRoadComponents={maxComponents}, starterHutKept={starterHutKept}, starterRoadKept={starterRoadKept}, newHutsDisconnected={newHutsDisconnected}");
            foreach (var l in lines) report.AppendLine(l);
            return working;
        }

        private static GameSaveData GrowthSession(GameSaveData source, TimeSpan elapsed, out SettlementSimulationResult settle)
        {
            var copy = SaveClone.DeepClone(source);
            var sim = new WorldGrowthSimulation();
            var simResult = sim.Simulate(copy, elapsed, WorldGrowthConfig.CreateDefault());
            var working = simResult != null && simResult.success && simResult.updatedSave != null ? simResult.updatedSave : copy;
            settle = null;
            if (simResult != null && simResult.success)
            {
                SaveMigrationService.EnsureEcosystemWeatherAnimals(working);
                var rng = new DeterministicRng(unchecked((uint)(working.worldGrowth.growthSeed ^ (int)simResult.appliedSeconds ^ 0xEC05)));
                settle = SettlementSimulation.Apply(working, simResult.appliedSeconds, rng);
                SettlementSimulation.RebalanceExcessStones(working);
                working.worldGrowth.growthSeed = unchecked((int)rng.State);
            }

            return working;
        }

        // ---------------- scene part ----------------

        private static bool RunSceneChecks(StringBuilder report, CultureInfo inv, GameSaveData seededFreshCopy, GameSaveData grownFresh, GameSaveData realCopy)
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
                report.AppendLine("B scene wiring: FAIL (visualizers missing)");
                return false;
            }

            var flagDefaultFalse = !home.UseContinuousPathNetworkRenderer;
            island.Configure(null, chunkManager);
            home.Configure(null, chunkManager);
            var cam = CreateCamera();
            var ok = flagDefaultFalse;
            report.AppendLine($"B scene: flag useContinuousPathNetworkRenderer default false={flagDefaultFalse}");

            ok &= SceneCase(report, inv, home, island, cam, "B1 starter on fresh save", seededFreshCopy, "c3i_01_starter", true);
            if (realCopy != null)
            {
                ok &= SceneCase(report, inv, home, island, cam, "B2 starter on real-save copy", realCopy, "c3i_02_realsave_starter", true);
            }

            ok &= SceneCase(report, inv, home, island, cam, "B3 after 12 x 7d growth (fresh-like)", grownFresh, "c3i_03_grown", false);

            // Seed sweep: the same 12 x 7d growth from the starter with 8 different growth seeds.
            // Components and hut doors are hard requirements; foldback triangles are a renderer metric, reported.
            var sweepOk = true;
            var sweepLines = new StringBuilder();
            var foldbackSeeds = 0;
            var totalFoldbacks = 0;
            var blobCells = 0;
            for (var seed = 101; seed <= 108; seed++)
            {
                var grown = GrowSilently(StarterSeeded(seed), 12);
                home.UseContinuousPathNetworkRenderer = true;
                home.RebuildFromSave(grown);
                var plan = home.LastContinuousPathPlan;
                var build = home.ContinuousPathRenderer != null ? home.ContinuousPathRenderer.LastBuildReport : null;
                var huts = plan != null ? plan.HutReports.Count : 0;
                var connected = CountConnectedHuts(plan);
                var components = plan != null ? plan.ConnectedComponents : -1;
                var fold = build != null ? build.FoldbackTriangles : -1;
                var down = build != null ? build.DownwardTriangles : -1;
                var roads = RoadSet(grown);
                var in2x2 = CountCellsInRoadSquares(roads);
                blobCells += in2x2;
                if (fold > 0) { foldbackSeeds++; totalFoldbacks += fold; }
                var seedOk = components == 1 && connected == huts && huts >= 1 && down == 0;
                sweepOk &= seedOk;
                sweepLines.AppendLine($"   seed {seed}: roadCells={roads.Count}, cellsInside2x2RoadSquares={in2x2}, components={components}, hutsConnected={connected}/{huts}, downwardTris={down}, foldbacks={fold} {(seedOk ? "ok" : "FAIL")}");
            }

            report.AppendLine($"B3s seed sweep (8 seeds, 12 x 7d, renderer ON): ok={sweepOk}; seedsWithFoldbacks={foldbackSeeds}/8, totalFoldbacks={totalFoldbacks}, road cells inside 2x2 squares (sum)={blobCells}");
            report.Append(sweepLines.ToString());
            ok &= sweepOk;

            // OFF -> ON -> OFF zero-leak.
            home.UseContinuousPathNetworkRenderer = true;
            home.RebuildFromSave(grownFresh);
            home.UseContinuousPathNetworkRenderer = false;
            home.RebuildFromSave(grownFresh);
            var cleared = home.LastClearedRendererStats;
            var zeroLeaks = home.ContinuousPathRenderer == null && cleared != null &&
                            cleared.LiveOwnedMeshCount == 0 && cleared.TotalMeshesCreated == cleared.TotalMeshesDestroyed;
            report.AppendLine($"B4 OFF->ON->OFF zero leaks: ok={zeroLeaks} (created={cleared?.TotalMeshesCreated}, destroyed={cleared?.TotalMeshesDestroyed}, live={cleared?.LiveOwnedMeshCount})");
            ok &= zeroLeaks;

            UnityEngine.Object.DestroyImmediate(cam.gameObject);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            return ok;
        }

        private static bool SceneCase(
            StringBuilder report,
            CultureInfo inv,
            HomeSettlementVisualizer home,
            IslandMeshVisualizer island,
            Camera cam,
            string title,
            GameSaveData save,
            string shotPrefix,
            bool starterOnly)
        {
            var shaBefore = SaveSha(save);
            island.RebuildFromSave(save);

            home.UseContinuousPathNetworkRenderer = false;
            home.RebuildFromSave(save);
            var legacyPaths = CountNamedChildren(home.GrownSettlementRoot, "Path_");
            Shot(cam, new Vector3(3.8f, 5.2f, -5.8f), new Vector3(-0.4f, 1.9f, -0.4f), 38f, shotPrefix + "_OFF.png");

            home.UseContinuousPathNetworkRenderer = true;
            home.RebuildFromSave(save);
            var plan = home.LastContinuousPathPlan;
            var renderer = home.ContinuousPathRenderer;
            var build = renderer != null ? renderer.LastBuildReport : null;
            var pond = CountCoreVerticesInsidePond(renderer);
            var legacyOn = CountNamedChildren(home.GrownSettlementRoot, "Path_");
            Shot(cam, new Vector3(3.8f, 5.2f, -5.8f), new Vector3(-0.4f, 1.9f, -0.4f), 38f, shotPrefix + "_ON.png");

            var totalHuts = plan != null ? plan.HutReports.Count : 0;
            var connectedHuts = CountConnectedHuts(plan);
            var statuses = new StringBuilder();
            if (plan != null)
            {
                for (var i = 0; i < plan.HutReports.Count; i++)
                {
                    statuses.Append(plan.HutReports[i].Status).Append(i + 1 < plan.HutReports.Count ? "," : string.Empty);
                }
            }

            var shaAfter = SaveSha(save);
            var topologyOk = plan != null && plan.ConnectedComponents == 1 && connectedHuts == totalHuts &&
                             totalHuts >= 1 && plan.StarterIssues.Count == 0;
            var geometryOk = build != null && build.DownwardTriangles == 0 && build.FoldbackTriangles == 0 && pond == 0 && legacyOn == 0;
            var offOk = legacyPaths > 0;
            var ok = topologyOk && geometryOk && offOk && shaBefore == shaAfter;

            report.AppendLine($"{title}: ok={ok}");
            report.AppendLine($"   renderer OFF: legacyPathTiles={legacyPaths} (expect >0)");
            report.AppendLine($"   renderer ON : components={plan?.ConnectedComponents}, huts connected={connectedHuts}/{totalHuts} [{statuses}], nodes={plan?.Nodes.Count}, edges={plan?.Edges.Count}, starterIssues={plan?.StarterIssues.Count}, pondIntrusions={pond}, legacyPathTiles={legacyOn}, downwardTris={build?.DownwardTriangles}, foldbacks={build?.FoldbackTriangles}");
            report.AppendLine($"   saveImmutable={shaBefore == shaAfter}");
            if (!ok || !starterOnly)
            {
                report.AppendLine("   home chunk map (z top=15 .. 0, x 0..15): R=road H=hut c=clearing .=none");
                for (var z = Grid - 1; z >= 0; z--)
                {
                    var row = new StringBuilder("     ");
                    for (var x = 0; x < Grid; x++)
                    {
                        var st = SettlementSimulation.SampleSettlementStage(save, x, z);
                        row.Append(st == (byte)SettlementStage.Path || st == (byte)SettlementStage.Stone ? 'R'
                            : st == (byte)SettlementStage.Hut ? 'H'
                            : st == (byte)SettlementStage.Clearing ? 'c' : '.');
                    }

                    report.AppendLine(row.ToString());
                }
            }

            if (plan != null && (plan.ConnectedComponents != 1 || connectedHuts != totalHuts))
            {
                report.AppendLine("   NOTE: topology mismatch; hut statuses above show which door is not attached.");
            }

            return ok;
        }

        // ---------------- fixtures / analysis ----------------

        private static GameSaveData CreateFreshLikeSave(int growthSeed = 20260101)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var data = new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                lastSaveUtc = now,
                plants = new List<PlantSaveData>(),
                chunks = new List<ChunkSaveData>
                {
                    new ChunkSaveData
                    {
                        chunkX = 0, chunkZ = 0, isUnlocked = true, biomeId = "starter_island",
                        randomSeed = 0, lastSimulatedUtc = now, plants = new List<PlantSaveData>()
                    }
                },
                worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(now),
                ecosystem = SaveMigrationService.CreateDefaultEcosystem(),
                weather = SaveMigrationService.CreateDefaultWeather(now),
                animals = new List<AnimalSaveData>(),
                habitats = new List<HabitatSaveData>()
            };
            data.worldGrowth.lastSimulationUtc = now;
            data.worldGrowth.growthSeed = growthSeed;
            data.worldGrowth.chunks.Add(SaveMigrationService.CreateGrowthRecord(0, 0, true));
            return data;
        }

        private static GameSaveData StarterSeeded(int growthSeed)
        {
            var s = CreateFreshLikeSave(growthSeed);
            StarterSettlementSeed.SeedIfEmpty(s);
            return s;
        }

        /// <summary>Counts road cells that belong to at least one 2x2 block of road cells (a "plaza" instead of a road).</summary>
        private static int CountCellsInRoadSquares(HashSet<long> roads)
        {
            var inSquare = new HashSet<long>();
            foreach (var k in roads)
            {
                var x = (int)(k >> 32);
                var z = (int)(uint)(k & 0xFFFFFFFFL);
                if (roads.Contains(Key(x + 1, z)) && roads.Contains(Key(x, z + 1)) && roads.Contains(Key(x + 1, z + 1)))
                {
                    inSquare.Add(k);
                    inSquare.Add(Key(x + 1, z));
                    inSquare.Add(Key(x, z + 1));
                    inSquare.Add(Key(x + 1, z + 1));
                }
            }

            return inSquare.Count;
        }

        private static GameSaveData GrowSilently(GameSaveData seeded, int sessions)
        {
            const long sevenDays = 7L * 24 * 3600;
            var working = SaveClone.DeepClone(seeded);
            working.worldGrowth.lastSimulationUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sevenDays * 2;
            working.worldGrowth.settlementWatermarkUtc = 0;
            for (var s = 0; s < sessions; s++)
            {
                working = GrowthSession(working, TimeSpan.FromSeconds(sevenDays), out _);
            }

            return working;
        }

        private sealed class Layout
        {
            public bool HutOk, PathOk, PadOk, HutTouchesRoad;
            public int Roads, Components;
        }

        private static Layout DescribeLayout(GameSaveData save)
        {
            var l = new Layout
            {
                HutOk = HomeStage(save, HutCell) == (byte)SettlementStage.Hut,
                PathOk = true,
                PadOk = true
            };
            foreach (var c in PathCells) if (HomeStage(save, c) != (byte)SettlementStage.Path) l.PathOk = false;
            foreach (var c in PadCells) if (HomeStage(save, c) != (byte)SettlementStage.Clearing) l.PadOk = false;
            var roads = RoadSet(save);
            l.Roads = roads.Count;
            l.Components = Components(roads);
            l.HutTouchesRoad = Degree(roads, Key(HutCell.x, HutCell.y)) > 0;
            return l;
        }

        private static ChunkGrowthSaveData FindChunk(GameSaveData save, int cx, int cz)
        {
            foreach (var g in save.worldGrowth.chunks) if (g != null && g.chunkX == cx && g.chunkZ == cz) return g;
            return null;
        }

        private static byte HomeStage(GameSaveData save, Vector2Int cell) =>
            SettlementSimulation.SampleSettlementStage(save, cell.x, cell.y);

        private static long Key(int wx, int wz) => ((long)wx << 32) | (long)(uint)wz;

        private static HashSet<long> RoadSet(GameSaveData save) => StageSet(save, true);

        private static HashSet<long> HutSet(GameSaveData save) => StageSet(save, false);

        private static HashSet<long> StageSet(GameSaveData save, bool roads)
        {
            var set = new HashSet<long>();
            if (save?.worldGrowth?.chunks == null) return set;
            foreach (var g in save.worldGrowth.chunks)
            {
                if (g == null || !ChunkMaskCodec.TryDecodeBase64(g.settlementMaskBase64, out var settle, out _)) continue;
                for (var z = 0; z < Grid; z++)
                {
                    for (var x = 0; x < Grid; x++)
                    {
                        var st = settle[ChunkMaskCodec.CellIndex(x, z)];
                        var match = roads
                            ? st == (byte)SettlementStage.Path || st == (byte)SettlementStage.Stone
                            : st == (byte)SettlementStage.Hut;
                        if (match) set.Add(Key(g.chunkX * Grid + x, g.chunkZ * Grid + z));
                    }
                }
            }

            return set;
        }

        private static int Degree(HashSet<long> roads, long k)
        {
            var x = (int)(k >> 32);
            var z = (int)(uint)(k & 0xFFFFFFFFL);
            var d = 0;
            if (roads.Contains(Key(x + 1, z))) d++;
            if (roads.Contains(Key(x - 1, z))) d++;
            if (roads.Contains(Key(x, z + 1))) d++;
            if (roads.Contains(Key(x, z - 1))) d++;
            return d;
        }

        private static int Components(HashSet<long> set)
        {
            var visited = new HashSet<long>();
            var stack = new Stack<long>();
            var count = 0;
            foreach (var start in set)
            {
                if (!visited.Add(start)) continue;
                count++;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    var x = (int)(cur >> 32);
                    var z = (int)(uint)(cur & 0xFFFFFFFFL);
                    foreach (var nb in new[] { Key(x + 1, z), Key(x - 1, z), Key(x, z + 1), Key(x, z - 1) })
                    {
                        if (set.Contains(nb) && visited.Add(nb)) stack.Push(nb);
                    }
                }
            }

            return count;
        }

        private static int CountConnectedHuts(SettlementPathTopology.TopologyPlan plan)
        {
            if (plan == null) return 0;
            var count = 0;
            for (var i = 0; i < plan.HutReports.Count; i++)
            {
                var st = plan.HutReports[i].Status;
                if (st == SettlementPathTopology.HutConnectionStatus.Connected ||
                    st == SettlementPathTopology.HutConnectionStatus.ConnectedWithVisualDoorCorrection) count++;
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

        private static int CountCoreVerticesInsidePond(SettlementPathNetworkRenderer renderer)
        {
            if (renderer == null || renderer.LastBuildReport == null) return 0;
            var pond = WaterSourceLayout.GetWorldCenter();
            var inside = 0;
            foreach (var kv in renderer.LastBuildReport.Chunks)
            {
                if (!renderer.TryGetChunkMeshes(kv.Key, out var coreMesh, out _) || coreMesh == null) continue;
                var origin = GrowthWorldPlacement.ChunkOriginXZ(kv.Key.x, kv.Key.y);
                var verts = coreMesh.vertices;
                for (var i = 0; i < verts.Length; i++)
                {
                    var dx = (origin.x + verts[i].x - pond.x) / WaterSourceLayout.WaterHalfExtentX;
                    var dz = (origin.y + verts[i].z - pond.z) / WaterSourceLayout.WaterHalfExtentZ;
                    if (dx * dx + dz * dz < 0.96f) inside++;
                }
            }

            return inside;
        }

        private static Camera CreateCamera()
        {
            var go = new GameObject("StageC3I_VerifyCamera");
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.36f, 0.68f, 0.84f);
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 220f;
            return cam;
        }

        private static void Shot(Camera cam, Vector3 pos, Vector3 target, float fov, string fileName)
        {
            const int width = 1280;
            const int height = 720;
            cam.transform.position = pos;
            cam.transform.LookAt(target);
            cam.fieldOfView = fov;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
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
                File.WriteAllBytes(Path.Combine(ShotsDir, fileName), tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = prevActive;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private static string MaskSha(GameSaveData save)
        {
            var home = FindChunk(save, 0, 0);
            return Sha(home != null ? home.settlementMaskBase64 : string.Empty);
        }

        private static string SaveSha(GameSaveData save) => Sha(JsonUtility.ToJson(save, false));

        private static string Sha(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
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
    }
}
