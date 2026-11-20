using System;
using System.Collections.Generic;
using AfkIsland.Save;

namespace AfkIsland.World
{
    /// <summary>
    /// Pure C# world expansion simulation. No Unity scene / GameObject / Mesh.
    /// </summary>
    public sealed class WorldGrowthSimulation
    {
        private static readonly string[] SectorNames =
        {
            "N", "NE", "E", "SE", "S", "SW", "W", "NW"
        };

        private static readonly int[] SectorDx = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] SectorDz = { 1, 1, 0, -1, -1, -1, 0, 1 };

        public GrowthSimulationResult Simulate(
            GameSaveData source,
            TimeSpan elapsed,
            WorldGrowthConfig config)
        {
            if (source == null)
            {
                return GrowthSimulationResult.Fail("null_source", "Source save is null.");
            }

            if (config == null)
            {
                return GrowthSimulationResult.Fail("null_config", "Config is null.");
            }

            if (elapsed < TimeSpan.Zero)
            {
                return GrowthSimulationResult.Fail("negative_elapsed", "Elapsed time is negative.");
            }

            if (source.worldGrowth == null)
            {
                return GrowthSimulationResult.Fail("missing_world_growth", "worldGrowth is null.");
            }

            var working = SaveClone.DeepClone(source);
            if (working == null)
            {
                return GrowthSimulationResult.Fail("clone_failed", "Failed to clone save.");
            }

            var requested = (long)Math.Floor(elapsed.TotalSeconds);
            if (requested == 0)
            {
                return new GrowthSimulationResult
                {
                    success = true,
                    updatedSave = working,
                    elapsedSeconds = 0,
                    appliedSeconds = 0
                };
            }

            var applied = Math.Min(requested, Math.Max(0, config.maxOfflineSeconds));
            var result = new GrowthSimulationResult
            {
                success = true,
                updatedSave = working,
                elapsedSeconds = requested,
                appliedSeconds = applied,
                zones = new List<ZoneGrowthReport>()
            };

            var wg = working.worldGrowth;
            var rng = new DeterministicRng(unchecked((uint)wg.growthSeed));

            AdvanceExistingCells(working, config, applied, result, rng);
            ExpandEdge(working, config, applied, result, rng);
            WorldEventsSimulation.Apply(working, applied, config, rng, result);

            wg.totalSimulatedSeconds += applied;
            wg.lastSimulationUtc += applied;
            wg.growthSeed = unchecked((int)rng.State);

            if (!ValidateWorkingCopy(working, out var error))
            {
                return GrowthSimulationResult.Fail("invariant_failed", error);
            }

            return result;
        }

        private static void AdvanceExistingCells(
            GameSaveData save,
            WorldGrowthConfig config,
            long appliedSeconds,
            GrowthSimulationResult result,
            DeterministicRng rng)
        {
            if (appliedSeconds <= 0 || save.worldGrowth?.chunks == null)
            {
                return;
            }

            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                var progressMap = BuildProgressMap(growth);
                var changed = false;
                var advanced = 0;

                for (var i = 0; i < stages.Length; i++)
                {
                    var stage = stages[i];
                    if (stage == (byte)CellStage.Void || stage >= (byte)CellStage.Vegetation)
                    {
                        continue;
                    }

                    var remaining = appliedSeconds;
                    var progress = progressMap.TryGetValue(i, out var p) ? p : 0f;

                    while (remaining > 0 && stage < (byte)CellStage.Vegetation)
                    {
                        var duration = StageDuration((CellStage)stage, config);
                        var left = (1f - progress) * duration;
                        if (remaining >= left)
                        {
                            remaining -= (long)Math.Ceiling(left);
                            progress = 0f;
                            stage++;
                            advanced++;
                            changed = true;
                        }
                        else
                        {
                            progress += remaining / (float)duration;
                            remaining = 0;
                            changed = true;
                        }
                    }

                    stages[i] = stage;
                    if (stage > 0 && stage < (byte)CellStage.Vegetation)
                    {
                        progressMap[i] = progress;
                    }
                    else
                    {
                        progressMap.Remove(i);
                    }
                }

                if (!changed)
                {
                    continue;
                }

                growth.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(stages);
                growth.cellProgress = SerializeProgress(progressMap);
                // Sticky biome evolve (Mossy→Forest etc.) — never random re-roll.
                var biomes = BiomeMaskCodec.DecodeOrEmpty(growth.biomeMaskBase64);
                for (var i = 0; i < stages.Length; i++)
                {
                    BiomeSampling.AssignCell(growth, stages, biomes, i, (wx, wz) => IsWorldShoreCell(save, wx, wz));
                }

                growth.biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biomes);
                result.cellsAdvanced += advanced;

                UpsertZone(result.zones, growth.chunkX, growth.chunkZ, "local", 0f, advanced, false);
            }
        }

        private static void ExpandEdge(
            GameSaveData save,
            WorldGrowthConfig config,
            long appliedSeconds,
            GrowthSimulationResult result,
            DeterministicRng rng)
        {
            var hours = appliedSeconds / 3600d;
            if (hours <= 0d)
            {
                return;
            }

            var a0 = Math.Max(0d, save.worldGrowth.totalExpandedArea);
            var a1 = IntegrateExpandedArea(a0, config.baseExpansionCellsPerHour, config.diminishingReturnsK, hours);
            var budget = (float)Math.Max(0d, a1 - a0);
            budget = Math.Min(budget, config.maxAreaPerSession);
            if (budget < 0.5f)
            {
                return;
            }

            EnsureWeights(save.worldGrowth, config);
            var cellsToAdd = Math.Min(config.maxCellsAdvancedPerSession, (int)Math.Floor(budget));
            var newChunks = 0;

            for (var n = 0; n < cellsToAdd; n++)
            {
                if (newChunks >= config.maxNewChunksPerSession)
                {
                    break;
                }

                var sector = PickSector(save.worldGrowth.growthSectorWeights, rng);
                if (!TryGrowOneCell(save, sector, config, rng, out var zx, out var zz, out var createdChunk))
                {
                    continue;
                }

                result.expandedArea += 1f;
                result.cellsAdvanced += 1;
                if (createdChunk)
                {
                    newChunks++;
                    result.chunksCreated++;
                }

                UpsertZone(result.zones, zx, zz, SectorNames[sector], 1f, 1, createdChunk);
                // Soft anti-monopoly: slightly reduce chosen sector weight.
                save.worldGrowth.growthSectorWeights[sector] *= 0.92f;
                NormalizeWeights(save.worldGrowth.growthSectorWeights);
            }

            save.worldGrowth.totalExpandedArea = (float)(a0 + result.expandedArea);
            save.worldGrowth.edgeGrowthProgress += result.expandedArea;
        }

        private static bool TryGrowOneCell(
            GameSaveData save,
            int sector,
            WorldGrowthConfig config,
            DeterministicRng rng,
            out int chunkX,
            out int chunkZ,
            out bool createdChunk)
        {
            chunkX = 0;
            chunkZ = 0;
            createdChunk = false;

            var candidates = CollectFrontier(save, sector);
            if (candidates.Count == 0)
            {
                candidates = CollectFrontier(save, -1);
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            var pick = candidates[(int)(rng.NextUInt() % (uint)candidates.Count)];
            chunkX = pick.chunkX;
            chunkZ = pick.chunkZ;

            var growth = FindOrCreateGrowth(save, pick.chunkX, pick.chunkZ, out createdChunk);
            if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
            {
                return false;
            }

            if (stages[pick.cellIndex] != (byte)CellStage.Void)
            {
                return false;
            }

            // No isolated cells: require a non-void 4-neighbour if not first cell in brand-new chunk.
            if (!HasLandNeighbor(save, pick.chunkX, pick.chunkZ, pick.cellIndex) &&
                CountNonVoid(stages) > 0)
            {
                return false;
            }

            stages[pick.cellIndex] = (byte)CellStage.Roots;
            growth.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(stages);
            var biomes = BiomeMaskCodec.DecodeOrEmpty(growth.biomeMaskBase64);
            BiomeSampling.AssignCell(
                growth, stages, biomes, pick.cellIndex,
                (wx, wz) => IsWorldShoreCell(save, wx, wz));
            growth.biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biomes);
            growth.cellProgress ??= new List<CellGrowthProgressSaveData>();
            growth.cellProgress.Add(new CellGrowthProgressSaveData
            {
                cellIndex = pick.cellIndex,
                progressQ16 = 0
            });

            EnsureLogicalChunk(save, pick.chunkX, pick.chunkZ);
            return true;
        }

        private static bool IsWorldShoreCell(GameSaveData save, int worldCellX, int worldCellZ)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    if (SampleStage(save, worldCellX + dx, worldCellZ + dz) == (byte)CellStage.Void)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static byte SampleStage(GameSaveData save, int worldCellX, int worldCellZ)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return (byte)CellStage.Void;
            }

            var grid = ChunkMaskCodec.GridSize;
            var chunkX = FloorDiv(worldCellX, grid);
            var chunkZ = FloorDiv(worldCellZ, grid);
            var lx = worldCellX - chunkX * grid;
            var lz = worldCellZ - chunkZ * grid;
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null || growth.chunkX != chunkX || growth.chunkZ != chunkZ)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    return (byte)CellStage.Void;
                }

                return stages[ChunkMaskCodec.CellIndex(lx, lz)];
            }

            return (byte)CellStage.Void;
        }

        private static int FloorDiv(int a, int b)
        {
            var q = a / b;
            var r = a % b;
            if (r != 0 && ((r > 0) != (b > 0)))
            {
                q--;
            }

            return q;
        }

        private static List<FrontierCell> CollectFrontier(GameSaveData save, int preferredSector)
        {
            var list = new List<FrontierCell>();
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
                    continue;
                }

                for (var i = 0; i < stages.Length; i++)
                {
                    if (stages[i] == (byte)CellStage.Void)
                    {
                        continue;
                    }

                    ChunkMaskCodec.IndexToLocal(i, out var lx, out var lz);
                    for (var s = 0; s < 8; s++)
                    {
                        if (preferredSector >= 0 && s != preferredSector)
                        {
                            continue;
                        }

                        var nx = lx + SectorDx[s];
                        var nz = lz + SectorDz[s];
                        var targetChunkX = growth.chunkX;
                        var targetChunkZ = growth.chunkZ;
                        WrapLocal(ref nx, ref nz, ref targetChunkX, ref targetChunkZ);

                        var targetGrowth = FindGrowth(save, targetChunkX, targetChunkZ);
                        byte[] targetStages;
                        if (targetGrowth == null)
                        {
                            targetStages = new byte[ChunkMaskCodec.CellCount];
                        }
                        else if (!ChunkMaskCodec.TryDecodeBase64(targetGrowth.stageMaskBase64, out targetStages, out _))
                        {
                            continue;
                        }

                        var ti = ChunkMaskCodec.CellIndex(nx, nz);
                        if (targetStages[ti] != (byte)CellStage.Void)
                        {
                            continue;
                        }

                        list.Add(new FrontierCell
                        {
                            chunkX = targetChunkX,
                            chunkZ = targetChunkZ,
                            cellIndex = ti,
                            sector = s
                        });
                    }
                }
            }

            return list;
        }

        private static void WrapLocal(ref int lx, ref int lz, ref int chunkX, ref int chunkZ)
        {
            const int g = ChunkMaskCodec.GridSize;
            while (lx < 0)
            {
                lx += g;
                chunkX--;
            }

            while (lx >= g)
            {
                lx -= g;
                chunkX++;
            }

            while (lz < 0)
            {
                lz += g;
                chunkZ--;
            }

            while (lz >= g)
            {
                lz -= g;
                chunkZ++;
            }
        }

        private static bool HasLandNeighbor(GameSaveData save, int chunkX, int chunkZ, int cellIndex)
        {
            ChunkMaskCodec.IndexToLocal(cellIndex, out var lx, out var lz);
            for (var s = 0; s < 8; s += 2) // 4-neighbour cardinals
            {
                var nx = lx + SectorDx[s];
                var nz = lz + SectorDz[s];
                var cx = chunkX;
                var cz = chunkZ;
                WrapLocal(ref nx, ref nz, ref cx, ref cz);
                var growth = FindGrowth(save, cx, cz);
                if (growth == null)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                if (stages[ChunkMaskCodec.CellIndex(nx, nz)] != (byte)CellStage.Void)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountNonVoid(byte[] stages)
        {
            var n = 0;
            foreach (var s in stages)
            {
                if (s != (byte)CellStage.Void)
                {
                    n++;
                }
            }

            return n;
        }

        private static ChunkGrowthSaveData FindOrCreateGrowth(
            GameSaveData save,
            int chunkX,
            int chunkZ,
            out bool created)
        {
            created = false;
            var existing = FindGrowth(save, chunkX, chunkZ);
            if (existing != null)
            {
                return existing;
            }

            created = true;
            var record = SaveMigrationService.CreateGrowthRecord(chunkX, chunkZ, false);
            save.worldGrowth.chunks.Add(record);
            EnsureLogicalChunk(save, chunkX, chunkZ);
            return record;
        }

        private static void EnsureLogicalChunk(GameSaveData save, int chunkX, int chunkZ)
        {
            foreach (var c in save.chunks)
            {
                if (c != null && c.chunkX == chunkX && c.chunkZ == chunkZ)
                {
                    c.isUnlocked = true;
                    return;
                }
            }

            save.chunks.Add(new ChunkSaveData
            {
                chunkX = chunkX,
                chunkZ = chunkZ,
                isUnlocked = true,
                biomeId = "wild_expansion",
                randomSeed = chunkX * 31 + chunkZ * 17,
                lastSimulatedUtc = save.worldGrowth?.lastSimulationUtc ?? 0,
                plants = new List<PlantSaveData>()
            });
        }

        private static ChunkGrowthSaveData FindGrowth(GameSaveData save, int x, int z)
        {
            if (save.worldGrowth?.chunks == null)
            {
                return null;
            }

            foreach (var g in save.worldGrowth.chunks)
            {
                if (g != null && g.chunkX == x && g.chunkZ == z)
                {
                    return g;
                }
            }

            return null;
        }

        private static int PickSector(float[] weights, DeterministicRng rng)
        {
            double sum = 0;
            for (var i = 0; i < weights.Length; i++)
            {
                sum += Math.Max(0.01f, weights[i]);
            }

            var roll = rng.NextDouble() * sum;
            double acc = 0;
            for (var i = 0; i < weights.Length; i++)
            {
                acc += Math.Max(0.01f, weights[i]);
                if (roll <= acc)
                {
                    return i;
                }
            }

            return weights.Length - 1;
        }

        private static void EnsureWeights(WorldGrowthSaveData wg, WorldGrowthConfig config)
        {
            if (wg.growthSectorWeights == null || wg.growthSectorWeights.Length != 8)
            {
                wg.growthSectorWeights = (float[])config.baseSectorWeights.Clone();
            }

            NormalizeWeights(wg.growthSectorWeights);
        }

        private static void NormalizeWeights(float[] weights)
        {
            float sum = 0;
            for (var i = 0; i < weights.Length; i++)
            {
                if (weights[i] < 0.01f)
                {
                    weights[i] = 0.01f;
                }

                sum += weights[i];
            }

            if (sum <= 0f)
            {
                return;
            }

            for (var i = 0; i < weights.Length; i++)
            {
                weights[i] /= sum;
            }
        }

        private static double IntegrateExpandedArea(double a0, double g0, double k, double hours)
        {
            if (hours <= 0d || g0 <= 0d)
            {
                return a0;
            }

            if (Math.Abs(k) < 1e-8d)
            {
                return a0 + g0 * hours;
            }

            // A + k*A^2/2  increases by G0*t
            var lhs0 = a0 + k * a0 * a0 / 2d;
            var target = lhs0 + g0 * hours;
            // Solve k/2 * A^2 + A - target = 0
            var disc = 1d + 2d * k * target;
            if (disc < 0d)
            {
                return a0 + g0 * hours;
            }

            return (-1d + Math.Sqrt(disc)) / k;
        }

        private static double StageDuration(CellStage stage, WorldGrowthConfig config)
        {
            switch (stage)
            {
                case CellStage.Roots:
                    return Math.Max(1d, config.rootsDurationSeconds);
                case CellStage.Soil:
                    return Math.Max(1d, config.soilDurationSeconds);
                case CellStage.Moss:
                    return Math.Max(1d, config.mossDurationSeconds);
                default:
                    return 1d;
            }
        }

        private static Dictionary<int, float> BuildProgressMap(ChunkGrowthSaveData growth)
        {
            var map = new Dictionary<int, float>();
            if (growth.cellProgress == null)
            {
                return map;
            }

            foreach (var entry in growth.cellProgress)
            {
                if (entry == null)
                {
                    continue;
                }

                map[entry.cellIndex] = ChunkMaskCodec.ProgressFromQ16(entry.progressQ16);
            }

            return map;
        }

        private static List<CellGrowthProgressSaveData> SerializeProgress(Dictionary<int, float> map)
        {
            var list = new List<CellGrowthProgressSaveData>();
            foreach (var kv in map)
            {
                list.Add(new CellGrowthProgressSaveData
                {
                    cellIndex = kv.Key,
                    progressQ16 = ChunkMaskCodec.ProgressToQ16(kv.Value)
                });
            }

            return list;
        }

        private static void UpsertZone(
            List<ZoneGrowthReport> zones,
            int chunkX,
            int chunkZ,
            string direction,
            float area,
            int cells,
            bool newChunk)
        {
            foreach (var zone in zones)
            {
                if (zone.chunkX == chunkX && zone.chunkZ == chunkZ && zone.direction == direction)
                {
                    zone.expandedArea += area;
                    zone.cellsAdvanced += cells;
                    zone.newChunkCreated |= newChunk;
                    return;
                }
            }

            zones.Add(new ZoneGrowthReport
            {
                chunkX = chunkX,
                chunkZ = chunkZ,
                direction = direction,
                expandedArea = area,
                cellsAdvanced = cells,
                newChunkCreated = newChunk
            });
        }

        private static bool ValidateWorkingCopy(GameSaveData save, out string error)
        {
            error = null;
            if (save.worldGrowth?.chunks == null)
            {
                error = "missing_growth_chunks";
                return false;
            }

            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out _, out var maskError))
                {
                    error = maskError;
                    return false;
                }

                var hasLogical = false;
                foreach (var logical in save.chunks)
                {
                    if (logical != null && logical.chunkX == growth.chunkX && logical.chunkZ == growth.chunkZ)
                    {
                        hasLogical = true;
                        break;
                    }
                }

                if (!hasLogical)
                {
                    error = $"growth_without_logical_{growth.chunkX}_{growth.chunkZ}";
                    return false;
                }
            }

            return true;
        }

        private struct FrontierCell
        {
            public int chunkX;
            public int chunkZ;
            public int cellIndex;
            public int sector;
        }
    }

    public sealed class DeterministicRng
    {
        public uint State { get; private set; }

        public DeterministicRng(uint seed)
        {
            State = seed == 0 ? 0xA341316Cu : seed;
        }

        public uint NextUInt()
        {
            // xorshift32
            var x = State;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            State = x;
            return x;
        }

        public double NextDouble()
        {
            return (NextUInt() & 0xFFFFFF) / (double)0x1000000;
        }
    }
}
