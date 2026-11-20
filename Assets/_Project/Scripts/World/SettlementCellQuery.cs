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

        public const float DefaultHutCenterSpacing = 3.2f;
        public const float DefaultTreeHutClearance = 2.2f;

        /// <summary>
        /// True when a visual or new hut footprint would overlap an existing generated hut
        /// or either authored starter hut. This is a read-only query; it never changes the save.
        /// </summary>
        public static bool IsHutFootprintBlocked(
            GameSaveData save,
            float worldX,
            float worldZ,
            float minimumCenterSpacing)
        {
            var spacing = Mathf.Max(0.1f, minimumCenterSpacing);
            if (IsNearAuthoredStarterHut(worldX, worldZ, spacing))
            {
                return true;
            }

            if (save?.worldGrowth?.chunks == null)
            {
                return false;
            }

            var minDistanceSq = spacing * spacing;
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null ||
                    !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                {
                    continue;
                }

                for (var i = 0; i < settle.Length; i++)
                {
                    if (settle[i] != (byte)SettlementStage.Hut)
                    {
                        continue;
                    }

                    var hut = GrowthWorldPlacement.CellCenterWorld(growth.chunkX, growth.chunkZ, i);
                    var dx = hut.x - worldX;
                    var dz = hut.z - worldZ;
                    if (dx * dx + dz * dz < minDistanceSq)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Deterministically suppresses legacy overlapping hut visuals without rewriting Save v8.
        /// For an overlap cluster, the hut with the lowest world-cell Z/X key remains visible.
        /// </summary>
        public static bool IsHutVisualSuppressed(
            GameSaveData save,
            int chunkX,
            int chunkZ,
            int cellIndex,
            float minimumCenterSpacing = DefaultHutCenterSpacing)
        {
            if (cellIndex < 0 || cellIndex >= ChunkMaskCodec.CellCount)
            {
                return true;
            }

            var center = GrowthWorldPlacement.CellCenterWorld(chunkX, chunkZ, cellIndex);
            var spacing = Mathf.Max(0.1f, minimumCenterSpacing);
            if (IsNearAuthoredStarterHut(center.x, center.z, spacing))
            {
                return true;
            }

            if (save?.worldGrowth?.chunks == null)
            {
                return false;
            }

            ChunkMaskCodec.IndexToLocal(cellIndex, out var candidateX, out var candidateZ);
            var candidateWorldX = chunkX * ChunkMaskCodec.GridSize + candidateX;
            var candidateWorldZ = chunkZ * ChunkMaskCodec.GridSize + candidateZ;
            var minDistanceSq = spacing * spacing;

            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null ||
                    !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                {
                    continue;
                }

                for (var i = 0; i < settle.Length; i++)
                {
                    if (settle[i] != (byte)SettlementStage.Hut ||
                        (growth.chunkX == chunkX && growth.chunkZ == chunkZ && i == cellIndex))
                    {
                        continue;
                    }

                    var other = GrowthWorldPlacement.CellCenterWorld(growth.chunkX, growth.chunkZ, i);
                    var dx = other.x - center.x;
                    var dz = other.z - center.z;
                    if (dx * dx + dz * dz >= minDistanceSq)
                    {
                        continue;
                    }

                    ChunkMaskCodec.IndexToLocal(i, out var otherX, out var otherZ);
                    var otherWorldX = growth.chunkX * ChunkMaskCodec.GridSize + otherX;
                    var otherWorldZ = growth.chunkZ * ChunkMaskCodec.GridSize + otherZ;
                    if (otherWorldZ < candidateWorldZ ||
                        (otherWorldZ == candidateWorldZ && otherWorldX < candidateWorldX))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsNearAuthoredStarterHut(float worldX, float worldZ, float clearance)
        {
            var clearanceSq = clearance * clearance;
            var dxA = worldX + 1.6f;
            var dzA = worldZ - 0.9f;
            if (dxA * dxA + dzA * dzA < clearanceSq)
            {
                return true;
            }

            var dxB = worldX - 1.8f;
            var dzB = worldZ + 0.2f;
            return dxB * dxB + dzB * dzB < clearanceSq;
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
