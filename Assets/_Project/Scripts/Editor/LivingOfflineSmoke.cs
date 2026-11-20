#if UNITY_EDITOR
using System;
using System.IO;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Batchable checks for v0.6.1 Living Offline polish (cap, catch-up, fauna, bak).
    /// </summary>
    public static class LivingOfflineSmoke
    {
        [MenuItem("AFK Island/Smoke Living Offline 0.6.1")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Living Offline smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Living Offline smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            try
            {
                var config = WorldGrowthConfig.CreateDefault();
                if (config.maxOfflineSeconds != 7L * 24L * 3600L)
                {
                    Debug.LogError($"Expected maxOfflineSeconds=7d, got {config.maxOfflineSeconds}");
                    return 2;
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var save = new GameSaveData
                {
                    version = SaveMigrationService.CurrentVersion,
                    lastSaveUtc = now - 8L * 24L * 3600L,
                    plants = new System.Collections.Generic.List<PlantSaveData>(),
                    chunks = new System.Collections.Generic.List<ChunkSaveData>
                    {
                        new ChunkSaveData
                        {
                            chunkX = 0,
                            chunkZ = 0,
                            isUnlocked = true,
                            biomeId = "starter_island",
                            plants = new System.Collections.Generic.List<PlantSaveData>()
                        }
                    },
                    worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(now - 8L * 24L * 3600L),
                    ecosystem = SaveMigrationService.CreateDefaultEcosystem(),
                    weather = SaveMigrationService.CreateDefaultWeather(now - 8L * 24L * 3600L),
                    animals = new System.Collections.Generic.List<AnimalSaveData>(),
                    habitats = new System.Collections.Generic.List<HabitatSaveData>()
                };
                save.worldGrowth.lastSimulationUtc = now - 8L * 24L * 3600L;
                save.worldGrowth.chunks.Add(SaveMigrationService.CreateGrowthRecord(0, 0, true));

                var sim = new WorldGrowthSimulation();
                var elapsed = TimeSpan.FromDays(8);
                var world = sim.Simulate(save, elapsed, config);
                if (!world.success)
                {
                    Debug.LogError("World sim failed: " + world.errorMessage);
                    return 3;
                }

                if (world.appliedSeconds != config.maxOfflineSeconds)
                {
                    Debug.LogError(
                        $"Expected applied={config.maxOfflineSeconds}, got {world.appliedSeconds}");
                    return 4;
                }

                var catchUp = (long)elapsed.TotalSeconds - world.appliedSeconds;
                if (catchUp < 23L * 3600L)
                {
                    Debug.LogError($"Expected ~1d catch-up after 8d AFK, got {catchUp}");
                    return 5;
                }

                var report = ReturnReportData.FromSession(
                    (long)elapsed.TotalSeconds, world, world.updatedSave, 0, 0, 0, string.Empty);
                if (report.catchUpRemainingSeconds != catchUp)
                {
                    Debug.LogError("ReturnReport catchUp mismatch");
                    return 6;
                }

                var maxNew8h = FaunaSimulation.ResolveMaxNewPerSession(8L * 3600L);
                var maxNew7d = FaunaSimulation.ResolveMaxNewPerSession(7L * 24L * 3600L);
                if (maxNew8h != 12 || maxNew7d != 20)
                {
                    Debug.LogError($"Fauna session caps unexpected: 8h={maxNew8h}, 7d={maxNew7d}");
                    return 7;
                }

                var maxAnimals = FaunaSimulation.ResolveMaxAnimals(world.updatedSave);
                if (maxAnimals < 16 || maxAnimals > 48)
                {
                    Debug.LogError($"Fauna island cap unexpected: {maxAnimals}");
                    return 8;
                }

                var plantCap7d = PlantColonizationSimulation.ResolveMaxNewPerSession(7L * 24L * 3600L);
                if (plantCap7d != PlantColonizationSimulation.AbsoluteMaxNewPerSession)
                {
                    Debug.LogError($"Plant colonization 7d cap unexpected: {plantCap7d}");
                    return 30;
                }

                var colonizeWorking = world.updatedSave;
                var seedBase = unchecked((uint)(colonizeWorking.worldGrowth.growthSeed ^ 0xC01A));
                var cloneA = SaveClone.DeepClone(colonizeWorking);
                var cloneB = SaveClone.DeepClone(colonizeWorking);

                var rngA = new DeterministicRng(seedBase);
                var weatherA = WeatherSimulation.Advance(cloneA, world.appliedSeconds, rngA);
                var plantsBefore = CountAllPlants(cloneA);
                var colonize = PlantColonizationSimulation.Apply(
                    cloneA, world.appliedSeconds, rngA, weatherA);
                var plantsAfter = CountAllPlants(cloneA);
                var minExpected = colonize.cellsTargeted >= PlantColonizationSimulation.CellsPerNewPlant * 2
                    ? 2
                    : 1;
                if (colonize.cellsTargeted <= 0 ||
                    colonize.plantsSpawned < minExpected ||
                    plantsAfter < plantsBefore + minExpected)
                {
                    Debug.LogError(
                        $"Plant colonization expected ≥{minExpected} new plants after 7d land: " +
                        $"spawned={colonize.plantsSpawned} before={plantsBefore} after={plantsAfter} " +
                        $"cells={colonize.cellsTargeted}");
                    return 31;
                }

                var areaCap = PlantColonizationSimulation.ResolveMaxNewByLandArea(colonize.cellsTargeted);
                if (colonize.plantsSpawned > areaCap)
                {
                    Debug.LogError(
                        $"Density cap violated: spawned={colonize.plantsSpawned} areaCap={areaCap}");
                    return 38;
                }

                foreach (var kv in colonize.growthSecondsByInstanceId)
                {
                    if (kv.Value < 0 || kv.Value > world.appliedSeconds)
                    {
                        Debug.LogError(
                            $"Colonized plant growth remaining out of window: {kv.Key}={kv.Value}");
                        return 33;
                    }
                }

                // Replay same interval on already-colonized save → no duplicates.
                var rngReplay = new DeterministicRng(seedBase);
                WeatherSimulation.Advance(cloneA, world.appliedSeconds, rngReplay);
                var colonizeReplay = PlantColonizationSimulation.Apply(
                    cloneA, world.appliedSeconds, rngReplay, weatherA);
                if (colonizeReplay.plantsSpawned != 0)
                {
                    Debug.LogError(
                        $"Deterministic occupancy failed: replay spawned {colonizeReplay.plantsSpawned}");
                    return 34;
                }

                // Same seed on fresh clone → identical plotIds.
                var rngB = new DeterministicRng(seedBase);
                var weatherB = WeatherSimulation.Advance(cloneB, world.appliedSeconds, rngB);
                var colonizeB = PlantColonizationSimulation.Apply(
                    cloneB, world.appliedSeconds, rngB, weatherB);
                if (colonizeB.plantsSpawned != colonize.plantsSpawned)
                {
                    Debug.LogError(
                        $"Determinism failed: A={colonize.plantsSpawned} B={colonizeB.plantsSpawned}");
                    return 35;
                }

                for (var i = 0; i < colonize.spawned.Count; i++)
                {
                    if (colonize.spawned[i].plotId != colonizeB.spawned[i].plotId ||
                        colonize.spawned[i].instanceId != colonizeB.spawned[i].instanceId)
                    {
                        Debug.LogError("Determinism failed: plotId/instanceId mismatch");
                        return 36;
                    }
                }

                // No suitable cells → 0, no invalid spawns.
                var empty = SaveClone.DeepClone(colonizeWorking);
                empty.worldGrowth.chunks.Clear();
                if (empty.chunks != null)
                {
                    foreach (var c in empty.chunks)
                    {
                        c?.plants?.Clear();
                    }
                }

                empty.plants?.Clear();
                var emptyResult = PlantColonizationSimulation.Apply(
                    empty, world.appliedSeconds, new DeterministicRng(seedBase), null);
                if (emptyResult.plantsSpawned != 0 || emptyResult.cellsTargeted != 0)
                {
                    Debug.LogError(
                        $"Expected 0 colonization without Moss+ cells, got " +
                        $"spawned={emptyResult.plantsSpawned} cells={emptyResult.cellsTargeted}");
                    return 37;
                }

                var natureReport = ReturnReportData.FromSession(
                    (long)elapsed.TotalSeconds,
                    world,
                    cloneA,
                    0,
                    0,
                    plantsAfter,
                    string.Empty,
                    null,
                    null,
                    0,
                    colonize.plantsSpawned,
                    colonize.ToReportLine());
                if (natureReport.plantsSpawnedByNature != colonize.plantsSpawned)
                {
                    Debug.LogError("ReturnReport nature spawn count mismatch");
                    return 32;
                }

                var saveSmoke = RunSaveRecoverySmoke();
                if (saveSmoke != 0)
                {
                    return saveSmoke;
                }

                Debug.Log(
                    $"[AFK Island] Smoke OK: applied={world.appliedSeconds}s catchUp={catchUp}s " +
                    $"faunaNew8h={maxNew8h} faunaNew7d={maxNew7d} maxAnimals={maxAnimals} " +
                    $"natureSpawned={colonize.plantsSpawned} plantsTotal={plantsAfter}");
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] Smoke exception: " + e);
                return 1;
            }
        }

        /// <summary>
        /// File-contract cases for SaveService bak recovery (no scene / no format change).
        /// </summary>
        private static int RunSaveRecoverySmoke()
        {
            var dir = Path.Combine(Application.temporaryCachePath, "afk_smoke_save_recovery");
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            Directory.CreateDirectory(dir);
            var main = Path.Combine(dir, "afk_island_save.json");
            var bak = main + ".bak";
            var tmp = main + ".tmp";

            var goodV1 = "{\"version\":7,\"lastSaveUtc\":100,\"plants\":[],\"chunks\":[],\"animals\":[],\"habitats\":[]}";
            var goodV2 = "{\"version\":7,\"lastSaveUtc\":200,\"plants\":[],\"chunks\":[],\"animals\":[],\"habitats\":[]}";

            // 1) Normal write rotation keeps .bak
            File.WriteAllText(main, goodV1);
            File.WriteAllText(tmp, goodV2);
            File.Replace(tmp, main, bak);
            if (!File.Exists(bak) || !SaveService.IsLikelyReadableSaveFile(bak))
            {
                Debug.LogError("Save smoke: .bak missing/unreadable after normal File.Replace");
                return 9;
            }

            // 2) Corrupt primary + good bak → restore primary, bak unchanged
            File.WriteAllText(main, "{not-json");
            File.WriteAllText(bak, goodV1);
            if (!SaveService.TryRestorePrimaryFromVerifiedBackup(main, bak))
            {
                Debug.LogError("Save smoke: restore from .bak failed");
                return 10;
            }

            if (!SaveService.IsLikelyReadableSaveFile(main))
            {
                Debug.LogError("Save smoke: primary still unreadable after restore");
                return 11;
            }

            var bakAfter = File.ReadAllText(bak);
            if (bakAfter != goodV1)
            {
                Debug.LogError("Save smoke: .bak changed during primary restore");
                return 12;
            }

            // 3) Empty primary rejected by readability probe
            File.WriteAllText(main, "   ");
            if (SaveService.IsLikelyReadableSaveFile(main))
            {
                Debug.LogError("Save smoke: empty primary should be unreadable");
                return 13;
            }

            // 4) Corrupt bak rejected
            File.WriteAllText(bak, "%%%%");
            if (SaveService.IsLikelyReadableSaveFile(bak))
            {
                Debug.LogError("Save smoke: corrupt .bak should be unreadable");
                return 14;
            }

            // 5) Unsupported newer version must not silently replace via bak in TryReadPath semantics:
            //    probe still "reads" JSON, but SaveService.TryRead blocks writes — covered by version gate.
            var newer = "{\"version\":999,\"lastSaveUtc\":1,\"plants\":[],\"chunks\":[],\"animals\":[],\"habitats\":[]}";
            File.WriteAllText(main, newer);
            if (!SaveService.IsLikelyReadableSaveFile(main))
            {
                Debug.LogError("Save smoke: newer-version JSON should still parse as readable file");
                return 15;
            }

            // 6) Android copy strategy: first write (no primary)
            TryDelete(main);
            TryDelete(bak);
            TryDelete(tmp);
            File.WriteAllText(tmp, goodV1);
            if (!SaveService.TryCommitSaveFiles(main, tmp, bak, forceCopyStrategy: true, out var err) ||
                !SaveService.IsLikelyReadableSaveFile(main))
            {
                Debug.LogError("Save smoke: android first write failed: " + err);
                return 16;
            }

            // 7) Android copy: rewrite good primary → bak refreshed, primary becomes v2
            File.WriteAllText(tmp, goodV2);
            if (!SaveService.TryCommitSaveFiles(main, tmp, bak, forceCopyStrategy: true, out err))
            {
                Debug.LogError("Save smoke: android rewrite failed: " + err);
                return 17;
            }

            if (File.ReadAllText(bak) != goodV1 || File.ReadAllText(main) != goodV2)
            {
                Debug.LogError("Save smoke: android rewrite bak/primary mismatch");
                return 18;
            }

            // 8) Corrupt/empty primary + good bak: do not clobber bak; commit new temp
            File.WriteAllText(main, "");
            File.WriteAllText(bak, goodV1);
            File.WriteAllText(tmp, goodV2);
            if (!SaveService.TryCommitSaveFiles(main, tmp, bak, forceCopyStrategy: true, out err))
            {
                Debug.LogError("Save smoke: android corrupt-primary write failed: " + err);
                return 19;
            }

            if (File.ReadAllText(bak) != goodV1)
            {
                Debug.LogError("Save smoke: corrupt primary must not overwrite .bak");
                return 20;
            }

            if (!SaveService.IsLikelyReadableSaveFile(main) || File.ReadAllText(main) != goodV2)
            {
                Debug.LogError("Save smoke: primary should be goodV2 after android commit");
                return 21;
            }

            // 8b) Rapid sequential android commits (autosave pressure)
            for (var i = 0; i < 5; i++)
            {
                var payload =
                    "{\"version\":7,\"lastSaveUtc\":" + (300 + i) +
                    ",\"plants\":[],\"chunks\":[],\"animals\":[],\"habitats\":[]}";
                File.WriteAllText(tmp, payload);
                if (!SaveService.TryCommitSaveFiles(main, tmp, bak, forceCopyStrategy: true, out err))
                {
                    Debug.LogError($"Save smoke: rapid android commit #{i} failed: {err}");
                    return 23;
                }
            }

            // 9) Desktop Replace path still works in Editor
            File.WriteAllText(main, goodV1);
            File.WriteAllText(tmp, goodV2);
            if (!SaveService.TryCommitSaveFiles(main, tmp, bak, forceCopyStrategy: false, out err))
            {
                Debug.LogError("Save smoke: desktop Replace commit failed: " + err);
                return 22;
            }

            Debug.Log("[AFK Island] Save recovery smoke OK (android-copy + replace + bak guards).");
            return 0;
        }

        private static int CountAllPlants(GameSaveData save)
        {
            var n = 0;
            if (save?.chunks != null)
            {
                foreach (var c in save.chunks)
                {
                    if (c?.plants != null)
                    {
                        n += c.plants.Count;
                    }
                }
            }

            if (n == 0 && save?.plants != null)
            {
                n = save.plants.Count;
            }

            return n;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
#endif
