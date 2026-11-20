using System.Collections.Generic;
using AfkIsland.Offline;
using AfkIsland.Save;

namespace AfkIsland.World
{
    /// <summary>
    /// Shared offline nature pipeline after world growth:
    /// weather → grow existing plants → colonize → grow new (remaining time only) → fauna.
    /// CIF-A4: mutates working save data only — no live plant / visual side effects.
    /// </summary>
    public static class LivingOfflineNaturePass
    {
        public sealed class Result
        {
            public WeatherSimulation.WeatherWindowStats weatherStats;
            public PlantColonizationResult colonization;
            public SettlementSimulationResult settlement;
            public string plantSummary = string.Empty;
            public PlantOfflineResult plantResult;
            public LivingWorldResult living;
        }

        public static Result Apply(
            GameSaveData working,
            long appliedSeconds,
            DeterministicRng rng,
            OfflineGrowthService offlineGrowth,
            long plantElapsedSeconds = -1)
        {
            var result = new Result();
            if (working == null || appliedSeconds <= 0 || rng == null)
            {
                result.colonization = new PlantColonizationResult();
                result.settlement = new SettlementSimulationResult();
                result.living = new LivingWorldResult();
                result.plantResult = new PlantOfflineResult();
                return result;
            }

            // offlineGrowth is retained for call-site compatibility; CIF-A4 ages save data only.
            _ = offlineGrowth;

            SaveMigrationService.EnsureEcosystemWeatherAnimals(working);
            PlantColonizationSimulation.SyncRootPlantIndex(working);

            result.weatherStats = WeatherSimulation.Advance(working, appliedSeconds, rng);

            var plantElapsed = plantElapsedSeconds >= 0 ? plantElapsedSeconds : appliedSeconds;
            var weatherMul = result.weatherStats.plantGrowthMultiplier;

            var beforeStages = CaptureAuthoritativeStages(working);

            // 1) Existing plants get the full AFK/plant window first (data only).
            AgeAuthoritativePlants(working, plantElapsed, weatherMul, null);

            // 2) Colonize into chunk.plants (idempotent per occupied wild plotId).
            result.colonization = PlantColonizationSimulation.Apply(
                working, appliedSeconds, rng, result.weatherStats);

            // 3) New plants grow only remaining time after their spawn point in the window.
            if (result.colonization.plantsSpawned > 0)
            {
                AgeAuthoritativePlants(
                    working,
                    0,
                    weatherMul,
                    result.colonization.growthSecondsByInstanceId);
            }

            PlantColonizationSimulation.SyncRootPlantIndex(working);

            var afterStages = CaptureAuthoritativeStages(working);
            result.plantResult = BuildPlantResult(beforeStages, afterStages, plantElapsed, weatherMul);
            result.plantSummary = result.plantResult.summaryText ?? string.Empty;

            if (result.colonization != null)
            {
                result.plantResult.plantsSpawnedByNature = result.colonization.plantsSpawned;
                result.plantResult.cellsTargetedByNature = result.colonization.cellsTargeted;
                result.plantResult.natureColonizationLine = result.colonization.ToReportLine();
                var colonizeLine = result.colonization.ToReportLine();
                if (!string.IsNullOrEmpty(colonizeLine))
                {
                    result.plantSummary = colonizeLine + "\n" + result.plantSummary;
                }
            }

            // Settlement grows after nature/fauna: land mask already advanced this session.
            result.settlement = SettlementSimulation.Apply(working, appliedSeconds, rng);

            result.living = LivingWorldSimulation.SimulateAfterWeather(
                working, appliedSeconds, rng, result.weatherStats);
            return result;
        }

        private static Dictionary<string, (int stage, float progress)> CaptureAuthoritativeStages(
            GameSaveData save)
        {
            var map = new Dictionary<string, (int stage, float progress)>();
            if (save?.chunks == null)
            {
                return map;
            }

            foreach (var c in save.chunks)
            {
                if (c?.plants == null)
                {
                    continue;
                }

                foreach (var p in c.plants)
                {
                    if (p == null)
                    {
                        continue;
                    }

                    var key = !string.IsNullOrEmpty(p.instanceId) ? p.instanceId : p.plotId;
                    if (string.IsNullOrEmpty(key) || map.ContainsKey(key))
                    {
                        continue;
                    }

                    map[key] = (p.stage, p.growthProgress);
                }
            }

            return map;
        }

        private static PlantOfflineResult BuildPlantResult(
            Dictionary<string, (int stage, float progress)> before,
            Dictionary<string, (int stage, float progress)> after,
            long plantElapsed,
            float weatherMul)
        {
            var stagesAdvanced = 0;
            var matured = 0;
            var present = after?.Count ?? 0;
            if (before != null && after != null)
            {
                foreach (var kv in after)
                {
                    if (!before.TryGetValue(kv.Key, out var prev))
                    {
                        if (kv.Value.stage >= 3)
                        {
                            matured++;
                        }

                        continue;
                    }

                    if (kv.Value.stage > prev.stage)
                    {
                        stagesAdvanced += kv.Value.stage - prev.stage;
                    }

                    if (prev.stage < 3 && kv.Value.stage >= 3)
                    {
                        matured++;
                    }
                }
            }

            var mul = weatherMul < 1f ? 1f : weatherMul;
            var summary =
                $"Растения: отсутствие {AfkDurationFormat.Format(plantElapsed)}, " +
                $"симулировано {AfkDurationFormat.Format((long)(plantElapsed * mul))} (данные)\n" +
                $"Стадий +{stagesAdvanced}, созрело {matured}, сейчас {present}";

            return new PlantOfflineResult
            {
                summaryText = summary,
                plantsPresent = present,
                stagesAdvanced = stagesAdvanced,
                plantsMatured = matured,
                unknownPlantsSkipped = 0
            };
        }

        /// <summary>
        /// Ages chunk.plants only (authoritative). Optional per-instance seconds for new plants.
        /// When overrides is null and elapsedSeconds &gt; 0, ages every plant by elapsed.
        /// When overrides is set, only those instanceIds are aged (new colonized plants).
        /// </summary>
        private static void AgeAuthoritativePlants(
            GameSaveData save,
            long elapsedSeconds,
            float weatherMul,
            Dictionary<string, long> overrides)
        {
            if (save?.chunks == null)
            {
                return;
            }

            var mul = weatherMul < 1f ? 1f : weatherMul;
            const float stageSeconds = 6f * 3600f;

            void AgeOne(PlantSaveData p, double seconds)
            {
                if (p == null || seconds <= 0d)
                {
                    return;
                }

                var remaining = (float)(seconds * mul);
                while (remaining > 0f && p.stage < 3)
                {
                    var left = (1f - p.growthProgress) * stageSeconds;
                    if (remaining >= left)
                    {
                        remaining -= left;
                        p.growthProgress = 0f;
                        if (p.stage < 3)
                        {
                            p.stage++;
                        }
                        else
                        {
                            p.growthProgress = 1f;
                            break;
                        }
                    }
                    else
                    {
                        p.growthProgress += remaining / stageSeconds;
                        remaining = 0f;
                    }
                }

                if (p.stage >= 3)
                {
                    p.stage = 3;
                    p.growthProgress = 1f;
                }
            }

            foreach (var c in save.chunks)
            {
                if (c?.plants == null)
                {
                    continue;
                }

                foreach (var p in c.plants)
                {
                    if (p == null)
                    {
                        continue;
                    }

                    if (overrides != null)
                    {
                        if (overrides.TryGetValue(p.instanceId, out var rem))
                        {
                            AgeOne(p, rem);
                        }

                        continue;
                    }

                    AgeOne(p, elapsedSeconds);
                }
            }

            PlantColonizationSimulation.SyncRootPlantIndex(save);
        }
    }
}
