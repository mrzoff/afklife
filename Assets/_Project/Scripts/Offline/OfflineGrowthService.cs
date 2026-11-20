using System;
using System.Collections.Generic;
using System.Text;
using AfkIsland.Plants;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.Offline
{
    /// <summary>
    /// Plant restore + plant offline growth only. World expansion is WorldGrowthService.
    /// Supports plot-bound and wild (plotId wild_*) living plants.
    /// </summary>
    public sealed class OfflineGrowthService : MonoBehaviour
    {
        [SerializeField] private SaveService saveService;
        [SerializeField] private PlantingSystem plantingSystem;
        [SerializeField] private PlantCatalog plantCatalog;
        [SerializeField] private ChunkManager chunkManager;
        [SerializeField] private float debugTimeMultiplier = 1f;

        private bool appliedThisSession;

        public string LastReturnSummary { get; private set; } = "Новая игра.";
        public PlantOfflineResult LastPlantResult { get; private set; } = new();

        public void Configure(
            SaveService save,
            PlantingSystem planting,
            float timeMultiplier = 1f,
            PlantCatalog catalog = null,
            ChunkManager chunks = null)
        {
            saveService = save;
            plantingSystem = planting;
            debugTimeMultiplier = Mathf.Max(0.01f, timeMultiplier);
            if (catalog != null)
            {
                plantCatalog = catalog;
            }
            else if (planting != null)
            {
                plantCatalog = planting.Catalog;
            }

            if (chunks != null)
            {
                chunkManager = chunks;
            }
        }

        public void MarkAppliedExternal(string summary)
        {
            LastReturnSummary = summary ?? LastReturnSummary;
            appliedThisSession = true;
        }

        /// <summary>
        /// Legacy entry used when WorldGrowthService is absent.
        /// Prefer WorldGrowthService.ApplyOfflineSession().
        /// CIF-A4: ages save data → commit → then restore visuals (never leave live ahead of disk).
        /// </summary>
        public void ApplyOnStartup()
        {
            if (appliedThisSession)
            {
                Debug.LogWarning("[AFK Island] Plant offline already applied this session.");
                return;
            }

            if (saveService == null || plantingSystem == null)
            {
                LastReturnSummary = "Системы сохранения/посадки не готовы.";
                return;
            }

            if (!saveService.TryRead(out var data) || data == null)
            {
                LastReturnSummary = "Сохранения нет. Тапни по зелёной точке посадки.";
                appliedThisSession = true;
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var elapsed = Math.Max(0, now - data.lastSaveUtc);
            // Age authoritative plant records only; do not spawn/grow live objects yet.
            AgeSavePlantsOnly(data, elapsed, 1f);
            data.lastSaveUtc = now;
            if (!saveService.TryWrite(data, out var writeError))
            {
                LastReturnSummary = $"Commit сейва не удался: {writeError}";
                Debug.LogError("[AFK Island] ApplyOnStartup write failed: " + writeError);
                return;
            }

            saveService.SetAuthoritative(data);
            LastReturnSummary = RestoreVisualsFromCommitted(data);
            appliedThisSession = true;
        }

        /// <summary>
        /// CIF-A4: after successful commit, spawn/sync live plants from the committed snapshot.
        /// Does not advance growth — stages come from save data only.
        /// </summary>
        public string RestoreVisualsFromCommitted(GameSaveData data)
        {
            if (plantingSystem == null)
            {
                return "PlantingSystem не готов.";
            }

            if (data == null)
            {
                return "Нет committed snapshot.";
            }

            PlantColonizationSimulation.SyncRootPlantIndex(data);
            var plants = ResolvePlants(data);
            if (plants == null)
            {
                return "Сохранение повреждено: нет списка растений.";
            }

            var unknownSkipped = RestorePlants(plants);
            SyncLiveStagesFromSave(plants);

            var present = 0;
            foreach (var _ in plantingSystem.EnumerateLivePlants())
            {
                present++;
            }

            var summary =
                $"Растения восстановлены из commit: {present}" +
                (unknownSkipped > 0 ? $", пропущено неизвестных: {unknownSkipped}" : string.Empty);
            LastPlantResult = new PlantOfflineResult
            {
                summaryText = summary,
                plantsPresent = present,
                unknownPlantsSkipped = unknownSkipped
            };
            return summary;
        }

        private void AgeSavePlantsOnly(GameSaveData data, long elapsedSeconds, float weatherMul)
        {
            if (data?.chunks == null)
            {
                return;
            }

            var mul = weatherMul < 1f ? 1f : weatherMul;
            const float stageSeconds = 6f * 3600f;
            var simulated = elapsedSeconds * debugTimeMultiplier * mul;

            foreach (var chunk in data.chunks)
            {
                if (chunk?.plants == null)
                {
                    continue;
                }

                foreach (var p in chunk.plants)
                {
                    if (p == null || simulated <= 0d)
                    {
                        continue;
                    }

                    var remaining = (float)simulated;
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
            }

            PlantColonizationSimulation.SyncRootPlantIndex(data);
        }

        private void SyncLiveStagesFromSave(List<PlantSaveData> plants)
        {
            if (plants == null || plantingSystem == null)
            {
                return;
            }

            foreach (var plantSave in plants)
            {
                if (plantSave == null)
                {
                    continue;
                }

                PlantInstance live = null;
                if (!string.IsNullOrEmpty(plantSave.instanceId))
                {
                    live = plantingSystem.FindLivePlantByInstanceId(plantSave.instanceId);
                }

                if (live == null && !string.IsNullOrEmpty(plantSave.plotId))
                {
                    live = FindLivePlantByPlotId(plantSave.plotId);
                }

                live?.SetState(plantSave.stage, plantSave.growthProgress);
            }
        }

        public string ApplyPlantsFromSave(GameSaveData data, long elapsedSeconds, float weatherGrowthMultiplier = 1f)
        {
            if (plantingSystem == null)
            {
                return "PlantingSystem не готов.";
            }

            PlantColonizationSimulation.SyncRootPlantIndex(data);
            var plants = ResolvePlants(data);
            if (plants == null)
            {
                return "Сохранение повреждено: нет списка растений.";
            }

            // Weather can only accelerate (never punish long AFK).
            var weatherMul = weatherGrowthMultiplier < 1f ? 1f : weatherGrowthMultiplier;
            var simulated = elapsedSeconds * debugTimeMultiplier * weatherMul;
            var unknownSkipped = RestorePlants(plants);
            var before = CapturePlantStages();
            ApplyGrowth(simulated, null);
            var after = CapturePlantStages();
            AccumulateGrowthStats(before, after, out var stagesAdvanced, out var matured, out var present);

            var summary =
                $"Растения: отсутствие {FormatDuration(elapsedSeconds)}, " +
                $"симулировано {FormatDuration((long)simulated)} (x{debugTimeMultiplier:0.##})\n" +
                BuildPlantSummary();
            if (unknownSkipped > 0)
            {
                summary += $"\nПропущено неизвестных plantId: {unknownSkipped}";
            }

            LastPlantResult = new PlantOfflineResult
            {
                summaryText = summary,
                plantsPresent = present,
                stagesAdvanced = stagesAdvanced,
                plantsMatured = matured,
                unknownPlantsSkipped = unknownSkipped
            };
            LastReturnSummary = summary;
            return summary;
        }

        /// <summary>
        /// Restore newly colonized plants and grow each only for its remaining window seconds.
        /// Does not re-apply full AFK growth to plants already processed this pass.
        /// </summary>
        public void RestoreAndGrowColonized(
            GameSaveData data,
            IReadOnlyList<PlantSaveData> colonized,
            IReadOnlyDictionary<string, long> growthSecondsByInstanceId,
            float weatherGrowthMultiplier = 1f)
        {
            if (plantingSystem == null || colonized == null || colonized.Count == 0)
            {
                return;
            }

            var weatherMul = weatherGrowthMultiplier < 1f ? 1f : weatherGrowthMultiplier;
            var unknownSkipped = RestorePlants(new List<PlantSaveData>(colonized));
            var before = CapturePlantStages();

            var overrides = new Dictionary<string, double>();
            foreach (var plant in colonized)
            {
                if (plant == null || string.IsNullOrEmpty(plant.instanceId))
                {
                    continue;
                }

                long rem = 0;
                growthSecondsByInstanceId?.TryGetValue(plant.instanceId, out rem);
                overrides[plant.instanceId] = rem * debugTimeMultiplier * weatherMul;
            }

            ApplyGrowth(0d, overrides);
            var after = CapturePlantStages();
            AccumulateGrowthStats(before, after, out var stagesAdvanced, out var matured, out var present);

            var prev = LastPlantResult ?? new PlantOfflineResult();
            LastPlantResult = new PlantOfflineResult
            {
                summaryText = prev.summaryText,
                plantsPresent = Mathf.Max(prev.plantsPresent, present),
                stagesAdvanced = prev.stagesAdvanced + stagesAdvanced,
                plantsMatured = prev.plantsMatured + matured,
                unknownPlantsSkipped = prev.unknownPlantsSkipped + unknownSkipped,
                plantsSpawnedByNature = colonized.Count,
                natureColonizationLine = prev.natureColonizationLine
            };
        }

        private static void AccumulateGrowthStats(
            Dictionary<string, (int stage, bool mature)> before,
            Dictionary<string, (int stage, bool mature)> after,
            out int stagesAdvanced,
            out int matured,
            out int present)
        {
            stagesAdvanced = 0;
            matured = 0;
            present = 0;
            foreach (var kv in after)
            {
                present++;
                if (!before.TryGetValue(kv.Key, out var prev))
                {
                    if (kv.Value.mature)
                    {
                        matured++;
                    }

                    continue;
                }

                if (kv.Value.stage > prev.stage)
                {
                    stagesAdvanced += kv.Value.stage - prev.stage;
                }

                if (!prev.mature && kv.Value.mature)
                {
                    matured++;
                }
            }
        }

        /// <summary>
        /// Writes live plot + wild plant stages back into chunk/root plant lists before TryWrite.
        /// </summary>
        public void MergeLivePlantsIntoSave(GameSaveData data)
        {
            if (data == null || plantingSystem == null)
            {
                return;
            }

            data.chunks ??= new List<ChunkSaveData>();
            data.plants ??= new List<PlantSaveData>();

            foreach (var plant in plantingSystem.EnumerateLivePlants())
            {
                if (plant == null || string.IsNullOrEmpty(plant.InstanceId))
                {
                    continue;
                }

                var plotId = plant.BoundPlotId ?? string.Empty;
                var pos = plant.transform.position;
                var plantId = plant.Data != null ? plant.Data.plantId : "starter_tree";
                UpsertPlantSave(
                    data,
                    plant.InstanceId,
                    plantId,
                    plotId,
                    plant.StageIndex,
                    plant.StageProgress,
                    pos);
            }

            // Root plants is a compatibility index of authoritative chunk.plants.
            PlantColonizationSimulation.SyncRootPlantIndex(data);
        }

        private static void UpsertPlantSave(
            GameSaveData data,
            string instanceId,
            string plantId,
            string plotId,
            int stage,
            float progress,
            Vector3 pos)
        {
            PlantSaveData existing = null;
            ChunkSaveData ownerChunk = null;

            foreach (var chunk in data.chunks)
            {
                if (chunk?.plants == null)
                {
                    continue;
                }

                foreach (var p in chunk.plants)
                {
                    if (p == null)
                    {
                        continue;
                    }

                    if (p.instanceId == instanceId ||
                        (!string.IsNullOrEmpty(plotId) && p.plotId == plotId))
                    {
                        existing = p;
                        ownerChunk = chunk;
                        break;
                    }
                }

                if (existing != null)
                {
                    break;
                }
            }

            if (existing == null)
            {
                foreach (var p in data.plants)
                {
                    if (p != null &&
                        (p.instanceId == instanceId ||
                         (!string.IsNullOrEmpty(plotId) && p.plotId == plotId)))
                    {
                        existing = p;
                        break;
                    }
                }
            }

            if (existing != null)
            {
                existing.plantId = plantId;
                existing.plotId = plotId;
                existing.stage = stage;
                existing.growthProgress = progress;
                existing.posX = pos.x;
                existing.posY = pos.y;
                existing.posZ = pos.z;
                if (ownerChunk == null)
                {
                    PlaceInNearestChunk(data, existing);
                }

                return;
            }

            var created = new PlantSaveData
            {
                instanceId = instanceId,
                plantId = plantId,
                plotId = plotId,
                stage = stage,
                growthProgress = progress,
                posX = pos.x,
                posY = pos.y,
                posZ = pos.z
            };
            PlaceInNearestChunk(data, created);
            data.plants.Add(created);
        }

        private static void PlaceInNearestChunk(GameSaveData data, PlantSaveData plant)
        {
            const float chunkSize = 16f;
            var cx = Mathf.FloorToInt((plant.posX + chunkSize * 0.5f) / chunkSize);
            var cz = Mathf.FloorToInt((plant.posZ + chunkSize * 0.5f) / chunkSize);
            ChunkSaveData chunk = null;
            foreach (var c in data.chunks)
            {
                if (c != null && c.chunkX == cx && c.chunkZ == cz)
                {
                    chunk = c;
                    break;
                }
            }

            if (chunk == null)
            {
                chunk = new ChunkSaveData
                {
                    chunkX = cx,
                    chunkZ = cz,
                    isUnlocked = true,
                    biomeId = "starter_island",
                    plants = new List<PlantSaveData>()
                };
                data.chunks.Add(chunk);
            }

            chunk.plants ??= new List<PlantSaveData>();
            var already = false;
            foreach (var p in chunk.plants)
            {
                if (p != null && p.instanceId == plant.instanceId)
                {
                    already = true;
                    break;
                }
            }

            if (!already)
            {
                chunk.plants.Add(plant);
            }
        }

        private static List<PlantSaveData> FlattenFromChunks(GameSaveData data)
        {
            var list = new List<PlantSaveData>();
            if (data?.chunks == null)
            {
                return list;
            }

            foreach (var chunk in data.chunks)
            {
                if (chunk?.plants == null)
                {
                    continue;
                }

                foreach (var p in chunk.plants)
                {
                    if (p != null)
                    {
                        list.Add(p);
                    }
                }
            }

            return list;
        }

        private Dictionary<string, (int stage, bool mature)> CapturePlantStages()
        {
            var map = new Dictionary<string, (int stage, bool mature)>();
            if (plantingSystem == null)
            {
                return map;
            }

            foreach (var plant in plantingSystem.EnumerateLivePlants())
            {
                if (plant == null || string.IsNullOrEmpty(plant.InstanceId))
                {
                    continue;
                }

                var key = !string.IsNullOrEmpty(plant.BoundPlotId)
                    ? plant.BoundPlotId
                    : plant.InstanceId;
                map[key] = (plant.StageIndex, plant.IsFullyGrown);
            }

            return map;
        }

        private List<PlantSaveData> ResolvePlants(GameSaveData data)
        {
            // Authoritative: chunk.plants (deduped). Root plants only as legacy fallback.
            PlantColonizationSimulation.SyncRootPlantIndex(data);
            if (data?.chunks != null && data.chunks.Count > 0)
            {
                var list = new List<PlantSaveData>();
                var seen = new HashSet<string>();
                foreach (var chunk in data.chunks)
                {
                    if (chunk?.plants == null)
                    {
                        continue;
                    }

                    foreach (var p in chunk.plants)
                    {
                        if (p == null)
                        {
                            continue;
                        }

                        var key = !string.IsNullOrEmpty(p.instanceId) ? p.instanceId : p.plotId;
                        if (string.IsNullOrEmpty(key) || !seen.Add(key))
                        {
                            continue;
                        }

                        list.Add(p);
                    }
                }

                if (list.Count > 0)
                {
                    return list;
                }
            }

            return data?.plants ?? new List<PlantSaveData>();
        }

        private int RestorePlants(List<PlantSaveData> plants)
        {
            var unknownSkipped = 0;
            var catalog = plantCatalog != null ? plantCatalog : plantingSystem.Catalog;
            foreach (var plantSave in plants)
            {
                if (plantSave == null)
                {
                    continue;
                }

                PlantData plantData = null;
                catalog?.TryGetById(plantSave.plantId, out plantData);
                if (plantData == null)
                {
                    unknownSkipped++;
                    Debug.LogWarning(
                        $"[AFK Island] Unknown plantId '{plantSave.plantId}' " +
                        $"(instance {plantSave.instanceId}). Skipping.");
                    continue;
                }

                if (plantingSystem.FindLivePlantByInstanceId(plantSave.instanceId) != null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(plantSave.plotId) &&
                    FindLivePlantByPlotId(plantSave.plotId) != null)
                {
                    continue;
                }

                PlantPlot plot = null;
                if (!PlantColonizationSimulation.IsWildPlotId(plantSave.plotId))
                {
                    foreach (var p in plantingSystem.Plots)
                    {
                        if (p != null && p.PlotId == plantSave.plotId)
                        {
                            plot = p;
                            break;
                        }
                    }
                }

                if (plot != null)
                {
                    if (plot.IsOccupied)
                    {
                        continue;
                    }

                    plantingSystem.RestorePlant(
                        plot,
                        plantData,
                        plantSave.instanceId,
                        plantSave.stage,
                        plantSave.growthProgress);
                    continue;
                }

                // Wild or orphaned plotId: spawn at saved world position.
                var worldPos = new Vector3(plantSave.posX, plantSave.posY, plantSave.posZ);
                if (worldPos.sqrMagnitude < 0.0001f && !PlantColonizationSimulation.IsWildPlotId(plantSave.plotId))
                {
                    // Legacy plot plant with missing plot — skip rather than spawn at origin.
                    continue;
                }

                // Stage 11E: home wild trees from full 16×16 mask float in the ocean void.
                if (PlantColonizationSimulation.IsWildPlotId(plantSave.plotId) &&
                    plantSave.plotId.StartsWith("wild_0_0_", System.StringComparison.Ordinal) &&
                    !GrowthWorldPlacement.IsInsideAuthoredHomeFootprint(worldPos))
                {
                    continue;
                }

                // Keep the saved plant. Do not draw it through a hut. Coordinates stay.
                if (SettlementCellQuery.TryParseWildCell(
                        plantSave.plotId, out var wildChunkX, out var wildChunkZ, out var wildCell) &&
                    SettlementCellQuery.IsHut(saveService.AuthoritativeSnapshot, wildChunkX, wildChunkZ, wildCell))
                {
                    continue;
                }

                plantingSystem.PlantAtWorldPosition(
                    worldPos,
                    plantData,
                    plantSave.instanceId,
                    plantSave.plotId,
                    plantSave.stage,
                    plantSave.growthProgress);
            }

            return unknownSkipped;
        }

        private PlantInstance FindLivePlantByPlotId(string plotId)
        {
            if (string.IsNullOrEmpty(plotId) || plantingSystem == null)
            {
                return null;
            }

            foreach (var plant in plantingSystem.EnumerateLivePlants())
            {
                if (plant != null && plant.BoundPlotId == plotId)
                {
                    return plant;
                }
            }

            return null;
        }

        private void ApplyGrowth(double defaultSimulatedSeconds, Dictionary<string, double> secondsByInstanceId)
        {
            if (plantingSystem == null)
            {
                return;
            }

            foreach (var plant in plantingSystem.EnumerateLivePlants())
            {
                if (plant == null)
                {
                    continue;
                }

                double seconds = defaultSimulatedSeconds;
                if (secondsByInstanceId != null)
                {
                    if (!secondsByInstanceId.TryGetValue(plant.InstanceId, out seconds))
                    {
                        continue;
                    }
                }

                plant.ApplyOfflineSeconds(seconds);
            }
        }

        private string BuildPlantSummary()
        {
            var sb = new StringBuilder();
            if (plantingSystem == null)
            {
                return "Растений нет.";
            }

            foreach (var plant in plantingSystem.EnumerateLivePlants())
            {
                if (plant == null)
                {
                    continue;
                }

                var id = plant.Data != null ? plant.Data.plantId : "unknown";
                var label = !string.IsNullOrEmpty(plant.BoundPlotId) ? plant.BoundPlotId : plant.InstanceId;
                sb.AppendLine(
                    $"• {label} [{id}]: стадия {plant.StageIndex + 1}, прогресс {plant.StageProgress * 100f:0}%");
            }

            return sb.Length == 0 ? "Растений нет." : sb.ToString();
        }

        private static string FormatDuration(long seconds) => AfkDurationFormat.Format(seconds);
    }
}
