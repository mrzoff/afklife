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
        private bool _hasLegacyHomeBase;

        public int Revision { get; private set; }
        public bool HasLegacyHomeBase => _hasLegacyHomeBase;

        public void Rebuild(GameSaveData data)
        {
            _stageSamples.Clear();
            _biomeSamples.Clear();
            _hasLegacyHomeBase = false;

            if (data?.worldGrowth?.chunks != null)
            {
                foreach (var growth in data.worldGrowth.chunks)
                {
                    if (growth == null) continue;

                    var key = Pack(growth.chunkX, growth.chunkZ);
                    byte[] stages;
                    if (growth.isLegacyBase)
                    {
                        if (growth.chunkX == 0 && growth.chunkZ == 0)
                            _hasLegacyHomeBase = true;
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

        public float SampleHeight(float worldX, float worldZ)
        {
            return SampleHeight(worldX, worldZ, GrowthWorldPlacement.ChunkSizeMeters);
        }

        public float SampleHeight(float worldX, float worldZ, float chunkSize)
        {
            var terrainY = ChunkTerrainMesher.SampleVisualHeight(
                worldX, worldZ, SampleStage, SampleBiome, chunkSize);
            if (!_hasLegacyHomeBase)
            {
                return terrainY;
            }

            // On the authored legacy home island (chunk 0,0), SettlementYard sits at ~1.85 m,
            // MidPlateau at ~1.40 m, and BaseRock at ~0.80 m. Smoothly blend down to grown
            // ChunkTerrainMesher height before reaching the chunk (0,0) edge so paths sit flush
            // on the authored yard and cross chunk borders with zero height discontinuity.
            var legacyY = SampleLegacyHomeEnvelopeY(worldX, worldZ, terrainY);
            return Mathf.Max(terrainY, legacyY);
        }

        private static float SampleLegacyHomeEnvelopeY(float wx, float wz, float terrainY)
        {
            const float yardY = 1.85f;
            const float midY = 1.41f;
            const float baseY = 0.81f;

            // Normalized elliptical distances matching IslandPrototypeBuilder organic platforms.
            // Between nested ellipses A (inner) and B (outer), u = (rA - 1) / (rA - rB) is
            // exactly 0 on rA=1 and 1 on rB=1 at every angle, guaranteeing C1 continuity.
            var rYard = Mathf.Sqrt((wx / 3.7f) * (wx / 3.7f) + (wz / 3.2f) * (wz / 3.2f));
            if (rYard <= 1f)
            {
                return yardY;
            }

            var rMid = Mathf.Sqrt((wx / 6.1f) * (wx / 6.1f) + (wz / 5.1f) * (wz / 5.1f));
            if (rMid <= 1f)
            {
                var u = Mathf.Clamp01((rYard - 1f) / Mathf.Max(0.001f, rYard - rMid));
                return Mathf.Lerp(yardY, midY, Mathf.SmoothStep(0f, 1f, u));
            }

            var rBase = Mathf.Sqrt((wx / 7.8f) * (wx / 7.8f) + (wz / 6.8f) * (wz / 6.8f));
            if (rBase <= 1f)
            {
                var u = Mathf.Clamp01((rMid - 1f) / Mathf.Max(0.001f, rMid - rBase));
                return Mathf.Lerp(midY, Mathf.Max(baseY, terrainY), Mathf.SmoothStep(0f, 1f, u));
            }

            var rEdge = Mathf.Sqrt((wx / 8.8f) * (wx / 8.8f) + (wz / 7.8f) * (wz / 7.8f));
            if (rEdge < 1f)
            {
                var u = Mathf.Clamp01((rBase - 1f) / Mathf.Max(0.001f, rBase - rEdge));
                return Mathf.Lerp(Mathf.Max(baseY, terrainY), terrainY, Mathf.SmoothStep(0f, 1f, u));
            }

            return terrainY;
        }

        public bool IsInsideLand(float worldX, float worldZ)
        {
            return IsInsideLand(worldX, worldZ, GrowthWorldPlacement.ChunkSizeMeters);
        }

        public bool IsInsideLand(float worldX, float worldZ, float chunkSize)
        {
            var size = chunkSize > 0.01f ? chunkSize : GrowthWorldPlacement.ChunkSizeMeters;
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