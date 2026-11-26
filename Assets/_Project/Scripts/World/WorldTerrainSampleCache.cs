using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Shared read-only world stage/biome sampler used by terrain and settlement visuals.
    /// Each consumer refreshes it from the same authoritative GameSaveData snapshot.
    /// Legacy home chunks are represented as Moss, matching ChunkTerrainMesher's existing terrain contract.
    /// </summary>
    public sealed class WorldTerrainSampleCache
    {
        private readonly Dictionary<long, byte[]> _stageSamples = new Dictionary<long, byte[]>();
        private readonly Dictionary<long, byte[]> _biomeSamples = new Dictionary<long, byte[]>();

        public int Revision { get; private set; }

        public void Rebuild(GameSaveData data)
        {
            _stageSamples.Clear();
            _biomeSamples.Clear();

            if (data?.worldGrowth?.chunks != null)
            {
                foreach (var growth in data.worldGrowth.chunks)
                {
                    if (growth == null) continue;

                    var key = Pack(growth.chunkX, growth.chunkZ);
                    byte[] stages;
                    if (growth.isLegacyBase)
                    {
                        stages = new byte[ChunkMaskCodec.CellCount];
                        for (var i = 0; i < stages.Length; i++)
                            stages[i] = (byte)CellStage.Moss;
                    }
                    else if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out stages, out _))
                    {
                        stages = System.Array.Empty<byte>();
                    }

                    _stageSamples[key] = stages;
                    _biomeSamples[key] = BiomeMaskCodec.DecodeOrEmpty(growth.biomeMaskBase64);
                }
            }

            unchecked { Revision++; }
        }

        public byte SampleStage(int worldCellX, int worldCellZ)
        {
            ResolveCell(worldCellX, worldCellZ, out var key, out var index);
            return _stageSamples.TryGetValue(key, out var stages) && index >= 0 && index < stages.Length
                ? stages[index]
                : (byte)CellStage.Void;
        }

        public BiomeType SampleBiome(int worldCellX, int worldCellZ)
        {
            ResolveCell(worldCellX, worldCellZ, out var key, out var index);
            return _biomeSamples.TryGetValue(key, out var biomes) && index >= 0 && index < biomes.Length
                ? (BiomeType)biomes[index]
                : BiomeType.None;
        }

        public float SampleHeight(float worldX, float worldZ, float chunkSize)
        {
            return ChunkTerrainMesher.SampleVisualHeight(
                worldX, worldZ, SampleStage, SampleBiome, chunkSize);
        }

        public bool IsInsideLand(float worldX, float worldZ)
        {
            var size = GrowthWorldPlacement.ChunkSizeMeters;
            var cell = size / ChunkMaskCodec.GridSize;
            var gridOrigin = -size * 0.5f;
            var worldCellX = Mathf.FloorToInt((worldX - gridOrigin) / cell);
            var worldCellZ = Mathf.FloorToInt((worldZ - gridOrigin) / cell);
            return SampleStage(worldCellX, worldCellZ) != (byte)CellStage.Void;
        }

        private static void ResolveCell(int worldCellX, int worldCellZ, out long key, out int index)
        {
            var grid = ChunkMaskCodec.GridSize;
            var chunkX = FloorDiv(worldCellX, grid);
            var chunkZ = FloorDiv(worldCellZ, grid);
            var lx = worldCellX - chunkX * grid;
            var lz = worldCellZ - chunkZ * grid;
            key = Pack(chunkX, chunkZ);
            index = ChunkMaskCodec.CellIndex(lx, lz);
        }

        private static int FloorDiv(int value, int divisor)
        {
            var q = value / divisor;
            var r = value % divisor;
            if (r != 0 && ((r > 0) != (divisor > 0))) q--;
            return q;
        }

        private static long Pack(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}