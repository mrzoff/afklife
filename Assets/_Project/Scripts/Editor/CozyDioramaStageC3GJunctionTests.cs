#if UNITY_EDITOR
using System.Collections.Generic;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>Deterministic mask-level regression fixtures for C-3G junction preservation.</summary>
    public static class CozyDioramaStageC3GJunctionTests
    {
        [MenuItem("AFK Island/Cozy Diorama/Stage C-3G/Run Junction Preservation Tests")]
        public static void Run()
        {
            var passed = 0;
            var failed = 0;

            RunCase("straight", 2, 7, 7, new[]
            {
                new Vector2Int(6, 7), new Vector2Int(8, 7)
            }, ref passed, ref failed);

            RunCase("90-degree bend", 2, 7, 7, new[]
            {
                new Vector2Int(8, 7), new Vector2Int(7, 8)
            }, ref passed, ref failed);

            RunCase("T-junction", 3, 7, 7, new[]
            {
                new Vector2Int(6, 7), new Vector2Int(8, 7), new Vector2Int(7, 8)
            }, ref passed, ref failed);

            RunCase("four-way crossing", 4, 7, 7, new[]
            {
                new Vector2Int(6, 7), new Vector2Int(8, 7),
                new Vector2Int(7, 6), new Vector2Int(7, 8)
            }, ref passed, ref failed);

            RunCase("diagonal-only adjacency", 0, 7, 7, new[]
            {
                new Vector2Int(6, 6), new Vector2Int(8, 8)
            }, ref passed, ref failed);

            RunSeamCase("X chunk seam T-junction", 3, 15, 7, new[]
            {
                new Vector2Int(14, 7), new Vector2Int(16, 7), new Vector2Int(15, 8)
            }, ref passed, ref failed);

            RunSeamCase("X/Z corner seam four-way", 4, 15, 15, new[]
            {
                new Vector2Int(14, 15), new Vector2Int(16, 15),
                new Vector2Int(15, 14), new Vector2Int(15, 16)
            }, ref passed, ref failed);

            RunTypedRoadCase("Stone is road; Hut is door-branch only", ref passed, ref failed);

            var summary = $"[C-3G] Junction tests: passed={passed}, failed={failed}";
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary);
        }

        private static void RunCase(string name, int expectedDegree, int centerX, int centerZ,
            Vector2Int[] neighbors, ref int passed, ref int failed)
        {
            var fixture = new MaskFixture();
            fixture.Set(centerX, centerZ, SettlementStage.Path);
            for (var i = 0; i < neighbors.Length; i++)
                fixture.Set(neighbors[i].x, neighbors[i].y, SettlementStage.Path);
            AssertDegree(name, fixture.Save, centerX, centerZ, expectedDegree, ref passed, ref failed);
        }

        private static void RunSeamCase(string name, int expectedDegree, int centerX, int centerZ,
            Vector2Int[] neighbors, ref int passed, ref int failed)
        {
            // The fixture maps world cells into separate 16x16 chunk masks, so X/Z=16
            // neighbours are read from adjacent chunks through SampleSettlementStage.
            RunCase(name, expectedDegree, centerX, centerZ, neighbors, ref passed, ref failed);
        }

        private static void RunTypedRoadCase(string name, ref int passed, ref int failed)
        {
            var fixture = new MaskFixture();
            fixture.Set(7, 7, SettlementStage.Path);
            fixture.Set(6, 7, SettlementStage.Stone);
            fixture.Set(8, 7, SettlementStage.Hut);
            fixture.Set(7, 6, SettlementStage.Clearing);
            fixture.Set(7, 8, SettlementStage.Path);
            AssertDegree(name, fixture.Save, 7, 7, 2, ref passed, ref failed);
        }

        private static void AssertDegree(string name, GameSaveData save, int worldX, int worldZ,
            int expected, ref int passed, ref int failed)
        {
            var chunkX = FloorDiv(worldX, ChunkMaskCodec.GridSize);
            var chunkZ = FloorDiv(worldZ, ChunkMaskCodec.GridSize);
            var localX = worldX - chunkX * ChunkMaskCodec.GridSize;
            var localZ = worldZ - chunkZ * ChunkMaskCodec.GridSize;
            var cell = ChunkMaskCodec.CellIndex(localX, localZ);

            var actual = SettlementSimulation.CountCardinalRoadNeighbors(save, chunkX, chunkZ, cell);
            var expectedJunction = expected >= 3;
            var actualJunction = SettlementSimulation.IsRoadJunctionCell(save, chunkX, chunkZ, cell);
            if (actual == expected && actualJunction == expectedJunction)
            {
                Debug.Log($"[C-3G] PASS {name}: degree={actual}, junction={actualJunction}");
                passed++;
            }
            else
            {
                Debug.LogError($"[C-3G] FAIL {name}: expected degree={expected}/junction={expectedJunction}, actual degree={actual}/junction={actualJunction}");
                failed++;
            }
        }

        private sealed class MaskFixture
        {
            private readonly Dictionary<long, byte[]> _masks = new Dictionary<long, byte[]>();
            public readonly GameSaveData Save = new GameSaveData
            {
                version = 8,
                worldGrowth = new WorldGrowthSaveData()
            };

            public void Set(int worldX, int worldZ, SettlementStage stage)
            {
                var chunkX = FloorDiv(worldX, ChunkMaskCodec.GridSize);
                var chunkZ = FloorDiv(worldZ, ChunkMaskCodec.GridSize);
                var localX = worldX - chunkX * ChunkMaskCodec.GridSize;
                var localZ = worldZ - chunkZ * ChunkMaskCodec.GridSize;
                var key = ((long)chunkX << 32) ^ (uint)chunkZ;

                if (!_masks.TryGetValue(key, out var mask))
                {
                    mask = new byte[ChunkMaskCodec.CellCount];
                    _masks.Add(key, mask);
                    Save.worldGrowth.chunks.Add(new ChunkGrowthSaveData
                    {
                        chunkX = chunkX,
                        chunkZ = chunkZ,
                        settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(mask)
                    });
                }

                mask[ChunkMaskCodec.CellIndex(localX, localZ)] = (byte)stage;
                var growth = FindChunk(chunkX, chunkZ);
                growth.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(mask);
            }

            private ChunkGrowthSaveData FindChunk(int x, int z)
            {
                for (var i = 0; i < Save.worldGrowth.chunks.Count; i++)
                {
                    var chunk = Save.worldGrowth.chunks[i];
                    if (chunk.chunkX == x && chunk.chunkZ == z) return chunk;
                }

                return null;
            }

            private static int FloorDiv(int value, int divisor)
            {
                var q = value / divisor;
                var r = value % divisor;
                return r != 0 && value < 0 ? q - 1 : q;
            }
        }
    }
}
#endif
