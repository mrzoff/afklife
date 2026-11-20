using System;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Sticky biome assignment + soft world-space sampling for materials/decor.
    /// Coastal can be visually boosted near water without rewriting saved inland biomes.
    /// </summary>
    public static class BiomeSampling
    {
        public const int CoastalNeighborRadius = 1;

        /// <summary>
        /// Primary sticky assign when a void cell becomes land (or migration fill).
        /// Never called to randomly re-roll an existing non-None biome.
        /// </summary>
        public static BiomeType ChooseInitial(
            int worldCellX,
            int worldCellZ,
            byte stage,
            bool isShore,
            int growthSeed)
        {
            if (stage == (byte)CellStage.Void)
            {
                return BiomeType.None;
            }

            if (isShore)
            {
                return BiomeType.Coastal;
            }

            // Inland: Mossy early, Forest once vegetation-mature.
            if (stage >= (byte)CellStage.Vegetation)
            {
                return BiomeType.Forest;
            }

            if (stage >= (byte)CellStage.Moss)
            {
                return BiomeType.Mossy;
            }

            // Roots/Soil inland — slight deterministic preference.
            var h = Hash01(growthSeed, worldCellX, worldCellZ);
            return h < 0.35f ? BiomeType.Mossy : BiomeType.Coastal;
        }

        /// <summary>
        /// One-way evolution only: Mossy→Forest when stage hits Vegetation.
        /// Coastal never changes. None fills via ChooseInitial.
        /// </summary>
        public static BiomeType EvolveSticky(BiomeType current, byte stage, bool isShore, int worldCellX, int worldCellZ, int growthSeed)
        {
            if (stage == (byte)CellStage.Void)
            {
                return BiomeType.None;
            }

            if (current == BiomeType.None)
            {
                return ChooseInitial(worldCellX, worldCellZ, stage, isShore, growthSeed);
            }

            if (current == BiomeType.Coastal)
            {
                return BiomeType.Coastal;
            }

            if (current == BiomeType.Mossy && stage >= (byte)CellStage.Vegetation)
            {
                return BiomeType.Forest;
            }

            if (current == BiomeType.Forest)
            {
                return BiomeType.Forest;
            }

            // Mossy / Coastal-ish inland stay unless Forest upgrade above.
            if (stage >= (byte)CellStage.Moss && current == BiomeType.None)
            {
                return BiomeType.Mossy;
            }

            return current;
        }

        public static void EnsureAndBackfill(ChunkGrowthSaveData growth)
        {
            if (growth == null)
            {
                return;
            }

            if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
            {
                return;
            }

            var biomes = BiomeMaskCodec.DecodeOrEmpty(growth.biomeMaskBase64);
            var seed = growth.growthSeed != 0
                ? growth.growthSeed
                : growth.chunkX * 73856093 ^ growth.chunkZ * 19349663;
            var changed = false;

            for (var i = 0; i < stages.Length; i++)
            {
                var stage = stages[i];
                if (stage == (byte)CellStage.Void)
                {
                    if (biomes[i] != (byte)BiomeType.None)
                    {
                        biomes[i] = (byte)BiomeType.None;
                        changed = true;
                    }

                    continue;
                }

                ChunkMaskCodec.IndexToLocal(i, out var lx, out var lz);
                var wx = growth.chunkX * ChunkMaskCodec.GridSize + lx;
                var wz = growth.chunkZ * ChunkMaskCodec.GridSize + lz;
                var shore = IsLocalShore(stages, lx, lz);
                var cur = (BiomeType)biomes[i];
                var next = EvolveSticky(cur, stage, shore, wx, wz, seed);
                if ((byte)next != biomes[i])
                {
                    biomes[i] = (byte)next;
                    changed = true;
                }
            }

            if (changed || string.IsNullOrWhiteSpace(growth.biomeMaskBase64))
            {
                growth.biomeMaskBase64 = BiomeMaskCodec.EncodeToBase64(biomes);
            }
        }

        public static void AssignCell(
            ChunkGrowthSaveData growth,
            byte[] stages,
            byte[] biomes,
            int cellIndex,
            Func<int, int, bool> isWorldShore)
        {
            if (growth == null || stages == null || biomes == null)
            {
                return;
            }

            if (cellIndex < 0 || cellIndex >= stages.Length)
            {
                return;
            }

            var stage = stages[cellIndex];
            ChunkMaskCodec.IndexToLocal(cellIndex, out var lx, out var lz);
            var wx = growth.chunkX * ChunkMaskCodec.GridSize + lx;
            var wz = growth.chunkZ * ChunkMaskCodec.GridSize + lz;
            var shore = isWorldShore != null
                ? isWorldShore(wx, wz)
                : IsLocalShore(stages, lx, lz);
            var seed = growth.growthSeed != 0
                ? growth.growthSeed
                : growth.chunkX * 73856093 ^ growth.chunkZ * 19349663;
            var cur = (BiomeType)biomes[cellIndex];
            biomes[cellIndex] = (byte)EvolveSticky(cur, stage, shore, wx, wz, seed);
        }

        /// <summary>
        /// Soft weights (coastal, mossy, forest) for materials. Uses bilinear sticky ids
        /// plus a coastal boost from signed distance to water (metres, optional).
        /// </summary>
        public static Vector3 SoftWeights(
            float worldCellFx,
            float worldCellFz,
            Func<int, int, BiomeType> sampleBiome,
            float signedDistanceMetres)
        {
            var x0 = Mathf.FloorToInt(worldCellFx);
            var z0 = Mathf.FloorToInt(worldCellFz);
            var tx = worldCellFx - x0;
            var tz = worldCellFz - z0;

            var w00 = OneHot(sampleBiome(x0, z0));
            var w10 = OneHot(sampleBiome(x0 + 1, z0));
            var w01 = OneHot(sampleBiome(x0, z0 + 1));
            var w11 = OneHot(sampleBiome(x0 + 1, z0 + 1));

            var w0 = Vector3.Lerp(w00, w10, tx);
            var w1 = Vector3.Lerp(w01, w11, tx);
            var w = Vector3.Lerp(w0, w1, tz);

            // Visual coastal belt near water — does not rewrite save.
            if (signedDistanceMetres < 2.4f)
            {
                var coast = 1f - Mathf.SmoothStep(0.15f, 2.4f, Mathf.Max(0f, signedDistanceMetres));
                w = Vector3.Lerp(w, new Vector3(1f, 0f, 0f), coast * 0.75f);
            }

            var sum = w.x + w.y + w.z;
            if (sum < 1e-5f)
            {
                return new Vector3(0.4f, 0.4f, 0.2f);
            }

            return w / sum;
        }

        public static Color TintForWeights(Vector3 w, Color baseColor)
        {
            var coastal = new Color(0.82f, 0.72f, 0.48f);
            var mossy = new Color(0.28f, 0.48f, 0.30f);
            var forest = new Color(0.22f, 0.50f, 0.26f);
            var tint = coastal * w.x + mossy * w.y + forest * w.z;
            return Color.Lerp(baseColor, tint, 0.45f);
        }

        private static Vector3 OneHot(BiomeType b)
        {
            switch (b)
            {
                case BiomeType.Coastal: return new Vector3(1f, 0f, 0f);
                case BiomeType.Mossy: return new Vector3(0f, 1f, 0f);
                case BiomeType.Forest: return new Vector3(0f, 0f, 1f);
                default: return new Vector3(0.5f, 0.35f, 0.15f);
            }
        }

        private static bool IsLocalShore(byte[] stages, int lx, int lz)
        {
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
                    if (nx < 0 || nz < 0 || nx >= ChunkMaskCodec.GridSize || nz >= ChunkMaskCodec.GridSize)
                    {
                        return true;
                    }

                    if (stages[ChunkMaskCodec.CellIndex(nx, nz)] == (byte)CellStage.Void)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static float Hash01(int seed, int x, int z)
        {
            unchecked
            {
                var h = (uint)(seed * 374761393 + x * 668265263 + z * 2147483647);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }
    }
}
