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
            RunAll(out _, out _);
        }

        public static bool RunAll(out int passed, out int failed)
        {
            passed = 0;
            failed = 0;

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

            // C-3G.2 Bridge / articulation-vertex connectivity guard tests
            RunConnectivityCase(
                "straight corridor without loop is protected cut-vertex",
                7, 7,
                new[] { new Vector2Int(6, 7), new Vector2Int(7, 7), new Vector2Int(8, 7) },
                expectedDisconnects: true,
                expectedProtected: true,
                ref passed, ref failed);

            RunConnectivityCase(
                "cross-chunk bridge at X seam is protected cut-vertex",
                15, 7,
                new[] { new Vector2Int(14, 7), new Vector2Int(15, 7), new Vector2Int(16, 7), new Vector2Int(17, 7) },
                expectedDisconnects: true,
                expectedProtected: true,
                ref passed, ref failed);

            RunConnectivityCase(
                "dead-end terminal road cell remains eligible (ordinary cell)",
                7, 7,
                new[] { new Vector2Int(6, 7), new Vector2Int(7, 7) },
                expectedDisconnects: false,
                expectedProtected: false,
                ref passed, ref failed);

            RunConnectivityCase(
                "straight cell on closed loop remains eligible (alternate route exists)",
                7, 6,
                new[]
                {
                    new Vector2Int(6, 6), new Vector2Int(7, 6), new Vector2Int(8, 6),
                    new Vector2Int(6, 7),                       new Vector2Int(8, 7),
                    new Vector2Int(6, 8), new Vector2Int(7, 8), new Vector2Int(8, 8)
                },
                expectedDisconnects: false,
                expectedProtected: false,
                ref passed, ref failed);

            RunOpenedLoopCase(
                "second cut on opened loop becomes protected cut-vertex",
                ref passed, ref failed);

            RunConnectivityCase(
                "four-way junction inside dense 3x3 block stays protected",
                7, 7,
                new[]
                {
                    new Vector2Int(6, 6), new Vector2Int(7, 6), new Vector2Int(8, 6),
                    new Vector2Int(6, 7), new Vector2Int(7, 7), new Vector2Int(8, 7),
                    new Vector2Int(6, 8), new Vector2Int(7, 8), new Vector2Int(8, 8)
                },
                expectedDisconnects: false,
                expectedProtected: true,
                ref passed, ref failed);

            RunEndToEndApplyCase(
                "end-to-end Apply grows hut on non-critical spur without breaking bridge or junctions",
                ref passed, ref failed);

            var summary = $"[C-3G] Junction & bridge tests: passed={passed}, failed={failed}";
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary);
            return failed == 0;
        }

        private static void RunConnectivityCase(
            string name,
            int targetX,
            int targetZ,
            Vector2Int[] roadCells,
            bool expectedDisconnects,
            bool expectedProtected,
            ref int passed,
            ref int failed)
        {
            var fixture = new MaskFixture();
            for (var i = 0; i < roadCells.Length; i++)
            {
                fixture.Set(roadCells[i].x, roadCells[i].y, SettlementStage.Path);
            }

            AssertConnectivity(name, fixture.Save, targetX, targetZ, expectedDisconnects, expectedProtected, ref passed, ref failed);
        }

        private static void RunOpenedLoopCase(string name, ref int passed, ref int failed)
        {
            var fixture = new MaskFixture();
            var ring = new[]
            {
                new Vector2Int(6, 6), new Vector2Int(7, 6), new Vector2Int(8, 6),
                new Vector2Int(6, 7),                       new Vector2Int(8, 7),
                new Vector2Int(6, 8), new Vector2Int(7, 8), new Vector2Int(8, 8)
            };
            for (var i = 0; i < ring.Length; i++)
            {
                fixture.Set(ring[i].x, ring[i].y, SettlementStage.Path);
            }

            // Simulate first Path -> Hut opening the ring at (7, 6)
            fixture.Set(7, 6, SettlementStage.Hut);
            // Opposite cell (7, 8) is now a cut-vertex on the opened ring and must be protected.
            AssertConnectivity(name, fixture.Save, 7, 8, expectedDisconnects: true, expectedProtected: true, ref passed, ref failed);
        }

        private static void RunEndToEndApplyCase(string name, ref int passed, ref int failed)
        {
            // Grown chunk (1,0) filled with Vegetation land + a corridor with a T-junction,
            // a 4-way crossing, a straight bridge between them, and dead-end spurs clear of existing huts.
            var land = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < land.Length; i++) land[i] = (byte)CellStage.Vegetation;
            var biome = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < biome.Length; i++) biome[i] = (byte)BiomeType.Mossy;
            var settle = new byte[ChunkMaskCodec.CellCount];

            // Horizontal spine x=3..12, z=8
            for (var x = 3; x <= 12; x++) settle[ChunkMaskCodec.CellIndex(x, 8)] = (byte)SettlementStage.Path;
            // T-junction at (5,8) with south spur (5,7)..(5,4)
            for (var z = 4; z <= 7; z++) settle[ChunkMaskCodec.CellIndex(5, z)] = (byte)SettlementStage.Path;
            // 4-way crossing at (10,8) with north/south spurs (10,5)..(10,11)
            for (var z = 5; z <= 11; z++) settle[ChunkMaskCodec.CellIndex(10, z)] = (byte)SettlementStage.Path;

            var save = new GameSaveData
            {
                version = 8,
                lastSaveUtc = 1760000000L,
                worldGrowth = new WorldGrowthSaveData
                {
                    growthSeed = 424242,
                    lastSimulationUtc = 1760000000L + 7 * 86400L,
                    settlementWatermarkUtc = 1760000000L,
                    chunks = new List<ChunkGrowthSaveData>
                    {
                        new ChunkGrowthSaveData
                        {
                            chunkX = 1, chunkZ = 0, isLegacyBase = false, growthSeed = 7777,
                            stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land),
                            biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biome),
                            settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle)
                        }
                    }
                }
            };
            save.chunks.Add(new ChunkSaveData { chunkX = 1, chunkZ = 0, isUnlocked = true, biomeId = "mossy" });

            var protectedCoords = new[]
            {
                new Vector2Int(5, 8),  // T-junction
                new Vector2Int(10, 8), // 4-way crossing
                new Vector2Int(6, 8),  // straight bridge between junctions
                new Vector2Int(7, 8),
                new Vector2Int(8, 8),
                new Vector2Int(9, 8)
            };

            var rng = new DeterministicRng(1234567u);
            var res = SettlementSimulation.Apply(save, 7 * 86400L, rng);

            var allProtectedIntact = true;
            for (var i = 0; i < protectedCoords.Length; i++)
            {
                var p = protectedCoords[i];
                var st = SettlementSimulation.SampleSettlementStage(save, 16 + p.x, p.y);
                if (st != (byte)SettlementStage.Path && st != (byte)SettlementStage.Stone)
                {
                    allProtectedIntact = false;
                }
            }

            // Count connected components over all Path/Stone cells in chunk (1,0)
            ChunkMaskCodec.TryDecodeBase64(save.worldGrowth.chunks[0].settlementMaskBase64, out var afterMask, out _);
            var roadSet = new HashSet<int>();
            for (var i = 0; i < afterMask.Length; i++)
            {
                if (afterMask[i] == (byte)SettlementStage.Path || afterMask[i] == (byte)SettlementStage.Stone)
                    roadSet.Add(i);
            }
            var visited = new HashSet<int>();
            var components = 0;
            foreach (var start in roadSet)
            {
                if (!visited.Add(start)) continue;
                components++;
                var q = new Queue<int>();
                q.Enqueue(start);
                while (q.Count > 0)
                {
                    var cur = q.Dequeue();
                    ChunkMaskCodec.IndexToLocal(cur, out var lx, out var lz);
                    TryVisit(lx - 1, lz, roadSet, visited, q);
                    TryVisit(lx + 1, lz, roadSet, visited, q);
                    TryVisit(lx, lz - 1, roadSet, visited, q);
                    TryVisit(lx, lz + 1, roadSet, visited, q);
                }
            }

            if (res.hutsAdded >= 1 && allProtectedIntact && components == 1)
            {
                Debug.Log($"[C-3G] PASS {name}: hutsAdded={res.hutsAdded}, protectedIntact=True, roadComponents={components}");
                passed++;
            }
            else
            {
                Debug.LogError($"[C-3G] FAIL {name}: hutsAdded={res.hutsAdded}, protectedIntact={allProtectedIntact}, roadComponents={components}");
                failed++;
            }
        }

        private static void TryVisit(int lx, int lz, HashSet<int> roadSet, HashSet<int> visited, Queue<int> q)
        {
            if (lx < 0 || lz < 0 || lx >= ChunkMaskCodec.GridSize || lz >= ChunkMaskCodec.GridSize) return;
            var idx = ChunkMaskCodec.CellIndex(lx, lz);
            if (roadSet.Contains(idx) && visited.Add(idx)) q.Enqueue(idx);
        }

        private static void AssertConnectivity(
            string name,
            GameSaveData save,
            int worldX,
            int worldZ,
            bool expectedDisconnects,
            bool expectedProtected,
            ref int passed,
            ref int failed)
        {
            var chunkX = FloorDiv(worldX, ChunkMaskCodec.GridSize);
            var chunkZ = FloorDiv(worldZ, ChunkMaskCodec.GridSize);
            var localX = worldX - chunkX * ChunkMaskCodec.GridSize;
            var localZ = worldZ - chunkZ * ChunkMaskCodec.GridSize;
            var cell = ChunkMaskCodec.CellIndex(localX, localZ);

            var actualDisconnects = SettlementSimulation.WouldDisconnectRoadNetwork(save, chunkX, chunkZ, cell);
            var actualProtected = SettlementSimulation.IsProtectedRoadCell(save, chunkX, chunkZ, cell);
            if (actualDisconnects == expectedDisconnects && actualProtected == expectedProtected)
            {
                Debug.Log($"[C-3G] PASS {name}: disconnects={actualDisconnects}, protected={actualProtected}");
                passed++;
            }
            else
            {
                Debug.LogError($"[C-3G] FAIL {name}: expected disconnects={expectedDisconnects}/protected={expectedProtected}, actual disconnects={actualDisconnects}/protected={actualProtected}");
                failed++;
            }
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
        }

        private static int FloorDiv(int value, int divisor)
        {
            var q = value / divisor;
            var r = value % divisor;
            return r != 0 && value < 0 ? q - 1 : q;
        }
    }
}
#endif
