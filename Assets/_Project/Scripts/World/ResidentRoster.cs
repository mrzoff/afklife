using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>One resident derived from a hut cell. Not stored in the save.</summary>
    public sealed class ResidentEntry
    {
        public string residentId;
        public int chunkX;
        public int chunkZ;
        public int cellIndex;
        public int slot;
        public Vector3 homeWorld;
        /// <summary>Where the resident stands. A hut is wider than a cell, so this is the
        /// nearest walkable neighbour cell instead of the hut centre.</summary>
        public Vector3 standWorld;
    }

    /// <summary>
    /// Reads the settlement mask and derives residents from hut cells.
    /// Save version stays 8: nothing here is persisted, so a hut that disappears
    /// takes its resident with it and a restart produces the same roster.
    /// </summary>
    public static class ResidentRoster
    {
        /// <summary>Residents living in one hut cell.</summary>
        public const int ResidentsPerHut = 1;

        /// <summary>Half the capsule height: lifts the pivot from the ground to the body centre.</summary>
        public const float StandHeightOffset = 0.45f;

        public static List<ResidentEntry> Build(GameSaveData save)
        {
            var list = new List<ResidentEntry>();
            if (save?.worldGrowth?.chunks == null)
            {
                return list;
            }

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

                    var home = GrowthWorldPlacement.CellWorldPosition(
                        growth.chunkX,
                        growth.chunkZ,
                        i,
                        (byte)CellStage.Moss,
                        growth.isLegacyBase);

                    var stand = FindStandPoint(save, growth, i, home);

                    for (var slot = 0; slot < ResidentsPerHut; slot++)
                    {
                        list.Add(new ResidentEntry
                        {
                            residentId = MakeResidentId(growth.chunkX, growth.chunkZ, i, slot),
                            chunkX = growth.chunkX,
                            chunkZ = growth.chunkZ,
                            cellIndex = i,
                            slot = slot,
                            homeWorld = home,
                            standWorld = stand
                        });
                    }
                }
            }

            list.Sort((a, b) => string.CompareOrdinal(a.residentId, b.residentId));
            return list;
        }

        public static int Count(GameSaveData save)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return 0;
            }

            var huts = 0;
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null ||
                    !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                {
                    continue;
                }

                for (var i = 0; i < settle.Length; i++)
                {
                    if (settle[i] == (byte)SettlementStage.Hut)
                    {
                        huts++;
                    }
                }
            }

            return huts * ResidentsPerHut;
        }

        public static string MakeResidentId(int chunkX, int chunkZ, int cellIndex, int slot) =>
            $"res_{chunkX}_{chunkZ}_{cellIndex}_{slot}";

        /// <summary>
        /// A hut is wider than one cell, so a resident standing on the hut centre is hidden
        /// inside the building. Stand on the nearest path/stone neighbour; fall back to a
        /// fixed offset when the hut has no road yet.
        /// </summary>
        private static Vector3 FindStandPoint(
            GameSaveData save,
            ChunkGrowthSaveData growth,
            int cellIndex,
            Vector3 home)
        {
            ChunkMaskCodec.IndexToLocal(cellIndex, out var lx, out var lz);
            var wx = growth.chunkX * ChunkMaskCodec.GridSize + lx;
            var wz = growth.chunkZ * ChunkMaskCodec.GridSize + lz;

            // Pass 1: prefer a road cell.
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    if (SettlementSimulation.SampleSettlementStage(save, wx + dx, wz + dz) != (byte)SettlementStage.Path)
                    {
                        continue;
                    }

                    return WorldCellCenter(wx + dx, wz + dz, home.y);
                }
            }

            // Pass 2: prefer a stone cell.
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    if (SettlementSimulation.SampleSettlementStage(save, wx + dx, wz + dz) != (byte)SettlementStage.Stone)
                    {
                        continue;
                    }

                    return WorldCellCenter(wx + dx, wz + dz, home.y);
                }
            }

            // Pass 3: fallback — step away from the hut centre until we find a non-hut cell.
            for (var dist = 1; dist <= 3; dist++)
            {
                for (var a = 0; a < 360; a += 30)
                {
                    var rad = a * Mathf.Deg2Rad;
                    var twx = Mathf.RoundToInt(wx + Mathf.Cos(rad) * dist);
                    var twz = Mathf.RoundToInt(wz + Mathf.Sin(rad) * dist);
                    var stage = SettlementSimulation.SampleSettlementStage(save, twx, twz);
                    if (stage != (byte)SettlementStage.Hut)
                    {
                        return WorldCellCenter(twx, twz, home.y);
                    }
                }
            }

            return home + new Vector3(0f, StandHeightOffset, 1.05f);
        }

        private static Vector3 WorldCellCenter(int worldCellX, int worldCellZ, float groundY)
        {
            var grid = ChunkMaskCodec.GridSize;
            var chunkX = worldCellX >= 0 ? worldCellX / grid : (worldCellX - grid + 1) / grid;
            var chunkZ = worldCellZ >= 0 ? worldCellZ / grid : (worldCellZ - grid + 1) / grid;
            var lx = worldCellX - chunkX * grid;
            var lz = worldCellZ - chunkZ * grid;
            var idx = ChunkMaskCodec.CellIndex(lx, lz);
            var center = GrowthWorldPlacement.CellCenterWorld(chunkX, chunkZ, idx);
            center.y = groundY + StandHeightOffset;
            return center;
        }
    }
}
