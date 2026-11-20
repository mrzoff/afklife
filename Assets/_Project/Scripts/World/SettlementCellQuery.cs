using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Reads the settlement mask. Does not advance stages or change the save.
    /// </summary>
    public static class SettlementCellQuery
    {
        public static bool IsHut(ChunkGrowthSaveData growth, int cellIndex)
        {
            if (growth == null || cellIndex < 0 || cellIndex >= ChunkMaskCodec.CellCount)
            {
                return false;
            }

            if (!ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
            {
                return false;
            }

            return cellIndex < settle.Length && settle[cellIndex] == (byte)SettlementStage.Hut;
        }

        public static bool IsHut(GameSaveData save, int chunkX, int chunkZ, int cellIndex)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return false;
            }

            for (var i = 0; i < save.worldGrowth.chunks.Count; i++)
            {
                var growth = save.worldGrowth.chunks[i];
                if (growth != null && growth.chunkX == chunkX && growth.chunkZ == chunkZ)
                {
                    return IsHut(growth, cellIndex);
                }
            }

            return false;
        }

        /// <summary>Maps a world XZ point to chunk/cell using the same origin as GrowthWorldPlacement.</summary>
        public static bool TryWorldToCell(float worldX, float worldZ, out int chunkX, out int chunkZ, out int cellIndex)
        {
            chunkX = 0;
            chunkZ = 0;
            cellIndex = -1;

            var size = GrowthWorldPlacement.ChunkSizeMeters;
            if (size <= 0f)
            {
                return false;
            }

            var cell = size / ChunkMaskCodec.GridSize;
            chunkX = Mathf.FloorToInt((worldX + size * 0.5f) / size);
            chunkZ = Mathf.FloorToInt((worldZ + size * 0.5f) / size);
            var origin = GrowthWorldPlacement.ChunkOriginXZ(chunkX, chunkZ, size);
            var lx = Mathf.FloorToInt((worldX - origin.x) / cell);
            var lz = Mathf.FloorToInt((worldZ - origin.y) / cell);
            if (lx < 0 || lz < 0 || lx >= ChunkMaskCodec.GridSize || lz >= ChunkMaskCodec.GridSize)
            {
                return false;
            }

            cellIndex = ChunkMaskCodec.CellIndex(lx, lz);
            return cellIndex >= 0 && cellIndex < ChunkMaskCodec.CellCount;
        }

        /// <summary>True when the world XZ point lands on a hut cell.</summary>
        public static bool IsHutAtWorldXZ(GameSaveData save, float worldX, float worldZ)
        {
            return TryWorldToCell(worldX, worldZ, out var chunkX, out var chunkZ, out var cellIndex) &&
                   IsHut(save, chunkX, chunkZ, cellIndex);
        }

        /// <summary>wild_{chunkX}_{chunkZ}_{cellIndex}. Indexes outside the 16×16 mask are not cells.</summary>
        public static bool TryParseWildCell(string plotId, out int chunkX, out int chunkZ, out int cellIndex)
        {
            chunkX = 0;
            chunkZ = 0;
            cellIndex = 0;
            if (!PlantColonizationSimulation.IsWildPlotId(plotId))
            {
                return false;
            }

            var body = plotId.Substring("wild_".Length);
            var parts = body.Split('_');
            if (parts.Length != 3)
            {
                return false;
            }

            return int.TryParse(parts[0], out chunkX)
                   && int.TryParse(parts[1], out chunkZ)
                   && int.TryParse(parts[2], out cellIndex)
                   && cellIndex >= 0
                   && cellIndex < ChunkMaskCodec.CellCount;
        }
    }
}
