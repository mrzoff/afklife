using System;
using AfkIsland.World;

namespace AfkIsland.Save
{
    /// <summary>
    /// Packs 256 biome ids (2 bits each) into 64 bytes / Base64.
    /// Same cell indexing as <see cref="ChunkMaskCodec"/>: i = z * 16 + x.
    /// </summary>
    public static class BiomeMaskCodec
    {
        public const int CellCount = ChunkMaskCodec.CellCount;
        public const int PackedByteCount = 64; // 256 * 2 / 8
        public const byte MaxBiome = (byte)BiomeType.Forest;

        public static byte[] CreateEmptyBiomes()
        {
            return new byte[CellCount];
        }

        public static string EncodeToBase64(byte[] biomes)
        {
            return Convert.ToBase64String(EncodeBiomes(biomes));
        }

        public static byte[] EncodeBiomes(byte[] biomes)
        {
            if (biomes == null || biomes.Length != CellCount)
            {
                throw new ArgumentException("biomes must contain exactly 256 values.");
            }

            var packed = new byte[PackedByteCount];
            for (var i = 0; i < CellCount; i++)
            {
                var v = biomes[i];
                if (v > 3)
                {
                    throw new ArgumentOutOfRangeException(nameof(biomes), $"Biome {v} out of 2-bit range.");
                }

                var bit = i * 2;
                packed[bit / 8] |= (byte)((v & 0x3) << (bit % 8));
            }

            return packed;
        }

        public static bool TryDecodeBase64(string base64, out byte[] biomes, out string error)
        {
            biomes = null;
            error = null;
            if (string.IsNullOrWhiteSpace(base64))
            {
                error = "empty_biome_mask";
                return false;
            }

            byte[] packed;
            try
            {
                packed = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                error = "invalid_biome_base64";
                return false;
            }

            if (packed.Length != PackedByteCount)
            {
                error = $"biome_mask_byte_length_{packed.Length}";
                return false;
            }

            biomes = new byte[CellCount];
            for (var i = 0; i < CellCount; i++)
            {
                var bit = i * 2;
                var v = (byte)((packed[bit / 8] >> (bit % 8)) & 0x3);
                if (v > MaxBiome)
                {
                    error = $"unknown_biome_{v}_at_{i}";
                    biomes = null;
                    return false;
                }

                biomes[i] = v;
            }

            return true;
        }

        public static byte[] DecodeOrEmpty(string base64)
        {
            if (TryDecodeBase64(base64, out var biomes, out _))
            {
                return biomes;
            }

            return CreateEmptyBiomes();
        }
    }
}
