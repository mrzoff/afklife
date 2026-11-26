#if UNITY_EDITOR
using System.Collections.Generic;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Regression tests for road connectivity during settlement growth.
    /// Rule under test: a Clearing may become Path only when it touches an existing cardinal Path/Stone
    /// cell, so growth extends a connected network and never creates isolated road fragments.
    /// Assertions are made on the FINAL topology after SettlementSimulation.Apply (growth inside one pass
    /// may legally chain from the seed), not on the pre-Apply neighbourhood alone.
    /// Uses only in-memory Save v8 fixtures; never reads or writes the real save.
    /// </summary>
    public static class CozyDioramaStageC3HRoadConnectivityTests
    {
        private const int Grid = ChunkMaskCodec.GridSize;

        private enum Seed
        {
            None,
            DiagonalPath,
            CardinalPath,
            CardinalStone
        }

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3H/Run Road Connectivity Tests")]
        public static void Run()
        {
            RunAll(out _, out _);
        }

        public static bool RunAll(out int passed, out int failed)
        {
            passed = 0;
            failed = 0;

            RunApplyCase("isolated Clearing cannot become Path (no roads at all)", Seed.None, ref passed, ref failed);
            RunEmptySettlementCase("empty settlement creates no roads over several sessions", ref passed, ref failed);
            RunApplyCase("diagonal-only seed: final network is one connected component", Seed.DiagonalPath, ref passed, ref failed);
            RunApplyCase("cardinal Path neighbour permits growth, single component", Seed.CardinalPath, ref passed, ref failed);
            RunApplyCase("cardinal Stone neighbour permits growth, single component", Seed.CardinalStone, ref passed, ref failed);
            RunSeamCase("Apply grows Path across the chunk seam from a Stone seed", ref passed, ref failed);
            RunStoneBridgeCase("Stone bridge between two Path cells is never turned into a Hut", ref passed, ref failed);

            var summary = $"[C-3H] Road connectivity tests: passed={passed}, failed={failed}";
            if (failed == 0) Debug.Log(summary);
            else Debug.LogError(summary);
            return failed == 0;
        }

        // ---------- cases ----------

        private static void RunApplyCase(string name, Seed seed, ref int passed, ref int failed)
        {
            const int chunkX = 20;
            const int chunkZ = 20;
            const int targetX = 7;
            const int targetZ = 7;
            var save = CreateFixture(chunkX, chunkZ, out var growth, out var land, out var settle);

            // Interior Moss patch with Clearings, surrounded by Void so only the inner 3x3 is non-shore.
            PaintClearingPatch(land, settle, 5, 9, 5, 9);

            var seedX = -1;
            var seedZ = -1;
            switch (seed)
            {
                case Seed.DiagonalPath:
                    seedX = targetX - 1;
                    seedZ = targetZ - 1;
                    settle[ChunkMaskCodec.CellIndex(seedX, seedZ)] = (byte)SettlementStage.Path;
                    break;
                case Seed.CardinalPath:
                    seedX = targetX - 1;
                    seedZ = targetZ;
                    settle[ChunkMaskCodec.CellIndex(seedX, seedZ)] = (byte)SettlementStage.Path;
                    break;
                case Seed.CardinalStone:
                    seedX = targetX - 1;
                    seedZ = targetZ;
                    settle[ChunkMaskCodec.CellIndex(seedX, seedZ)] = (byte)SettlementStage.Stone;
                    break;
            }

            growth.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land);
            growth.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle);

            var targetIndex = ChunkMaskCodec.CellIndex(targetX, targetZ);
            var expectNeighbor = seed == Seed.CardinalPath || seed == Seed.CardinalStone;
            var hasNeighbor = SettlementSimulation.HasCardinalRoadNeighbor(save, chunkX, chunkZ, targetIndex);

            var result = SettlementSimulation.Apply(save, 7 * 86400L, new DeterministicRng(123456u));
            var roads = AnalyzeRoads(save);

            var ok = hasNeighbor == expectNeighbor;
            var detail = $"cardinalNeighborBefore={hasNeighbor}, pathsAdded={result.pathsAdded}, stonesAdded={result.stonesAdded}, roadCells={roads.Cells}, components={roads.Components}";

            if (seed == Seed.None)
            {
                // No network exists: nothing may be created from nothing.
                ok &= roads.Cells == 0 && result.pathsAdded == 0;
            }
            else
            {
                var seedAttached = SeedStillPartOfNetwork(save, roads, chunkX * Grid + seedX, chunkZ * Grid + seedZ);
                ok &= seedAttached;
                // Every new Path/Stone cell must be attached to the original network: exactly one component.
                ok &= roads.Components == 1;
                detail += $", seedStage={(SettlementStage)SettlementSimulation.SampleSettlementStage(save, chunkX * Grid + seedX, chunkZ * Grid + seedZ)}, seedAttached={seedAttached}";

                if (seed == Seed.CardinalPath)
                {
                    // A real cardinal seed must allow growth.
                    ok &= result.pathsAdded > 0;
                }

                // The target may only be Path when it touches road cells in the final topology.
                var targetStage = SettlementSimulation.SampleSettlementStage(
                    save, chunkX * Grid + targetX, chunkZ * Grid + targetZ);
                if (targetStage == (byte)SettlementStage.Path)
                {
                    var attached = SettlementSimulation.HasCardinalRoadNeighbor(save, chunkX, chunkZ, targetIndex);
                    ok &= attached;
                    detail += $", targetPathAttached={attached}";
                }
            }

            Log(name, ok, detail, ref passed, ref failed);
        }

        private static void RunEmptySettlementCase(string name, ref int passed, ref int failed)
        {
            const int chunkX = 20;
            const int chunkZ = 20;
            var save = CreateFixture(chunkX, chunkZ, out var growth, out var land, out var settle);

            // Large Clearing area with no roads and no huts: simulation must never invent a road or a hut.
            PaintClearingPatch(land, settle, 3, 12, 3, 12);
            growth.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land);
            growth.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle);

            var pathsAdded = 0;
            var stonesAdded = 0;
            var hutsAdded = 0;
            var rng = new DeterministicRng(987654u);
            for (var session = 0; session < 4; session++)
            {
                save.worldGrowth.settlementWatermarkUtc = 0;
                var result = SettlementSimulation.Apply(save, 7 * 86400L, rng);
                pathsAdded += result.pathsAdded;
                stonesAdded += result.stonesAdded;
                hutsAdded += result.hutsAdded;
            }

            var roads = AnalyzeRoads(save);
            var ok = roads.Cells == 0 && pathsAdded == 0 && stonesAdded == 0 && hutsAdded == 0;
            Log(name, ok,
                $"sessions=4, pathsAdded={pathsAdded}, stonesAdded={stonesAdded}, hutsAdded={hutsAdded}, roadCells={roads.Cells}",
                ref passed, ref failed);
        }

        private static void RunSeamCase(string name, ref int passed, ref int failed)
        {
            const int leftChunkX = 20;
            const int rightChunkX = 21;
            const int chunkZ = 20;
            var save = new GameSaveData
            {
                version = 8,
                lastSaveUtc = 1760000000L,
                worldGrowth = new WorldGrowthSaveData
                {
                    growthSeed = 4321,
                    lastSimulationUtc = 1760000000L,
                    settlementWatermarkUtc = 0,
                    chunks = new List<ChunkGrowthSaveData>()
                }
            };

            var leftLand = new byte[ChunkMaskCodec.CellCount];
            var rightLand = new byte[ChunkMaskCodec.CellCount];
            var leftSettle = new byte[ChunkMaskCodec.CellCount];
            var rightSettle = new byte[ChunkMaskCodec.CellCount];

            // Moss on both sides of the seam so the corridor cells are not shore cells.
            for (var z = 5; z <= 9; z++)
            {
                for (var x = 10; x <= 15; x++) leftLand[ChunkMaskCodec.CellIndex(x, z)] = (byte)CellStage.Moss;
                for (var x = 0; x <= 5; x++) rightLand[ChunkMaskCodec.CellIndex(x, z)] = (byte)CellStage.Moss;
            }

            // Stone seed on the left side of the seam, Clearing corridor on the right side.
            leftSettle[ChunkMaskCodec.CellIndex(15, 7)] = (byte)SettlementStage.Stone;
            for (var x = 0; x <= 2; x++)
            {
                rightSettle[ChunkMaskCodec.CellIndex(x, 7)] = (byte)SettlementStage.Clearing;
            }

            save.worldGrowth.chunks.Add(CreateGrowth(leftChunkX, chunkZ, leftLand, leftSettle));
            save.worldGrowth.chunks.Add(CreateGrowth(rightChunkX, chunkZ, rightLand, rightSettle));
            save.chunks.Add(new ChunkSaveData { chunkX = leftChunkX, chunkZ = chunkZ, isUnlocked = true, biomeId = "mossy" });
            save.chunks.Add(new ChunkSaveData { chunkX = rightChunkX, chunkZ = chunkZ, isUnlocked = true, biomeId = "mossy" });

            var seamIndex = ChunkMaskCodec.CellIndex(0, 7);
            var neighborBefore = SettlementSimulation.HasCardinalRoadNeighbor(save, rightChunkX, chunkZ, seamIndex);

            var result = SettlementSimulation.Apply(save, 7 * 86400L, new DeterministicRng(2468u));
            var roads = AnalyzeRoads(save);
            var seamStage = SettlementSimulation.SampleSettlementStage(save, rightChunkX * Grid + 0, chunkZ * Grid + 7);
            var seedAttached = SeedStillPartOfNetwork(save, roads, leftChunkX * Grid + 15, chunkZ * Grid + 7);

            var ok = neighborBefore &&
                     seamStage == (byte)SettlementStage.Path &&
                     seedAttached &&
                     roads.Components == 1;
            Log(name, ok,
                $"neighborBefore={neighborBefore}, seamCellStage={(SettlementStage)seamStage}, pathsAdded={result.pathsAdded}, roadCells={roads.Cells}, components={roads.Components}",
                ref passed, ref failed);
        }

        /// <summary>
        /// A Stone between two Path cells is a cut vertex of the road graph. Turning it into a Hut would
        /// split the network, so Apply must keep it a road for every RNG seed.
        /// </summary>
        private static void RunStoneBridgeCase(string name, ref int passed, ref int failed)
        {
            const int chunkX = 20;
            const int chunkZ = 20;
            var ok = true;
            var details = string.Empty;

            for (var s = 1u; s <= 8u; s++)
            {
                var save = CreateFixture(chunkX, chunkZ, out var growth, out var land, out var settle);
                PaintMossPatch(land, 2, 13, 2, 13);
                settle[ChunkMaskCodec.CellIndex(5, 7)] = (byte)SettlementStage.Path;
                settle[ChunkMaskCodec.CellIndex(6, 7)] = (byte)SettlementStage.Stone;
                settle[ChunkMaskCodec.CellIndex(7, 7)] = (byte)SettlementStage.Path;
                growth.stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land);
                growth.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle);

                SettlementSimulation.Apply(save, 7 * 86400L, new DeterministicRng(1000u + s));
                var roads = AnalyzeRoads(save);
                var bridge = SettlementSimulation.SampleSettlementStage(save, chunkX * Grid + 6, chunkZ * Grid + 7);
                var bridgeIsRoad = bridge == (byte)SettlementStage.Stone || bridge == (byte)SettlementStage.Path;
                var seedOk = bridgeIsRoad && roads.Components == 1;
                ok &= seedOk;
                details += $"[rng{s}: bridge={(SettlementStage)bridge}, components={roads.Components}] ";
            }

            Log(name, ok, details.TrimEnd(), ref passed, ref failed);
        }

        /// <summary>The seed is still a road, or became a Hut that a road cardinally touches (a leaf hut is legal).</summary>
        private static bool SeedStillPartOfNetwork(GameSaveData save, RoadAnalysis roads, int seedWx, int seedWz)
        {
            if (roads.Set.Contains(Key(seedWx, seedWz)))
            {
                return true;
            }

            var stage = SettlementSimulation.SampleSettlementStage(save, seedWx, seedWz);
            return stage == (byte)SettlementStage.Hut &&
                   (roads.Set.Contains(Key(seedWx + 1, seedWz)) ||
                    roads.Set.Contains(Key(seedWx - 1, seedWz)) ||
                    roads.Set.Contains(Key(seedWx, seedWz + 1)) ||
                    roads.Set.Contains(Key(seedWx, seedWz - 1)));
        }

        // ---------- fixtures ----------

        private static void PaintMossPatch(byte[] land, int x0, int x1, int z0, int z1)
        {
            for (var z = z0; z <= z1; z++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    land[ChunkMaskCodec.CellIndex(x, z)] = (byte)CellStage.Moss;
                }
            }
        }

        private static void PaintClearingPatch(byte[] land, byte[] settle, int x0, int x1, int z0, int z1)
        {
            for (var z = z0; z <= z1; z++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var idx = ChunkMaskCodec.CellIndex(x, z);
                    land[idx] = (byte)CellStage.Moss;
                    settle[idx] = (byte)SettlementStage.Clearing;
                }
            }
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

        private static ChunkGrowthSaveData CreateGrowth(int chunkX, int chunkZ, byte[] land, byte[] settle)
        {
            var biome = new byte[ChunkMaskCodec.CellCount];
            return new ChunkGrowthSaveData
            {
                chunkX = chunkX,
                chunkZ = chunkZ,
                isLegacyBase = false,
                growthSeed = 1000 + chunkX,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(land),
                biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biome),
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle)
            };
        }

        // ---------- topology analysis (world-cell graph across chunks, 4-neighbourhood) ----------

        private sealed class RoadAnalysis
        {
            public readonly HashSet<long> Set = new HashSet<long>();
            public int Cells;
            public int Components;
        }

        private static long Key(int wx, int wz)
        {
            return ((long)wx << 32) | (long)(uint)wz;
        }

        private static RoadAnalysis AnalyzeRoads(GameSaveData save)
        {
            var a = new RoadAnalysis();
            if (save?.worldGrowth?.chunks == null) return a;

            foreach (var g in save.worldGrowth.chunks)
            {
                if (g == null || !ChunkMaskCodec.TryDecodeBase64(g.settlementMaskBase64, out var settle, out _)) continue;
                for (var z = 0; z < Grid; z++)
                {
                    for (var x = 0; x < Grid; x++)
                    {
                        var st = settle[ChunkMaskCodec.CellIndex(x, z)];
                        if (st != (byte)SettlementStage.Path && st != (byte)SettlementStage.Stone) continue;
                        a.Set.Add(Key(g.chunkX * Grid + x, g.chunkZ * Grid + z));
                    }
                }
            }

            a.Cells = a.Set.Count;

            var visited = new HashSet<long>();
            var stack = new Stack<long>();
            foreach (var start in a.Set)
            {
                if (visited.Contains(start)) continue;
                a.Components++;
                visited.Add(start);
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    var cx = (int)(cur >> 32);
                    var cz = (int)(uint)(cur & 0xFFFFFFFFL);
                    TryVisit(a.Set, visited, stack, cx + 1, cz);
                    TryVisit(a.Set, visited, stack, cx - 1, cz);
                    TryVisit(a.Set, visited, stack, cx, cz + 1);
                    TryVisit(a.Set, visited, stack, cx, cz - 1);
                }
            }

            return a;
        }

        private static void TryVisit(HashSet<long> set, HashSet<long> visited, Stack<long> stack, int wx, int wz)
        {
            var k = Key(wx, wz);
            if (!set.Contains(k) || visited.Contains(k)) return;
            visited.Add(k);
            stack.Push(k);
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
