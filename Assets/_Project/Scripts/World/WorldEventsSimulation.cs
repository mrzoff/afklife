using System;
using System.Collections.Generic;
using AfkIsland.Save;

namespace AfkIsland.World
{
    /// <summary>
    /// Deterministic offline world events. Mutates stage masks / save events only.
    /// Called from WorldGrowthSimulation after edge expansion.
    /// </summary>
    public static class WorldEventsSimulation
    {
        private sealed class EventDef
        {
            public string Id;
            public string TitleRu;
            public long MinSeconds;
            public Func<GameSaveData, bool> CanFire;
            public Func<GameSaveData, DeterministicRng, long, NatureEventSaveData> Apply;
        }

        public static void Apply(
            GameSaveData save,
            long appliedSeconds,
            WorldGrowthConfig config,
            DeterministicRng rng,
            GrowthSimulationResult result)
        {
            if (save?.worldGrowth == null || config == null || rng == null || result == null)
            {
                return;
            }

            if (appliedSeconds < Math.Max(0, config.eventMinSeconds))
            {
                return;
            }

            save.worldGrowth.events ??= new List<NatureEventSaveData>();
            result.newEvents ??= new List<NatureEventSaveData>();

            var defs = BuildDefs();
            var hours = appliedSeconds / 3600d;
            var budget = Math.Min(
                config.maxEventsPerSession,
                1 + (int)Math.Floor(hours * Math.Max(0.05d, config.eventChancePerHour)));

            // Prefer longer AFK → more rolls, still capped.
            if (appliedSeconds >= 8L * 3600L)
            {
                budget = Math.Max(budget, 2);
            }

            if (appliedSeconds >= 24L * 3600L)
            {
                budget = Math.Max(budget, 3);
            }

            if (appliedSeconds >= 72L * 3600L)
            {
                budget = Math.Min(config.maxEventsPerSession, Math.Max(budget, 4));
            }

            var createdUtc = save.worldGrowth.lastSimulationUtc + appliedSeconds;
            var firedTypes = new HashSet<string>();
            foreach (var existing in save.worldGrowth.events)
            {
                if (existing == null || string.IsNullOrEmpty(existing.eventId))
                {
                    continue;
                }

                // Same event type may fire again later sessions; never re-apply same instanceId.
                if (existing.createdUtc + 6L * 3600L > createdUtc)
                {
                    firedTypes.Add(existing.eventId);
                }
            }

            var attempts = 0;
            while (result.eventsCreated < budget && attempts < defs.Count * 3)
            {
                attempts++;
                var def = defs[(int)(rng.NextUInt() % (uint)defs.Count)];
                if (appliedSeconds < def.MinSeconds)
                {
                    continue;
                }

                if (firedTypes.Contains(def.Id))
                {
                    continue;
                }

                if (!def.CanFire(save))
                {
                    continue;
                }

                // Chance gate — longer AFK → higher acceptance.
                var chance = Math.Min(0.92d, 0.35d + hours * 0.08d);
                if (rng.NextDouble() > chance)
                {
                    continue;
                }

                var evt = def.Apply(save, rng, createdUtc);
                if (evt == null || string.IsNullOrEmpty(evt.instanceId))
                {
                    continue;
                }

                if (HasInstance(save, evt.instanceId))
                {
                    continue;
                }

                evt.status = (byte)NatureEventStatus.Resolved;
                evt.rewardClaimed = false;
                save.worldGrowth.events.Add(evt);
                result.newEvents.Add(evt);
                result.eventsCreated++;
                firedTypes.Add(def.Id);
            }
        }

        private static bool HasInstance(GameSaveData save, string instanceId)
        {
            foreach (var e in save.worldGrowth.events)
            {
                if (e != null && e.instanceId == instanceId)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<EventDef> BuildDefs()
        {
            return new List<EventDef>
            {
                new EventDef
                {
                    Id = WorldEventIds.ExtraTrees,
                    TitleRu = "Проросли дополнительные деревья",
                    MinSeconds = 45L * 60L,
                    CanFire = s => CountCells(s, CellStage.Soil, false) + CountCells(s, CellStage.Moss, false) >= 4,
                    Apply = (save, rng, utc) =>
                        MutatePatch(
                            save, rng, utc,
                            WorldEventIds.ExtraTrees,
                            "Проросли дополнительные деревья",
                            "На новой земле несколько участков стали густой растительностью.",
                            from => from == (byte)CellStage.Soil || from == (byte)CellStage.Moss,
                            (byte)CellStage.Vegetation,
                            4, 8)
                },
                new EventDef
                {
                    Id = WorldEventIds.YoungPlants,
                    TitleRu = "Появилась группа молодых растений",
                    MinSeconds = 30L * 60L,
                    CanFire = s => CountCells(s, CellStage.Roots, false) >= 3,
                    Apply = (save, rng, utc) =>
                        MutatePatch(
                            save, rng, utc,
                            WorldEventIds.YoungPlants,
                            "Появилась группа молодых растений",
                            "Корни дали всходы — молодая зелень на свежей земле.",
                            from => from == (byte)CellStage.Roots,
                            (byte)CellStage.Moss,
                            3, 7)
                },
                new EventDef
                {
                    Id = WorldEventIds.MossCover,
                    TitleRu = "Новая территория покрылась мхом",
                    MinSeconds = 60L * 60L,
                    CanFire = s => CountCells(s, CellStage.Soil, false) >= 3,
                    Apply = (save, rng, utc) =>
                        MutatePatch(
                            save, rng, utc,
                            WorldEventIds.MossCover,
                            "Новая территория покрылась мхом",
                            "Влажные участки земли мягко затянуло мхом.",
                            from => from == (byte)CellStage.Soil || from == (byte)CellStage.Roots,
                            (byte)CellStage.Moss,
                            5, 10)
                },
                new EventDef
                {
                    Id = WorldEventIds.RockZone,
                    TitleRu = "Образовалась небольшая каменная зона",
                    MinSeconds = 2L * 3600L,
                    CanFire = s => CountNonVoid(s, false) >= 8,
                    Apply = (save, rng, utc) =>
                        MutatePatch(
                            save, rng, utc,
                            WorldEventIds.RockZone,
                            "Образовалась небольшая каменная зона",
                            "Часть новой земли оголилась — почва и камни у поверхности.",
                            from => from == (byte)CellStage.Moss || from == (byte)CellStage.Vegetation,
                            (byte)CellStage.Soil,
                            4, 7)
                },
                new EventDef
                {
                    Id = WorldEventIds.FertileLand,
                    TitleRu = "Появился участок плодородной земли",
                    MinSeconds = 90L * 60L,
                    CanFire = s => CountNonVoid(s, false) >= 1 || CountNonVoid(s, true) >= 16,
                    Apply = ApplyFertileLand
                },
                new EventDef
                {
                    Id = WorldEventIds.UnusualFlora,
                    TitleRu = "Возникла необычная растительность",
                    MinSeconds = 3L * 3600L,
                    CanFire = s => CountCells(s, CellStage.Moss, false) >= 2,
                    Apply = (save, rng, utc) =>
                        MutatePatch(
                            save, rng, utc,
                            WorldEventIds.UnusualFlora,
                            "Возникла необычная растительность",
                            "Мох дал место редким зарослям на краю острова.",
                            from => from == (byte)CellStage.Moss,
                            (byte)CellStage.Vegetation,
                            3, 6)
                }
            };
        }

        private static NatureEventSaveData ApplyFertileLand(GameSaveData save, DeterministicRng rng, long createdUtc)
        {
            // Bonus land: convert void frontier cells to Soil (real world growth).
            var candidates = CollectVoidFrontier(save);
            if (candidates.Count == 0)
            {
                return null;
            }

            var count = 2 + (int)(rng.NextUInt() % 4); // 2..5
            var affected = 0;
            var chunkX = 0;
            var chunkZ = 0;
            var localX = 0f;
            var localZ = 0f;

            for (var n = 0; n < count && candidates.Count > 0; n++)
            {
                var pick = candidates[(int)(rng.NextUInt() % (uint)candidates.Count)];
                candidates.Remove(pick);
                var growth = FindOrCreateGrowth(save, pick.chunkX, pick.chunkZ);
                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                if (stages[pick.cellIndex] != (byte)CellStage.Void)
                {
                    continue;
                }

                stages[pick.cellIndex] = (byte)CellStage.Soil;
                growth.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(stages);
                EnsureLogicalChunk(save, pick.chunkX, pick.chunkZ);
                affected++;
                chunkX = pick.chunkX;
                chunkZ = pick.chunkZ;
                ChunkMaskCodec.IndexToLocal(pick.cellIndex, out var lx, out var lz);
                localX = lx;
                localZ = lz;
                save.worldGrowth.totalExpandedArea += 1f;
            }

            if (affected <= 0)
            {
                return null;
            }

            return new NatureEventSaveData
            {
                eventId = WorldEventIds.FertileLand,
                instanceId = $"evt_{WorldEventIds.FertileLand}_{createdUtc}_{chunkX}_{chunkZ}_{affected}",
                createdUtc = createdUtc,
                expiresUtc = createdUtc + 7L * 24L * 3600L,
                rewardSeed = unchecked((int)(createdUtc ^ (chunkX * 31) ^ (chunkZ * 17))),
                chunkX = chunkX,
                chunkZ = chunkZ,
                localX = localX,
                localZ = localZ,
                titleRu = "Появился участок плодородной земли",
                detailRu = $"У края острова сформировалось +{affected} клеток плодородной почвы.",
                cellsAffected = affected
            };
        }

        private static NatureEventSaveData MutatePatch(
            GameSaveData save,
            DeterministicRng rng,
            long createdUtc,
            string eventId,
            string title,
            string detailBase,
            Func<byte, bool> predicate,
            byte targetStage,
            int minCount,
            int maxCount)
        {
            // Never mutate the authored legacy home island mesh mask for cosmetics.
            var cells = CollectCells(save, predicate, nonLegacyOnly: true);
            if (cells.Count == 0)
            {
                return null;
            }

            var want = minCount + (int)(rng.NextUInt() % (uint)Math.Max(1, maxCount - minCount + 1));
            want = Math.Min(want, cells.Count);
            var seedCell = cells[(int)(rng.NextUInt() % (uint)cells.Count)];

            // Grow a contiguous-ish patch from seed by picking nearby cells preferentially.
            var affected = 0;
            var queue = new List<CellRef> { seedCell };
            var used = new HashSet<long>();
            while (queue.Count > 0 && affected < want)
            {
                var idx = (int)(rng.NextUInt() % (uint)queue.Count);
                var cur = queue[idx];
                queue.RemoveAt(idx);
                var key = Pack(cur.chunkX, cur.chunkZ, cur.cellIndex);
                if (!used.Add(key))
                {
                    continue;
                }

                var growth = FindGrowth(save, cur.chunkX, cur.chunkZ);
                if (growth == null || growth.isLegacyBase)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                var stage = stages[cur.cellIndex];
                if (!predicate(stage) || stage == targetStage)
                {
                    continue;
                }

                stages[cur.cellIndex] = targetStage;
                growth.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(stages);
                affected++;

                ChunkMaskCodec.IndexToLocal(cur.cellIndex, out var lx, out var lz);
                for (var dz = -1; dz <= 1; dz++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0)
                        {
                            continue;
                        }

                        var nx = lx + dx;
                        var nz = lz + dz;
                        var cx = cur.chunkX;
                        var cz = cur.chunkZ;
                        Wrap(ref nx, ref nz, ref cx, ref cz);
                        queue.Add(new CellRef { chunkX = cx, chunkZ = cz, cellIndex = ChunkMaskCodec.CellIndex(nx, nz) });
                    }
                }
            }

            if (affected <= 0)
            {
                return null;
            }

            return new NatureEventSaveData
            {
                eventId = eventId,
                instanceId = $"evt_{eventId}_{createdUtc}_{seedCell.chunkX}_{seedCell.chunkZ}_{seedCell.cellIndex}",
                createdUtc = createdUtc,
                expiresUtc = createdUtc + 7L * 24L * 3600L,
                rewardSeed = unchecked((int)(createdUtc ^ seedCell.cellIndex * 397)),
                chunkX = seedCell.chunkX,
                chunkZ = seedCell.chunkZ,
                localX = seedCell.cellIndex % ChunkMaskCodec.GridSize,
                localZ = seedCell.cellIndex / ChunkMaskCodec.GridSize,
                titleRu = title,
                detailRu = $"{detailBase} Затронуто клеток: {affected}.",
                cellsAffected = affected
            };
        }

        private static List<CellRef> CollectCells(GameSaveData save, Func<byte, bool> predicate, bool nonLegacyOnly)
        {
            var list = new List<CellRef>();
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

                if (nonLegacyOnly && growth.isLegacyBase)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                for (var i = 0; i < stages.Length; i++)
                {
                    if (predicate(stages[i]))
                    {
                        list.Add(new CellRef { chunkX = growth.chunkX, chunkZ = growth.chunkZ, cellIndex = i });
                    }
                }
            }

            return list;
        }

        private static List<CellRef> CollectVoidFrontier(GameSaveData save)
        {
            var list = new List<CellRef>();
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
                    for (var s = 0; s < 8; s += 2)
                    {
                        var nx = lx + (s == 2 ? 1 : s == 6 ? -1 : 0);
                        var nz = lz + (s == 0 ? 1 : s == 4 ? -1 : 0);
                        if (s == 0)
                        {
                            nx = lx;
                            nz = lz + 1;
                        }
                        else if (s == 2)
                        {
                            nx = lx + 1;
                            nz = lz;
                        }
                        else if (s == 4)
                        {
                            nx = lx;
                            nz = lz - 1;
                        }
                        else
                        {
                            nx = lx - 1;
                            nz = lz;
                        }

                        var cx = growth.chunkX;
                        var cz = growth.chunkZ;
                        Wrap(ref nx, ref nz, ref cx, ref cz);
                        var neighbor = FindGrowth(save, cx, cz);
                        byte[] nStages;
                        if (neighbor == null)
                        {
                            nStages = new byte[ChunkMaskCodec.CellCount];
                        }
                        else if (!ChunkMaskCodec.TryDecodeBase64(neighbor.stageMaskBase64, out nStages, out _))
                        {
                            continue;
                        }

                        var ni = ChunkMaskCodec.CellIndex(nx, nz);
                        if (nStages[ni] == (byte)CellStage.Void)
                        {
                            list.Add(new CellRef { chunkX = cx, chunkZ = cz, cellIndex = ni });
                        }
                    }
                }
            }

            return list;
        }

        private static int CountCells(GameSaveData save, CellStage stage, bool includeLegacy)
        {
            var n = 0;
            if (save.worldGrowth?.chunks == null)
            {
                return 0;
            }

            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null || (!includeLegacy && growth.isLegacyBase))
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                for (var i = 0; i < stages.Length; i++)
                {
                    if (stages[i] == (byte)stage)
                    {
                        n++;
                    }
                }
            }

            return n;
        }

        private static int CountNonVoid(GameSaveData save, bool includeLegacy)
        {
            var n = 0;
            if (save.worldGrowth?.chunks == null)
            {
                return 0;
            }

            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null || (!includeLegacy && growth.isLegacyBase))
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                for (var i = 0; i < stages.Length; i++)
                {
                    if (stages[i] != (byte)CellStage.Void)
                    {
                        n++;
                    }
                }
            }

            return n;
        }

        private static ChunkGrowthSaveData FindGrowth(GameSaveData save, int x, int z)
        {
            foreach (var g in save.worldGrowth.chunks)
            {
                if (g != null && g.chunkX == x && g.chunkZ == z)
                {
                    return g;
                }
            }

            return null;
        }

        private static ChunkGrowthSaveData FindOrCreateGrowth(GameSaveData save, int x, int z)
        {
            var existing = FindGrowth(save, x, z);
            if (existing != null)
            {
                return existing;
            }

            var record = SaveMigrationService.CreateGrowthRecord(x, z, false);
            save.worldGrowth.chunks.Add(record);
            return record;
        }

        private static void EnsureLogicalChunk(GameSaveData save, int chunkX, int chunkZ)
        {
            save.chunks ??= new List<ChunkSaveData>();
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

        private static void Wrap(ref int lx, ref int lz, ref int chunkX, ref int chunkZ)
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

        private static long Pack(int cx, int cz, int cell) =>
            ((long)cx << 42) ^ ((long)(uint)cz << 16) ^ (uint)cell;

        private struct CellRef
        {
            public int chunkX;
            public int chunkZ;
            public int cellIndex;
        }
    }
}
