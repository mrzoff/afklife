using System;
using AfkIsland.Offline;
using AfkIsland.Plants;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Coordinates read → migrate → world simulate → plant offline → single commit.
    /// </summary>
    public sealed class WorldGrowthService : MonoBehaviour
    {
        [SerializeField] private SaveService saveService;
        [SerializeField] private ChunkManager chunkManager;
        [SerializeField] private OfflineGrowthService offlineGrowth;
        [SerializeField] private PlantingSystem plantingSystem;
        [SerializeField] private WorldGrowthConfig config = new();

        private readonly WorldGrowthSimulation _simulation = new();
        private bool appliedThisSession;

        public string LastCombinedReport { get; private set; } = "Новая игра.";
        public GrowthSimulationResult LastWorldResult { get; private set; }
        public ReturnReportData LastReturnReport { get; private set; }
        public FaunaSimulationResult LastFaunaResult { get; private set; }
        public LivingWorldResult LastLivingResult { get; private set; }

        public void Configure(
            SaveService save,
            ChunkManager chunks,
            OfflineGrowthService offline,
            PlantingSystem planting,
            WorldGrowthConfig growthConfig = null)
        {
            saveService = save;
            chunkManager = chunks;
            offlineGrowth = offline;
            plantingSystem = planting;
            if (growthConfig != null)
            {
                config = growthConfig;
            }
            else if (config == null)
            {
                config = WorldGrowthConfig.CreateDefault();
            }
        }

        public bool ApplyOfflineSession()
        {
            if (appliedThisSession)
            {
                Debug.LogWarning("[AFK Island] Offline session already applied.");
                return true;
            }

            if (saveService == null || chunkManager == null || offlineGrowth == null)
            {
                LastCombinedReport = "Системы мира не готовы.";
                return false;
            }

            saveService.BeginLoad();
            try
            {
                if (!saveService.HasSave())
                {
                    var fresh = CreateFreshSave();
                    if (!saveService.TryWrite(fresh, out var freshError))
                    {
                        LastCombinedReport = $"Не удалось создать новый сейв: {freshError}";
                        return false;
                    }

                    chunkManager.RestoreLogicalChunks(fresh.chunks);
                    LastCombinedReport = "Новая игра. Тапни по зелёной точке посадки.";
                    appliedThisSession = true;
                    offlineGrowth.MarkAppliedExternal(LastCombinedReport);
                    return true;
                }

                if (!saveService.TryRead(out var data) || data == null)
                {
                    LastCombinedReport = "Не удалось прочитать сохранение.";
                    return false;
                }

                chunkManager.RestoreLogicalChunks(data.chunks);
                if (plantingSystem != null)
                {
                    chunkManager.EnsureHomeChunkInScene(plantingSystem.Plots);
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (data.worldGrowth == null)
                {
                    LastCombinedReport = "worldGrowth отсутствует после миграции.";
                    return false;
                }

                if (data.worldGrowth.lastSimulationUtc <= 0)
                {
                    data.worldGrowth.lastSimulationUtc = data.lastSaveUtc > 0 ? data.lastSaveUtc : now;
                }

                // C-3I: a save with no settlement at all gets the authored starter once. Growth rules are
                // unchanged; later roads attach to this real road through the normal connectivity guard.
                if (StarterSettlementSeed.SeedIfEmpty(data))
                {
                    Debug.Log("[AFK Island] C-3I: starter settlement seeded into the home chunk (hut + pad + road).");
                }

                var watermarkBefore = data.worldGrowth.lastSimulationUtc;
                var worldElapsed = Math.Max(0, now - watermarkBefore);
                var worldResult = _simulation.Simulate(data, TimeSpan.FromSeconds(worldElapsed), config);
                LastWorldResult = worldResult;
                if (!worldResult.success || worldResult.updatedSave == null)
                {
                    LastCombinedReport =
                        $"Симуляция мира не удалась: {worldResult.errorCode} / {worldResult.errorMessage}";
                    return false;
                }

                var working = worldResult.updatedSave;
                // Advance watermark only by appliedSeconds (never re-sim already processed time).
                working.worldGrowth.lastSimulationUtc = watermarkBefore + worldResult.appliedSeconds;
                var watermarkAfter = working.worldGrowth.lastSimulationUtc;
                var remainingAfter = Math.Max(0, now - watermarkAfter);
                Debug.Log(
                    $"[AFK Island] Watermark catch-up: " +
                    $"elapsed={worldElapsed}s ({AfkDurationFormat.Format(worldElapsed)}) " +
                    $"applied={worldResult.appliedSeconds}s ({AfkDurationFormat.Format(worldResult.appliedSeconds)}) " +
                    $"remaining={remainingAfter}s ({AfkDurationFormat.Format(remainingAfter)}) " +
                    $"beforeUtc={watermarkBefore} afterUtc={watermarkAfter}");

                SaveMigrationService.EnsureEcosystemWeatherAnimals(working);
                var livingRng = new DeterministicRng(
                    unchecked((uint)(working.worldGrowth.growthSeed ^ (int)worldResult.appliedSeconds ^ 0xEC05)));
                // Weather → grow existing → colonize → grow new (remaining) → fauna.
                var plantElapsed = Math.Max(0, now - data.lastSaveUtc);
                var nature = LivingOfflineNaturePass.Apply(
                    working,
                    worldResult.appliedSeconds,
                    livingRng,
                    offlineGrowth,
                    plantElapsed);
                LastLivingResult = nature.living;
                LastFaunaResult = LastLivingResult?.fauna;
                working.worldGrowth.growthSeed = unchecked((int)livingRng.State);
                var plantSummary = nature.plantSummary;

                working.lastSaveUtc = now;
                // CIF-A5: normalize settlement/fauna on the working clone before commit.
                SettlementSimulation.RebalanceExcessStones(working);
                FaunaSimulation.EnsureAnimalsOnLand(working);

                if (!saveService.TryWrite(working, out var writeError))
                {
                    LastCombinedReport = $"Commit сейва не удался: {writeError}. Watermark не продвинут.";
                    return false;
                }

                chunkManager.ApplyLogicalState(working);
                saveService.SetAuthoritative(working);
                // CIF-A4: visuals only after successful commit.
                offlineGrowth?.RestoreVisualsFromCommitted(working);

                var unlocked = 0;
                if (working.chunks != null)
                {
                    foreach (var c in working.chunks)
                    {
                        if (c != null && c.isUnlocked)
                        {
                            unlocked++;
                        }
                    }
                }

                var plantResult = nature.plantResult ?? offlineGrowth.LastPlantResult ?? new PlantOfflineResult();
                if (nature.colonization != null)
                {
                    plantResult.plantsSpawnedByNature = nature.colonization.plantsSpawned;
                    plantResult.cellsTargetedByNature = nature.colonization.cellsTargeted;
                    plantResult.natureColonizationLine = nature.colonization.ToReportLine();
                }

                if (nature.settlement != null)
                {
                    var settleLine = nature.settlement.ToReportLine();
                    if (!string.IsNullOrEmpty(settleLine))
                    {
                        plantResult.natureColonizationLine = string.IsNullOrEmpty(plantResult.natureColonizationLine)
                            ? settleLine
                            : plantResult.natureColonizationLine + "\n" + settleLine;
                    }
                }

                LastReturnReport = ReturnReportData.FromSession(
                    worldElapsed,
                    worldResult,
                    working,
                    plantResult.stagesAdvanced,
                    plantResult.plantsMatured,
                    plantResult.plantsPresent,
                    plantSummary,
                    LastFaunaResult,
                    LastLivingResult,
                    plantResult.unknownPlantsSkipped,
                    plantResult.plantsSpawnedByNature,
                    plantResult.natureColonizationLine,
                    nature.settlement);

                var livingLine = LastLivingResult != null ? LastLivingResult.ToReportLine() : string.Empty;
                if (!string.IsNullOrEmpty(livingLine))
                {
                    livingLine += "\n";
                }

                var catchUp = LastReturnReport.catchUpRemainingSeconds;
                var catchUpLine =
                    AfkDurationFormat.FormatCatchUpTriple(
                        worldElapsed, worldResult.appliedSeconds, catchUp) + "\n";

                LastCombinedReport =
                    catchUpLine +
                    worldResult.ToReportText() +
                    livingLine +
                    $"Всего открытых участков острова сейчас: {unlocked}\n\n" +
                    plantSummary;

                offlineGrowth.MarkAppliedExternal(LastCombinedReport);
                appliedThisSession = true;
                Debug.Log("[AFK Island] Offline session committed.\n" + LastCombinedReport);
                return true;
            }
            finally
            {
                saveService.EndLoad();
            }
        }

        public void PrepareFreshRuntime()
        {
            var fresh = CreateFreshSave();
            chunkManager?.RestoreLogicalChunks(fresh.chunks);
            if (plantingSystem != null)
            {
                chunkManager?.EnsureHomeChunkInScene(plantingSystem.Plots);
            }

            saveService?.SetAuthoritative(fresh);
            LastCombinedReport = "Новая игра.";
            LastReturnReport = null;
            LastFaunaResult = null;
            LastLivingResult = null;
            offlineGrowth?.MarkAppliedExternal(LastCombinedReport);
        }

        private GameSaveData CreateFreshSave()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var data = new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                lastSaveUtc = now,
                plants = new System.Collections.Generic.List<PlantSaveData>(),
                chunks = new System.Collections.Generic.List<ChunkSaveData>
                {
                    new ChunkSaveData
                    {
                        chunkX = 0,
                        chunkZ = 0,
                        isUnlocked = true,
                        biomeId = "starter_island",
                        randomSeed = 0,
                        lastSimulatedUtc = now,
                        plants = new System.Collections.Generic.List<PlantSaveData>()
                    }
                },
                worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(now),
                ecosystem = SaveMigrationService.CreateDefaultEcosystem(),
                weather = SaveMigrationService.CreateDefaultWeather(now),
                animals = new System.Collections.Generic.List<AnimalSaveData>(),
                habitats = new System.Collections.Generic.List<HabitatSaveData>()
            };
            data.worldGrowth.lastSimulationUtc = now;
            data.worldGrowth.chunks.Add(SaveMigrationService.CreateGrowthRecord(0, 0, true));
            // C-3I: authored starter settlement (hut by the pond + pad + short road) is the root of the network.
            StarterSettlementSeed.SeedIfEmpty(data);
            SaveMigrationService.EnsureCollectionAndFishingDefaults(data);
            EcosystemSimulation.Recalculate(data, raining: false);
            return data;
        }

        private static string FormatDuration(long seconds) => AfkDurationFormat.Format(seconds);
    }
}
