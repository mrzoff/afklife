using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Single island coordinate contract for mesh, plants, fauna, and decor.
    /// Chunk (0,0) is centered on world origin: XZ in [-size/2, +size/2).
    /// Matches authored Island/Ocean at World (0,0,0).
    /// </summary>
    public static class GrowthWorldPlacement
    {
        /// <summary>Must stay in sync with IslandMeshVisualizer.yBase.</summary>
        public const float VisualYBase = 0.08f;

        /// <summary>
        /// Authored home silhouette (IslandPrototypeBuilder BaseRock 18×14), inset so wild
        /// plants do not spawn on mask cells that sit in open ocean around the sculpted island.
        /// </summary>
        public const float AuthoredHomeHalfX = 7.25f;
        public const float AuthoredHomeHalfZ = 5.75f;

        public static float ChunkSizeMeters => IslandChunk.DefaultSizeMeters;

        public static bool IsInsideAuthoredHomeFootprint(Vector3 worldPos)
        {
            return IsInsideAuthoredHomeFootprint(worldPos.x, worldPos.z);
        }

        public static bool IsInsideAuthoredHomeFootprint(float worldX, float worldZ)
        {
            var nx = worldX / AuthoredHomeHalfX;
            var nz = worldZ / AuthoredHomeHalfZ;
            return nx * nx + nz * nz <= 1f;
        }

        public static int GridSize => ChunkMaskCodec.GridSize;

        public static float CellSizeMeters => ChunkSizeMeters / GridSize;

        /// <summary>World XZ of the chunk's local (0,0) corner (min corner of the chunk AABB).</summary>
        public static Vector2 ChunkOriginXZ(int chunkX, int chunkZ, float chunkSize = -1f)
        {
            var size = chunkSize > 0f ? chunkSize : ChunkSizeMeters;
            return new Vector2(chunkX * size - size * 0.5f, chunkZ * size - size * 0.5f);
        }

        public static Vector3 ChunkRootLocalPosition(int chunkX, int chunkZ, float yBase, float chunkSize = -1f)
        {
            var o = ChunkOriginXZ(chunkX, chunkZ, chunkSize);
            return new Vector3(o.x, yBase, o.y);
        }

        public static Vector3 CellCenterWorld(int chunkX, int chunkZ, int cellIndex, float chunkSize = -1f)
        {
            var size = chunkSize > 0f ? chunkSize : ChunkSizeMeters;
            var cell = size / GridSize;
            ChunkMaskCodec.IndexToLocal(cellIndex, out var lx, out var lz);
            var origin = ChunkOriginXZ(chunkX, chunkZ, size);
            var wx = origin.x + (lx + 0.5f) * cell;
            var wz = origin.y + (lz + 0.5f) * cell;
            return new Vector3(wx, 0f, wz);
        }

        /// <summary>Position inside a chunk root that already sits on ChunkOrigin + yBase.</summary>
        public static Vector3 CellLocalOnChunk(
            int chunkX,
            int chunkZ,
            int cellIndex,
            byte stage,
            float yBase,
            float chunkSize = -1f)
        {
            var size = chunkSize > 0f ? chunkSize : ChunkSizeMeters;
            var world = CellCenterWorld(chunkX, chunkZ, cellIndex, size);
            var origin = ChunkOriginXZ(chunkX, chunkZ, size);
            var yLocal = ApproximateSurfaceY(stage, isLegacyBase: false) - yBase;
            return new Vector3(world.x - origin.x, yLocal, world.z - origin.y);
        }

        public static float ApproximateSurfaceY(byte stage, bool isLegacyBase)
        {
            if (isLegacyBase)
            {
                return 1.85f;
            }

            // Keep in sync with ChunkTerrainMesher.SampleCellVisual + VisualYBase.
            var h = stage switch
            {
                (byte)CellStage.Roots => 0.26f,
                (byte)CellStage.Soil => 0.40f,
                (byte)CellStage.Moss => 0.52f,
                (byte)CellStage.Vegetation => 0.64f,
                _ => 0.20f
            };
            return h + VisualYBase;
        }

        public static Vector3 CellWorldPosition(
            int chunkX,
            int chunkZ,
            int cellIndex,
            byte stage,
            bool isLegacyBase,
            float chunkSize = -1f)
        {
            var c = CellCenterWorld(chunkX, chunkZ, cellIndex, chunkSize);
            c.y = ApproximateSurfaceY(stage, isLegacyBase);
            return c;
        }

        public static bool IsShoreCell(
            GameSaveData save,
            int chunkX,
            int chunkZ,
            int cellIndex,
            int radius = 1)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return false;
            }

            ChunkMaskCodec.IndexToLocal(cellIndex, out var lx, out var lz);
            var worldX = chunkX * GridSize + lx;
            var worldZ = chunkZ * GridSize + lz;
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    if (SampleStage(save, worldX + dx, worldZ + dz) == (byte)CellStage.Void)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static byte SampleStage(GameSaveData save, int worldCellX, int worldCellZ)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return (byte)CellStage.Void;
            }

            var grid = GridSize;
            var chunkX = FloorDiv(worldCellX, grid);
            var chunkZ = FloorDiv(worldCellZ, grid);
            var lx = worldCellX - chunkX * grid;
            var lz = worldCellZ - chunkZ * grid;
            if (lx < 0 || lz < 0 || lx >= grid || lz >= grid)
            {
                return (byte)CellStage.Void;
            }

            for (var i = 0; i < save.worldGrowth.chunks.Count; i++)
            {
                var g = save.worldGrowth.chunks[i];
                if (g == null || g.chunkX != chunkX || g.chunkZ != chunkZ)
                {
                    continue;
                }

                if (g.isLegacyBase)
                {
                    return (byte)CellStage.Moss;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(g.stageMaskBase64, out var stages, out _))
                {
                    return (byte)CellStage.Void;
                }

                var idx = lz * grid + lx;
                if (idx < 0 || idx >= stages.Length)
                {
                    return (byte)CellStage.Void;
                }

                return stages[idx];
            }

            return (byte)CellStage.Void;
        }

        private static int FloorDiv(int a, int b)
        {
            var q = a / b;
            var r = a % b;
            if (r != 0 && ((a < 0) ^ (b < 0)))
            {
                q--;
            }

            return q;
        }
    }
}
