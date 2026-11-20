using System;
using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    public sealed class PlantColonizationResult
    {
        public int plantsSpawned;
        public int cellsTargeted;
        public readonly List<PlantSaveData> spawned = new();
        /// <summary>Remaining offline seconds each new plant may grow in this window.</summary>
        public readonly Dictionary<string, long> growthSecondsByInstanceId = new();

        public string ToReportLine()
        {
            if (plantsSpawned <= 0)
            {
                return string.Empty;
            }

            return plantsSpawned == 1
                ? "Природа заселила 1 новый росток на доступной земле"
                : $"Природа заселила {plantsSpawned} новых растений на доступной земле";
        }
    }

    /// <summary>
    /// Offline seed spread into living PlantSaveData on Moss+ cells.
    /// Authority: writes only into chunk.plants; root plants is a rebuilt index.
    /// </summary>
    public static class PlantColonizationSimulation
    {
        public const string DefaultPlantId = "starter_tree";
        public const float MinSeparationMeters = 3.5f;
        public const int AbsoluteMaxNewPerSession = 12;
        /// <summary>One new wild plant budget per this many suitable Moss+ cells.</summary>
        public const int CellsPerNewPlant = 8;

        public static bool IsWildPlotId(string plotId) =>
            !string.IsNullOrEmpty(plotId) &&
            plotId.StartsWith("wild_", System.StringComparison.Ordinal);

        public static string MakeWildPlotId(int chunkX, int chunkZ, int cellIndex) =>
            $"wild_{chunkX}_{chunkZ}_{cellIndex}";

        public static string MakeWildInstanceId(int chunkX, int chunkZ, int cellIndex) =>
            $"p_wild_{chunkX}_{chunkZ}_{cellIndex}";

        public static int ResolveMaxNewPerSession(long appliedSeconds)
        {
            var hours = appliedSeconds / 3600.0;
            // CIF-A2: sub-5-minute AFK gets no colonization budget.
            if (hours < 1.0 / 12.0)
            {
                return 0;
            }

            if (hours >= 7 * 24)
            {
                return AbsoluteMaxNewPerSession;
            }

            if (hours >= 3 * 24)
            {
                return 10;
            }

            if (hours >= 24)
            {
                return 7;
            }

            if (hours >= 8)
            {
                return 5;
            }

            // Sub-hour but >=5 min: at most one sprout attempt.
            if (hours < 1.0)
            {
                return 1;
            }

            // Short multi-hour AFK: at most a couple of first sprouts when land allows.
            return 2;
        }

        public static int ResolveMaxNewByLandArea(int suitableCellCount)
        {
            if (suitableCellCount <= 0)
            {
                return 0;
            }

            var byArea = suitableCellCount / CellsPerNewPlant;
            // Sparse land can still get one attempt; chance + occupancy still gate outcomes.
            return Mathf.Clamp(Mathf.Max(1, byArea), 0, AbsoluteMaxNewPerSession);
        }

        /// <summary>
        /// Rebuild root plants as a deduped flatten of chunk.plants (compatibility index).
        /// </summary>
        public static void SyncRootPlantIndex(GameSaveData save)
        {
            if (save == null)
            {
                return;
            }

            save.chunks ??= new List<ChunkSaveData>();
            var list = new List<PlantSaveData>();
            var seen = new HashSet<string>();
            foreach (var chunk in save.chunks)
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

                    var key = !string.IsNullOrEmpty(p.instanceId)
                        ? p.instanceId
                        : (p.plotId ?? string.Empty);
                    if (string.IsNullOrEmpty(key) || !seen.Add(key))
                    {
                        continue;
                    }

                    list.Add(p);
                }
            }

            save.plants = list;
        }

        public static PlantColonizationResult Apply(
            GameSaveData save,
            long appliedSeconds,
            DeterministicRng rng,
            WeatherSimulation.WeatherWindowStats weatherStats = null)
        {
            var result = new PlantColonizationResult();
            if (save == null || appliedSeconds <= 0 || rng == null)
            {
                return result;
            }

            SaveMigrationService.EnsureEcosystemWeatherAnimals(save);
            save.chunks ??= new List<ChunkSaveData>();
            save.worldGrowth ??= SaveMigrationService.CreateDefaultWorldGrowth(0);

            // Same watermark already colonized → do not spawn again (interval idempotency).
            if (save.worldGrowth.lastSimulationUtc > 0 &&
                save.worldGrowth.natureColonizationWatermarkUtc == save.worldGrowth.lastSimulationUtc)
            {
                SyncRootPlantIndex(save);
                return result;
            }

            var existing = CollectAuthoritativePlants(save);
            var occupiedPlotIds = new HashSet<string>();
            foreach (var p in existing)
            {
                if (!string.IsNullOrEmpty(p.plotId))
                {
                    occupiedPlotIds.Add(p.plotId);
                }
            }

            var candidates = CollectCandidateCells(save, occupiedPlotIds);
            result.cellsTargeted = candidates.Count;
            if (candidates.Count == 0)
            {
                MarkColonizationWatermark(save);
                SyncRootPlantIndex(save);
                return result;
            }

            var bandCap = ResolveMaxNewPerSession(appliedSeconds);
            var areaCap = ResolveMaxNewByLandArea(candidates.Count);
            var maxNew = Mathf.Min(bandCap, areaCap, AbsoluteMaxNewPerSession);
            if (maxNew <= 0)
            {
                MarkColonizationWatermark(save);
                SyncRootPlantIndex(save);
                return result;
            }

            var hoursExact = appliedSeconds / 3600.0;
            var hourDenom = (float)Math.Max(hoursExact, 1e-6);
            var rainShare = weatherStats != null
                ? Mathf.Clamp01(weatherStats.rainHours / hourDenom)
                : 0f;
            // Rain only helps colonization (never below base chance).
            var baseChance = 0.42 + rainShare * 0.28;
            // CIF-A2: scale chance by fraction of hour so short visits don't equal 1h AFK.
            if (hoursExact < 1.0)
            {
                baseChance *= hoursExact;
            }

            // Stable order: Vegetation first, then Moss; ties by chunk/cell.
            candidates.Sort(CompareCandidates);

            while (candidates.Count > 0 && result.plantsSpawned < maxNew)
            {
                var idx = (int)(rng.NextUInt() % (uint)candidates.Count);
                var cell = candidates[idx];
                candidates.RemoveAt(idx);

                var plotId = MakeWildPlotId(cell.chunkX, cell.chunkZ, cell.cellIndex);
                if (occupiedPlotIds.Contains(plotId))
                {
                    continue;
                }

                if (TooCloseToExisting(existing, cell.worldPos))
                {
                    continue;
                }

                var chance = cell.stage >= (byte)CellStage.Vegetation
                    ? Mathf.Min(0.95f, (float)(baseChance + 0.25))
                    : (float)baseChance;
                if (rng.NextDouble() > chance)
                {
                    continue;
                }

                // Spawn somewhere inside the AFK window; only remaining time may grow.
                var spawnFraction = rng.NextDouble();
                var remaining = (long)Math.Max(0L, (long)(appliedSeconds * (1.0 - spawnFraction)));

                var instanceId = MakeWildInstanceId(cell.chunkX, cell.chunkZ, cell.cellIndex);
                var plant = new PlantSaveData
                {
                    instanceId = instanceId,
                    plantId = DefaultPlantId,
                    plotId = plotId,
                    stage = 0,
                    growthProgress = 0f,
                    posX = cell.worldPos.x,
                    posY = cell.worldPos.y,
                    posZ = cell.worldPos.z
                };

                if (!TryAddPlantToChunk(save, cell.chunkX, cell.chunkZ, plant))
                {
                    continue;
                }

                existing.Add(plant);
                occupiedPlotIds.Add(plotId);
                result.spawned.Add(plant);
                result.growthSecondsByInstanceId[instanceId] = remaining;
                result.plantsSpawned++;
            }

            MarkColonizationWatermark(save);
            SyncRootPlantIndex(save);
            return result;
        }

        private static void MarkColonizationWatermark(GameSaveData save)
        {
            if (save?.worldGrowth != null && save.worldGrowth.lastSimulationUtc > 0)
            {
                save.worldGrowth.natureColonizationWatermarkUtc = save.worldGrowth.lastSimulationUtc;
            }
        }

        private static int CompareCandidates(CandidateCell a, CandidateCell b)
        {
            var stageCmp = b.stage.CompareTo(a.stage);
            if (stageCmp != 0)
            {
                return stageCmp;
            }

            var cx = a.chunkX.CompareTo(b.chunkX);
            if (cx != 0)
            {
                return cx;
            }

            var cz = a.chunkZ.CompareTo(b.chunkZ);
            if (cz != 0)
            {
                return cz;
            }

            return a.cellIndex.CompareTo(b.cellIndex);
        }

        private static List<PlantSaveData> CollectAuthoritativePlants(GameSaveData save)
        {
            var list = new List<PlantSaveData>();
            var seen = new HashSet<string>();
            if (save.chunks != null)
            {
                foreach (var chunk in save.chunks)
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

                        var key = !string.IsNullOrEmpty(p.instanceId)
                            ? p.instanceId
                            : (p.plotId ?? string.Empty);
                        if (string.IsNullOrEmpty(key) || !seen.Add(key))
                        {
                            continue;
                        }

                        list.Add(p);
                    }
                }
            }

            // Root index only if chunks have no plants (legacy / migration).
            if (list.Count == 0 && save.plants != null)
            {
                foreach (var p in save.plants)
                {
                    if (p != null)
                    {
                        list.Add(p);
                    }
                }
            }

            return list;
        }

        private static bool TooCloseToExisting(List<PlantSaveData> existing, Vector3 pos)
        {
            var minSq = MinSeparationMeters * MinSeparationMeters;
            foreach (var p in existing)
            {
                if (p == null)
                {
                    continue;
                }

                var dx = p.posX - pos.x;
                var dz = p.posZ - pos.z;
                if (dx * dx + dz * dz < minSq)
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class CandidateCell
        {
            public int chunkX;
            public int chunkZ;
            public int cellIndex;
            public byte stage;
            public Vector3 worldPos;
        }

        private static List<CandidateCell> CollectCandidateCells(
            GameSaveData save,
            HashSet<string> occupiedPlotIds)
        {
            var list = new List<CandidateCell>();

            if (save.worldGrowth?.chunks == null)
            {
                return list;
            }

            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    if (growth.isLegacyBase)
                    {
                        AddLegacyFallbackCandidates(list, growth.chunkX, growth.chunkZ, occupiedPlotIds);
                    }

                    continue;
                }

                ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settlement, out _);

                // Legacy home mask is a full 16×16 rectangle; authored mesh is smaller.
                // Never scatter wild trees across the whole mask — they float in the ocean void.
                if (growth.isLegacyBase)
                {
                    AddLegacyFallbackCandidates(list, growth.chunkX, growth.chunkZ, occupiedPlotIds);
                    continue;
                }

                for (var i = 0; i < stages.Length; i++)
                {
                    if (stages[i] < (byte)CellStage.Moss)
                    {
                        continue;
                    }

                    // Keep wild plants inland — shore cells look like water after mesh warp.
                    if (GrowthWorldPlacement.IsShoreCell(save, growth.chunkX, growth.chunkZ, i, radius: 1))
                    {
                        continue;
                    }

                    var worldPos = GrowthWorldPlacement.CellWorldPosition(
                        growth.chunkX,
                        growth.chunkZ,
                        i,
                        stages[i],
                        growth.isLegacyBase);
                    // Stage 11E: never colonize the authored WaterSource disc.
                    if (WaterSourceLayout.IsInsideReserveXZ(worldPos))
                    {
                        continue;
                    }

                    if (settlement != null && i < settlement.Length &&
                        settlement[i] == (byte)SettlementStage.Hut)
                    {
                        continue;
                    }

                    var plotId = MakeWildPlotId(growth.chunkX, growth.chunkZ, i);
                    if (occupiedPlotIds.Contains(plotId))
                    {
                        continue;
                    }

                    list.Add(new CandidateCell
                    {
                        chunkX = growth.chunkX,
                        chunkZ = growth.chunkZ,
                        cellIndex = i,
                        stage = stages[i],
                        worldPos = worldPos
                    });
                }
            }

            return list;
        }

        private static void AddLegacyFallbackCandidates(
            List<CandidateCell> list,
            int chunkX,
            int chunkZ,
            HashSet<string> occupiedPlotIds)
        {
            var spots = new[]
            {
                new Vector3(-2.2f, 1.9f, 1.6f),
                new Vector3(2.0f, 1.9f, 1.4f),
                new Vector3(-1.8f, 1.9f, -1.8f),
                new Vector3(1.6f, 1.9f, -2.0f),
                new Vector3(0.4f, 1.9f, 2.2f),
                new Vector3(-0.6f, 1.9f, -2.4f)
            };
            for (var i = 0; i < spots.Length; i++)
            {
                if (WaterSourceLayout.IsInsideReserveXZ(spots[i]))
                {
                    continue;
                }

                var cellIndex = 1000 + i;
                var plotId = MakeWildPlotId(chunkX, chunkZ, cellIndex);
                if (occupiedPlotIds.Contains(plotId))
                {
                    continue;
                }

                list.Add(new CandidateCell
                {
                    chunkX = chunkX,
                    chunkZ = chunkZ,
                    cellIndex = cellIndex,
                    stage = (byte)CellStage.Moss,
                    worldPos = spots[i]
                });
            }
        }

        private static bool TryAddPlantToChunk(
            GameSaveData save,
            int chunkX,
            int chunkZ,
            PlantSaveData plant)
        {
            ChunkSaveData chunk = null;
            foreach (var c in save.chunks)
            {
                if (c != null && c.chunkX == chunkX && c.chunkZ == chunkZ)
                {
                    chunk = c;
                    break;
                }
            }

            if (chunk == null)
            {
                chunk = new ChunkSaveData
                {
                    chunkX = chunkX,
                    chunkZ = chunkZ,
                    isUnlocked = true,
                    biomeId = "starter_island",
                    plants = new List<PlantSaveData>()
                };
                save.chunks.Add(chunk);
            }

            chunk.plants ??= new List<PlantSaveData>();
            foreach (var p in chunk.plants)
            {
                if (p != null &&
                    (p.instanceId == plant.instanceId || p.plotId == plant.plotId))
                {
                    return false;
                }
            }

            chunk.plants.Add(plant);
            return true;
        }
    }
}
