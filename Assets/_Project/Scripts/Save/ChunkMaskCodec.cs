using System;
using System.Text;
using UnityEngine;

namespace AfkIsland.Save
{
    /// <summary>
    /// Packs 256 cell stages (3 bits each) into 96 bytes / Base64.
    /// Cell index: i = z * 16 + x. Bits for cell i: [3*i .. 3*i+2], LSB first.
    /// </summary>
    public static class ChunkMaskCodec
    {
        public const int GridSize = 16;
        public const int CellCount = GridSize * GridSize;
        public const int PackedByteCount = 96; // 256 * 3 / 8
        public const byte MaxStage = (byte)CellStage.Vegetation;

        public static byte[] CreateMatureMask()
        {
            var stages = new byte[CellCount];
            for (var i = 0; i < CellCount; i++)
            {
                stages[i] = (byte)CellStage.Vegetation;
            }

            return EncodeStages(stages);
        }

        public static string CreateMatureMaskBase64()
        {
            return Convert.ToBase64String(CreateMatureMask());
        }

        public static byte[] EncodeStages(byte[] stages)
        {
            if (stages == null || stages.Length != CellCount)
            {
                throw new ArgumentException("stages must contain exactly 256 values.");
            }

            var packed = new byte[PackedByteCount];
            for (var i = 0; i < CellCount; i++)
            {
                var stage = stages[i];
                if (stage > 7)
                {
                    throw new ArgumentOutOfRangeException(nameof(stages), $"Stage {stage} out of 3-bit range.");
                }

                var bit = i * 3;
                for (var b = 0; b < 3; b++)
                {
                    if (((stage >> b) & 1) == 0)
                    {
                        continue;
                    }

                    var absolute = bit + b;
                    packed[absolute / 8] |= (byte)(1 << (absolute % 8));
                }
            }

            return packed;
        }

        public static bool TryDecodeBase64(string base64, out byte[] stages, out string error)
        {
            stages = null;
            error = null;
            if (string.IsNullOrWhiteSpace(base64))
            {
                error = "empty_mask";
                return false;
            }

            byte[] packed;
            try
            {
                packed = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                error = "invalid_base64";
                return false;
            }

            if (packed.Length != PackedByteCount)
            {
                error = $"mask_byte_length_{packed.Length}";
                return false;
            }

            stages = new byte[CellCount];
            for (var i = 0; i < CellCount; i++)
            {
                byte stage = 0;
                var bit = i * 3;
                for (var b = 0; b < 3; b++)
                {
                    var absolute = bit + b;
                    if (((packed[absolute / 8] >> (absolute % 8)) & 1) != 0)
                    {
                        stage |= (byte)(1 << b);
                    }
                }

                if (stage > MaxStage)
                {
                    error = $"unknown_stage_{stage}_at_{i}";
                    stages = null;
                    return false;
                }

                stages[i] = stage;
            }

            return true;
        }

        public static string EncodeToBase64(byte[] stages)
        {
            return Convert.ToBase64String(EncodeStages(stages));
        }

        public static float ProgressFromQ16(int progressQ16)
        {
            return Mathf.Clamp01(progressQ16 / 65535f);
        }

        public static int ProgressToQ16(float progress01)
        {
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(progress01) * 65535f), 0, 65535);
        }

        public static int CellIndex(int localX, int localZ)
        {
            return localZ * GridSize + localX;
        }

        public static void IndexToLocal(int cellIndex, out int localX, out int localZ)
        {
            localX = cellIndex % GridSize;
            localZ = cellIndex / GridSize;
        }

        public static string Describe(byte[] stages)
        {
            if (stages == null)
            {
                return "null";
            }

            var sb = new StringBuilder();
            sb.Append("cells=").Append(stages.Length);
            return sb.ToString();
        }
    }
}
