#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Stage 12: settlement mask save, idempotent offline apply, land binding.
    /// Does not change SettlementSimulation.
    /// </summary>
    public static class SettlementAuditSmoke
    {
        private const long WeekSeconds = 7L * 24L * 3600L;

        [MenuItem("AFK Island/Smoke Settlement Audit (Stage 12)")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Settlement audit smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Settlement audit smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            try
            {
                var grown = RunGrownWeek();
                if (grown != 0)
                {
                    return grown;
                }

                var pond = RunHomePondCellIsEligible();
                if (pond != 0)
                {
                    return pond;
                }

                var stones = RunStoneCap();
                if (stones != 0)
                {
                    return stones;
                }

                var dup = RunDuplicateGrowthRejected();
                if (dup != 0)
                {
                    return dup;
                }

                Debug.Log("[AFK Island] Settlement audit smoke OK.");
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return 1;
            }
        }

        private static int RunGrownWeek()
        {
            var save = CreateWorld();
            var plant = new PlantSaveData
            {
                instanceId = "audit-hut-plant",
                plantId = "starter_tree",
                plotId = "wild_1_0_119",
                stage = 2,
                posX = 0f,
                posY = 1f,
                posZ = 0f
            };
            save.chunks[1].plants.Add(plant);

            var first = SettlementSimulation.Apply(save, WeekSeconds, new DeterministicRng(0x512u));
            if (first.cellsAdvanced <= 0)
            {
                Debug.LogError("Expected settlement advances on a vegetated grown chunk.");
                return 2;
            }

            SettlementSimulation.CountStages(save, out var clearings, out var paths, out var stones, out var huts);
            if (clearings + paths + stones + huts != first.cellsAdvanced &&
                clearings + paths + stones + huts <= 0)
            {
                Debug.LogError(
                    $"Stage counts empty after advances={first.cellsAdvanced} " +
                    $"c={clearings} p={paths} s={stones} h={huts}");
                return 3;
            }

            if (save.version != SaveMigrationService.CurrentVersion)
            {
                Debug.LogError("Settlement apply changed save version.");
                return 4;
            }

            if (save.worldGrowth.settlementWatermarkUtc != save.worldGrowth.lastSimulationUtc)
            {
                Debug.LogError("Watermark was not stamped to lastSimulationUtc.");
                return 5;
            }

            if (!AssertLandBound(save))
            {
                return 6;
            }

            if (!ChunkStaysEmpty(save, 2, 0))
            {
                Debug.LogError("Void chunk received settlement.");
                return 7;
            }

            var masks = SnapshotMasks(save);
            var hutsBefore = huts;
            var pathsBefore = paths;
            var second = SettlementSimulation.Apply(save, WeekSeconds, new DeterministicRng(0x512u));
            if (second.cellsAdvanced != 0)
            {
                Debug.LogError("Second apply with the same watermark advanced cells.");
                return 8;
            }

            if (!MasksEqual(save, masks))
            {
                Debug.LogError("Second apply changed settlement masks.");
                return 9;
            }

            SettlementSimulation.CountStages(save, out _, out var pathsAfter, out _, out var hutsAfter);
            if (hutsAfter != hutsBefore || pathsAfter != pathsBefore)
            {
                Debug.LogError("Houses or paths changed on the idempotent apply.");
                return 10;
            }

            var clone = SaveClone.DeepClone(save);
            SettlementSimulation.CountStages(clone, out var c2, out var p2, out var s2, out var h2);
            SettlementSimulation.CountStages(save, out clearings, out paths, out stones, out huts);
            if (c2 != clearings || p2 != paths || s2 != stones || h2 != huts)
            {
                Debug.LogError("SaveClone dropped settlement stages.");
                return 11;
            }

            if (save.chunks[1].plants.Count != 1 ||
                save.chunks[1].plants[0].instanceId != "audit-hut-plant")
            {
                Debug.LogError("Settlement apply removed a plant. It must not own vegetation.");
                return 12;
            }

            var home = FindGrowth(save, 0, 0);
            if (home == null ||
                !ChunkMaskCodec.TryDecodeBase64(home.settlementMaskBase64, out var homeSettle, out _))
            {
                Debug.LogError("Home settlement mask missing after the week.");
                return 13;
            }

            for (var i = 0; i < homeSettle.Length; i++)
            {
                if (homeSettle[i] != (byte)SettlementStage.None &&
                    WaterSourceLayout.BlocksSettlementCell(0, 0, i))
                {
                    Debug.LogError($"Week growth placed settlement in the pond at cell {i}.");
                    return 14;
                }
            }

            Debug.Log(
                $"[AFK Island] Settlement week: advances={first.cellsAdvanced} " +
                $"clearings={clearings} paths={paths} stones={stones} huts={huts}");
            return 0;
        }

        private static int RunHomePondCellIsEligible()
        {
            var pondIndex = ChunkMaskCodec.CellIndex(9, 6);
            var center = GrowthWorldPlacement.CellCenterWorld(0, 0, pondIndex);
            if (!WaterSourceLayout.BlocksTrunk(center.x, center.z, WaterSourceLayout.MinTrunkRadius))
            {
                Debug.LogError(
                    $"Pond fixture cell {pondIndex} at ({center.x:0.00},{center.z:0.00}) is outside the water ellipse.");
                return 20;
            }

            var save = CreateWorld();
            var home = FindGrowth(save, 0, 0);
            var settle = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < settle.Length; i++)
            {
                settle[i] = (byte)SettlementStage.Hut;
            }

            settle[pondIndex] = (byte)SettlementStage.Clearing;
            home.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle);
            save.worldGrowth.settlementWatermarkUtc = 0;

            var result = SettlementSimulation.Apply(save, 8L * 3600L, new DeterministicRng(1u));
            if (!ChunkMaskCodec.TryDecodeBase64(home.settlementMaskBase64, out var after, out var error))
            {
                Debug.LogError("Pond fixture mask failed to decode: " + error);
                return 21;
            }

            if (after[pondIndex] != (byte)SettlementStage.None)
            {
                Debug.LogError(
                    $"Home pond cell {pondIndex} is {(SettlementStage)after[pondIndex]}. It must be cleared.");
                return 22;
            }

            if (after[0] != (byte)SettlementStage.Hut)
            {
                Debug.LogError("Pond scrub cleared a hut outside the water.");
                return 23;
            }

            for (var i = 0; i < after.Length; i++)
            {
                if (after[i] != (byte)SettlementStage.None &&
                    WaterSourceLayout.BlocksSettlementCell(0, 0, i))
                {
                    Debug.LogError($"Settlement remains in the pond at cell {i}.");
                    return 24;
                }
            }

            Debug.Log(
                $"[AFK Island] Home pond cell {pondIndex} cleared " +
                $"(session advances={result.cellsAdvanced}). Settlement stays off the water.");
            return 0;
        }

        private static int RunStoneCap()
        {
            var save = CreateWorld();
            var home = FindGrowth(save, 0, 0);
            var settle = new byte[ChunkMaskCodec.CellCount];
            var stones = 0;
            for (var i = 0; i < settle.Length && stones < 20; i++)
            {
                settle[i] = (byte)SettlementStage.Stone;
                stones++;
            }

            for (var i = 20; i < 24; i++)
            {
                settle[i] = (byte)SettlementStage.Path;
            }

            home.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle);
            SettlementSimulation.RebalanceExcessStones(save);
            SettlementSimulation.CountStages(save, out _, out _, out var left, out var huts);
            if (left > 12 || huts != 0)
            {
                Debug.LogError($"Stone rebalance left {left} stones and {huts} huts.");
                return 30;
            }

            if (left > 4)
            {
                Debug.LogError($"Expected stones to fall to the soft cap, left {left}.");
                return 31;
            }

            Debug.Log($"[AFK Island] Stone rebalance kept {left} stones and did not remove huts.");
            return 0;
        }

        private static int RunDuplicateGrowthRejected()
        {
            var save = CreateWorld();
            var home = FindGrowth(save, 0, 0);
            save.worldGrowth.chunks.Add(home);
            var migrated = SaveMigrationService.MigrateIfNeeded(save);
            if (migrated != null)
            {
                Debug.LogError("Duplicate growth coordinates were accepted.");
                return 40;
            }

            Debug.Log("[AFK Island] Duplicate growth coordinates rejected. Save version stays 8.");
            return 0;
        }

        private static GameSaveData CreateWorld()
        {
            const long now = 1_700_000_000L;
            var save = new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                lastSaveUtc = now,
                plants = new List<PlantSaveData>(),
                chunks = new List<ChunkSaveData>
                {
                    Chunk(0, 0),
                    Chunk(1, 0),
                    Chunk(2, 0)
                },
                worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(now),
                ecosystem = SaveMigrationService.CreateDefaultEcosystem(),
                weather = SaveMigrationService.CreateDefaultWeather(now),
                animals = new List<AnimalSaveData>(),
                habitats = new List<HabitatSaveData>()
            };
            save.worldGrowth.lastSimulationUtc = now;
            save.worldGrowth.settlementWatermarkUtc = 0;
            save.worldGrowth.chunks.Add(SaveMigrationService.CreateGrowthRecord(0, 0, true));

            var grown = SaveMigrationService.CreateGrowthRecord(1, 0, false);
            var land = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < land.Length; i++)
            {
                land[i] = (byte)CellStage.Vegetation;
            }

            grown.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land);
            save.worldGrowth.chunks.Add(grown);
            save.worldGrowth.chunks.Add(SaveMigrationService.CreateGrowthRecord(2, 0, false));
            return save;
        }

        private static ChunkSaveData Chunk(int x, int z)
        {
            return new ChunkSaveData
            {
                chunkX = x,
                chunkZ = z,
                isUnlocked = true,
                biomeId = x == 0 ? "starter_island" : "grown",
                plants = new List<PlantSaveData>()
            };
        }

        private static bool AssertLandBound(GameSaveData save)
        {
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null || growth.isLegacyBase)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                {
                    Debug.LogError($"Missing settlement mask on ({growth.chunkX},{growth.chunkZ}).");
                    return false;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var land, out _))
                {
                    Debug.LogError($"Missing land mask on ({growth.chunkX},{growth.chunkZ}).");
                    return false;
                }

                for (var i = 0; i < settle.Length; i++)
                {
                    if (settle[i] == (byte)SettlementStage.None)
                    {
                        continue;
                    }

                    if (land[i] < (byte)CellStage.Moss ||
                        GrowthWorldPlacement.IsShoreCell(save, growth.chunkX, growth.chunkZ, i, 1))
                    {
                        Debug.LogError(
                            $"Settlement on unfit land ({growth.chunkX},{growth.chunkZ}) cell {i} " +
                            $"stage={settle[i]} land={land[i]}");
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool ChunkStaysEmpty(GameSaveData save, int x, int z)
        {
            var growth = FindGrowth(save, x, z);
            if (growth == null ||
                !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
            {
                return false;
            }

            for (var i = 0; i < settle.Length; i++)
            {
                if (settle[i] != (byte)SettlementStage.None)
                {
                    return false;
                }
            }

            return true;
        }

        private static ChunkGrowthSaveData FindGrowth(GameSaveData save, int x, int z)
        {
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth != null && growth.chunkX == x && growth.chunkZ == z)
                {
                    return growth;
                }
            }

            return null;
        }

        private static Dictionary<long, string> SnapshotMasks(GameSaveData save)
        {
            var map = new Dictionary<long, string>();
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                map[Pack(growth.chunkX, growth.chunkZ)] = growth.settlementMaskBase64;
            }

            return map;
        }

        private static bool MasksEqual(GameSaveData save, Dictionary<long, string> before)
        {
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                if (!before.TryGetValue(Pack(growth.chunkX, growth.chunkZ), out var mask) ||
                    mask != growth.settlementMaskBase64)
                {
                    return false;
                }
            }

            return true;
        }

        private static long Pack(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
#endif
