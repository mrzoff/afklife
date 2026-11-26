#if UNITY_EDITOR
using System.Collections.Generic;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Regression tests for preventing newly-created isolated Path cells.
    /// Uses only in-memory Save v8 fixtures; never reads or writes the real save.
    /// </summary>
    public static class CozyDioramaStageC3HRoadConnectivityTests
    {
        [MenuItem("AFK Island/Cozy Diorama/Stage C-3H/Run Road Connectivity Tests")]
        public static void Run()
        {
            RunAll(out _, out _);
        }

        public static bool RunAll(out int passed, out int failed)
        {
            passed = 0;
            failed = 0;

            RunApplyCase("isolated Clearing cannot become Path", false, false, ref passed, ref failed);
            RunApplyCase("diagonal-only road does not connect Path", true, false, ref passed, ref failed);
            RunApplyCase("cardinal Path neighbour permits Path", false, true, ref passed, ref failed);
            RunApplyCase("cardinal Stone neighbour permits Path", false, true, ref passed, ref failed);
            RunSeamCase("cardinal neighbour across chunk seam permits Path", ref passed, ref failed);

            var summary = $"[C-3H] Road connectivity tests: passed={passed}, failed={failed}";
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary);
            return failed == 0;
        }

        private static void RunApplyCase(
            string name,
            bool diagonalRoad,
            bool cardinalRoad,
            ref int passed,
            ref int failed)
        {
            const int chunkX = 20;
            const int chunkZ = 20;
            const int targetX = 7;
            const int targetZ = 7;
            var save = CreateFixture(chunkX, chunkZ, out var growth, out var land, out var settle);

            // Small interior Moss patch keeps the target away from the shoreline while
            // leaving every other cell non-buildable for a deterministic Apply fixture.
            for (var z = 5; z <= 9; z++)
            {
                for (var x = 5; x <= 9; x++)
                {
                    var idx = ChunkMaskCodec.CellIndex(x, z);
                    land[idx] = (byte)CellStage.Moss;
                    settle[idx] = (byte)SettlementStage.Clearing;
                }
            }

            var target = ChunkMaskCodec.CellIndex(targetX, targetZ);
            settle[target] = (byte)SettlementStage.Clearing;

            if (cardinalRoad)
            {
                settle[ChunkMaskCodec.CellIndex(targetX - 1, targetZ)] =
                    (byte)SettlementStage.Path;
            }
            else if (diagonalRoad)
            {
                settle[ChunkMaskCodec.CellIndex(targetX - 1, targetZ - 1)] =
                    (byte)SettlementStage.Path;
            }

            growth.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land);
            growth.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle);

            var targetWorldX = chunkX * ChunkMaskCodec.GridSize + targetX;
            var targetWorldZ = chunkZ * ChunkMaskCodec.GridSize + targetZ;
            var targetIndex = ChunkMaskCodec.CellIndex(targetX, targetZ);
            var hasNeighbor = SettlementSimulation.HasCardinalRoadNeighbor(
                save, chunkX, chunkZ, targetIndex);

            var result = SettlementSimulation.Apply(save, 7 * 86400L, new DeterministicRng(123456u));
            var actual = SettlementSimulation.SampleSettlementStage(save, targetWorldX, targetWorldZ);
            var expectedPath = cardinalRoad;
            var ok = hasNeighbor == expectedPath &&
                     (actual == (byte)SettlementStage.Path) == expectedPath;

            Log(name, ok,
                $"cardinalNeighbor={hasNeighbor}, targetStage={(SettlementStage)actual}, " +
                $"pathsAdded={result.pathsAdded}, expectedPath={expectedPath}",
                ref passed, ref failed);
        }

        private static void RunSeamCase(string name, ref int passed, ref int failed)
        {
            const int leftChunkX = 20;
            const int chunkZ = 20;
            var save = new GameSaveData
            {
                version = 8,
                worldGrowth = new WorldGrowthSaveData
                {
                    lastSimulationUtc = 1760000000L,
                    settlementWatermarkUtc = 0,
                    chunks = new List<ChunkGrowthSaveData>()
                }
            };

            var leftMask = new byte[ChunkMaskCodec.CellCount];
            leftMask[ChunkMaskCodec.CellIndex(15, 7)] = (byte)SettlementStage.Stone;
            var rightMask = new byte[ChunkMaskCodec.CellCount];
            rightMask[ChunkMaskCodec.CellIndex(0, 7)] = (byte)SettlementStage.Clearing;

            var left = CreateGrowth(leftChunkX, chunkZ, leftMask);
            var right = CreateGrowth(leftChunkX + 1, chunkZ, rightMask);
            save.worldGrowth.chunks.Add(left);
            save.worldGrowth.chunks.Add(right);

            var targetIndex = ChunkMaskCodec.CellIndex(0, 7);
            var hasNeighbor = SettlementSimulation.HasCardinalRoadNeighbor(
                save, leftChunkX + 1, chunkZ, targetIndex);

            var ok = hasNeighbor;
            Log(name, ok, $"cardinalNeighbor={hasNeighbor}", ref passed, ref failed);
        }

        private static GameSaveData CreateFixture(
            int chunkX,
            int chunkZ,
            out ChunkGrowthSaveData growth,
            out byte[] land,
            out byte[] settle)
        {
            land = new byte[ChunkMaskCodec.CellCount];
            settle = new byte[ChunkMaskCodec.CellCount];
            var biome = new byte[ChunkMaskCodec.CellCount];

            growth = new ChunkGrowthSaveData
            {
                chunkX = chunkX,
                chunkZ = chunkZ,
                isLegacyBase = false,
                growthSeed = 1234,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biome),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle)
            };

            var save = new GameSaveData
            {
                version = 8,
                lastSaveUtc = 1760000000L,
                worldGrowth = new WorldGrowthSaveData
                {
                    growthSeed = 1234,
                    lastSimulationUtc = 1760000000L,
                    settlementWatermarkUtc = 0,
                    chunks = new List<ChunkGrowthSaveData> { growth }
                }
            };
            save.chunks.Add(new ChunkSaveData
            {
                chunkX = chunkX,
                chunkZ = chunkZ,
                isUnlocked = true,
                biomeId = "mossy"
            });
            return save;
        }

        private static ChunkGrowthSaveData CreateGrowth(int chunkX, int chunkZ, byte[] settle)
        {
            var land = new byte[ChunkMaskCodec.CellCount];
            var biome = new byte[ChunkMaskCodec.CellCount];
            return new ChunkGrowthSaveData
            {
                chunkX = chunkX,
                chunkZ = chunkZ,
                isLegacyBase = false,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biome),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle)
            };
        }

        private static void Log(
            string name,
            bool ok,
            string details,
            ref int passed,
            ref int failed)
        {
            if (ok)
            {
                Debug.Log($"[C-3H] PASS {name}: {details}");
                passed++;
            }
            else
            {
                Debug.LogError($"[C-3H] FAIL {name}: {details}");
                failed++;
            }
        }
    }
}
#endif
