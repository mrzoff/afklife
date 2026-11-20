using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Walks the settlement mask to build walking routes between huts.
    /// Paths and stones are walkable; a hut is walkable only as the destination,
    /// so residents walk around houses instead of through them.
    /// Results are cached per hut pair until the save mask changes.
    /// </summary>
    public sealed class ResidentPathGraph
    {
        /// <summary>Search budget per pair. Keeps BFS cheap on big settlements.</summary>
        public const int MaxVisitedCells = 96;

        private readonly Dictionary<long, List<Vector3>> _cache = new();
        private GameSaveData _bound;
        private string _boundSignature;

        public void Bind(GameSaveData save)
        {
            var signature = BuildSignature(save);
            if (ReferenceEquals(_bound, save) && signature == _boundSignature)
            {
                return;
            }

            _bound = save;
            _boundSignature = signature;
            _cache.Clear();
        }

        public bool TryFindPath(ResidentEntry from, ResidentEntry to, List<Vector3> outPoints)
        {
            outPoints?.Clear();
            if (from == null || to == null || outPoints == null || _bound == null)
            {
                return false;
            }

            if (from.chunkX == to.chunkX && from.chunkZ == to.chunkZ && from.cellIndex == to.cellIndex)
            {
                return false;
            }

            var key = MakeKey(from, to);
            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached == null)
                {
                    return false;
                }

                outPoints.AddRange(cached);
                return true;
            }

            var found = Search(from, to, outPoints);
            _cache[key] = found ? new List<Vector3>(outPoints) : null;
            return found;
        }

        private bool Search(ResidentEntry from, ResidentEntry to, List<Vector3> outPoints)
        {
            var startX = 0;
            var startZ = 0;
            var goalX = 0;
            var goalZ = 0;

            if (!TryWorldCell(from.standWorld, from, out startX, out startZ) ||
                !TryWorldCell(to.standWorld, to, out goalX, out goalZ))
            {
                return false;
            }

            if (startX == goalX && startZ == goalZ)
            {
                return false;
            }

            var queue = new Queue<(int x, int z)>();
            var cameFrom = new Dictionary<long, long>();
            queue.Enqueue((startX, startZ));
            cameFrom[Pack(startX, startZ)] = Pack(startX, startZ);

            var visited = 0;
            var reached = false;

            while (queue.Count > 0 && visited < MaxVisitedCells)
            {
                var (cx, cz) = queue.Dequeue();
                visited++;
                if (cx == goalX && cz == goalZ)
                {
                    reached = true;
                    break;
                }

                TryEnqueue(cx, cz, cx + 1, cz);
                TryEnqueue(cx, cz, cx - 1, cz);
                TryEnqueue(cx, cz, cx, cz + 1);
                TryEnqueue(cx, cz, cx, cz - 1);
            }

            if (!reached)
            {
                return false;
            }

            // Walk the parent chain back from the goal.
            var cells = new List<(int x, int z)>(16);
            var cur = Pack(goalX, goalZ);
            var guard = 0;
            while (guard++ < MaxVisitedCells * 2)
            {
                Unpack(cur, out var ux, out var uz);
                cells.Add((ux, uz));
                var parent = cameFrom[cur];
                if (parent == cur)
                {
                    break;
                }

                cur = parent;
            }

            cells.Reverse();
            var y = from.standWorld.y;
            for (var i = 0; i < cells.Count; i++)
            {
                var world = WorldCellCenter(cells[i].x, cells[i].z);
                world.y = y;
                outPoints.Add(world);
            }

            // Finish on the exact stand point so the resident stops beside the hut, not inside it.
            outPoints.Add(new Vector3(to.standWorld.x, y, to.standWorld.z));
            return outPoints.Count > 1;

            void TryEnqueue(int fromX, int fromZ, int wx, int wz)
            {
                var packed = Pack(wx, wz);
                if (cameFrom.ContainsKey(packed))
                {
                    return;
                }

                var isGoal = wx == goalX && wz == goalZ;
                if (!isGoal && !IsWalkable(wx, wz))
                {
                    return;
                }

                cameFrom[packed] = Pack(fromX, fromZ);
                queue.Enqueue((wx, wz));
            }
        }

        private bool IsWalkable(int worldCellX, int worldCellZ)
        {
            var stage = SettlementSimulation.SampleSettlementStage(_bound, worldCellX, worldCellZ);
            return stage == (byte)SettlementStage.Path || stage == (byte)SettlementStage.Stone;
        }

        /// <summary>
        /// Cell of a stand point. Falls back to the hut cell when the point is not on the grid.
        /// </summary>
        private static bool TryWorldCell(Vector3 point, ResidentEntry entry, out int worldCellX, out int worldCellZ)
        {
            if (SettlementCellQuery.TryWorldToCell(
                    point.x, point.z, out var chunkX, out var chunkZ, out var cellIndex))
            {
                ChunkMaskCodec.IndexToLocal(cellIndex, out var lx, out var lz);
                worldCellX = chunkX * ChunkMaskCodec.GridSize + lx;
                worldCellZ = chunkZ * ChunkMaskCodec.GridSize + lz;
                return true;
            }

            if (entry == null)
            {
                worldCellX = 0;
                worldCellZ = 0;
                return false;
            }

            ChunkMaskCodec.IndexToLocal(entry.cellIndex, out var elx, out var elz);
            worldCellX = entry.chunkX * ChunkMaskCodec.GridSize + elx;
            worldCellZ = entry.chunkZ * ChunkMaskCodec.GridSize + elz;
            return true;
        }

        private static Vector3 WorldCellCenter(int worldCellX, int worldCellZ)
        {
            var grid = ChunkMaskCodec.GridSize;
            var chunkX = worldCellX >= 0
                ? worldCellX / grid
                : (worldCellX - grid + 1) / grid;
            var chunkZ = worldCellZ >= 0
                ? worldCellZ / grid
                : (worldCellZ - grid + 1) / grid;
            var lx = worldCellX - chunkX * grid;
            var lz = worldCellZ - chunkZ * grid;
            var cellIndex = ChunkMaskCodec.CellIndex(lx, lz);
            return GrowthWorldPlacement.CellCenterWorld(chunkX, chunkZ, cellIndex);
        }

        private static long MakeKey(ResidentEntry from, ResidentEntry to) =>
            (PackWorldCell(from.chunkX, from.chunkZ, from.cellIndex) * 1000003L) ^
            PackWorldCell(to.chunkX, to.chunkZ, to.cellIndex);

        private static long PackWorldCell(int chunkX, int chunkZ, int cellIndex)
        {
            ChunkMaskCodec.IndexToLocal(cellIndex, out var lx, out var lz);
            return Pack(chunkX * ChunkMaskCodec.GridSize + lx, chunkZ * ChunkMaskCodec.GridSize + lz);
        }

        private static long Pack(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static void Unpack(long packed, out int x, out int z)
        {
            x = (int)(packed >> 32);
            z = (int)(packed & 0xFFFFFFFF);
        }

        private static string BuildSignature(GameSaveData save)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return "empty";
            }

            var parts = new List<string>(save.worldGrowth.chunks.Count);
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth != null)
                {
                    parts.Add($"{growth.chunkX}:{growth.chunkZ}:{growth.settlementMaskBase64}");
                }
            }

            parts.Sort();
            return string.Join("|", parts);
        }
    }
}
