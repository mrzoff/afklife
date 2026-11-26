using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// C-3I: the authored starter settlement of MainIsland. One hut on the west bank of the home pond,
    /// a small clearing pad beside it, and a short road that becomes the first node of the future network.
    ///
    /// This is initial save DATA, not a simulation rule. SettlementSimulation keeps the strict
    /// "Path needs a cardinal road neighbour" guard with no seed exception: later roads grow from this
    /// real road, because it exists as ordinary Path cells in the settlement mask of the home chunk.
    ///
    /// Layout (chunk 0,0 is centred on the world origin, 1 m cells, world = cell - 7.5):
    ///   Hut   (7,6)            world (-0.5, -1.5)   pond bank, ~1.2 m from the water edge
    ///   Pad   (6,5)(7,5)(6,6)(6,7)  Clearing, west of the hut
    ///   Path  (7,7)(7,8)(7,9)  world (-0.5, -0.5 .. 1.5), runs north from the hut door
    /// Every cell is checked against WaterSourceLayout.BlocksSettlementCell, so the seed can never be
    /// scrubbed by SettlementSimulation.Apply (ScrubSettlementInPond). Save version stays 8; no schema change.
    /// </summary>
    public static class StarterSettlementSeed
    {
        public const int HomeChunkX = 0;
        public const int HomeChunkZ = 0;

        /// <summary>Distance around starter cell centres where authored scene props are pushed away.</summary>
        public const float PropClearanceRadius = 0.9f;

        private static readonly Vector2Int HutCell = new Vector2Int(7, 6);

        private static readonly Vector2Int[] PadCells =
        {
            new Vector2Int(6, 5),
            new Vector2Int(7, 5),
            new Vector2Int(6, 6),
            new Vector2Int(6, 7)
        };

        private static readonly Vector2Int[] PathCells =
        {
            new Vector2Int(7, 7),
            new Vector2Int(7, 8),
            new Vector2Int(7, 9)
        };

        /// <summary>World XZ of every starter cell centre (hut, pad, path). Used to keep authored props clear.</summary>
        public static IReadOnlyList<Vector2> GetFootprintCentersXZ()
        {
            var list = new List<Vector2>(1 + PadCells.Length + PathCells.Length);
            AddCenter(list, HutCell);
            for (var i = 0; i < PadCells.Length; i++) AddCenter(list, PadCells[i]);
            for (var i = 0; i < PathCells.Length; i++) AddCenter(list, PathCells[i]);
            return list;
        }

        /// <summary>True when any chunk already carries a Clearing/Path/Stone/Hut cell.</summary>
        public static bool HasAnySettlement(GameSaveData save)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return false;
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
                    if (settle[i] != (byte)SettlementStage.None)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Writes the starter hut, pad and road into the home chunk, but only when the save has no settlement
        /// at all. Idempotent: a save that already has any settlement cell is never touched.
        /// Returns true when cells were written. Does not change save version, limits or watermark.
        /// </summary>
        public static bool SeedIfEmpty(GameSaveData save)
        {
            if (save?.worldGrowth?.chunks == null || HasAnySettlement(save))
            {
                return false;
            }

            ChunkGrowthSaveData home = null;
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth != null && growth.chunkX == HomeChunkX && growth.chunkZ == HomeChunkZ)
                {
                    home = growth;
                    break;
                }
            }

            if (home == null)
            {
                return false;
            }

            byte[] land = null;
            if (!home.isLegacyBase &&
                !ChunkMaskCodec.TryDecodeBase64(home.stageMaskBase64, out land, out _))
            {
                return false;
            }

            if (!ChunkMaskCodec.TryDecodeBase64(home.settlementMaskBase64, out var settle, out _))
            {
                settle = new byte[ChunkMaskCodec.CellCount];
            }

            if (!AllCellsUsable(home, land))
            {
                return false;
            }

            for (var i = 0; i < PadCells.Length; i++)
            {
                settle[ChunkMaskCodec.CellIndex(PadCells[i].x, PadCells[i].y)] = (byte)SettlementStage.Clearing;
            }

            for (var i = 0; i < PathCells.Length; i++)
            {
                settle[ChunkMaskCodec.CellIndex(PathCells[i].x, PathCells[i].y)] = (byte)SettlementStage.Path;
            }

            settle[ChunkMaskCodec.CellIndex(HutCell.x, HutCell.y)] = (byte)SettlementStage.Hut;
            home.settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle);
            return true;
        }

        private static bool AllCellsUsable(ChunkGrowthSaveData home, byte[] land)
        {
            if (!CellUsable(home, land, HutCell))
            {
                return false;
            }

            for (var i = 0; i < PadCells.Length; i++)
            {
                if (!CellUsable(home, land, PadCells[i])) return false;
            }

            for (var i = 0; i < PathCells.Length; i++)
            {
                if (!CellUsable(home, land, PathCells[i])) return false;
            }

            return true;
        }

        private static bool CellUsable(ChunkGrowthSaveData home, byte[] land, Vector2Int cell)
        {
            var index = ChunkMaskCodec.CellIndex(cell.x, cell.y);
            if (WaterSourceLayout.BlocksSettlementCell(home.chunkX, home.chunkZ, index))
            {
                return false;
            }

            var center = GrowthWorldPlacement.CellCenterWorld(home.chunkX, home.chunkZ, index);
            if (!GrowthWorldPlacement.IsInsideAuthoredHomeFootprint(center.x, center.z))
            {
                return false;
            }

            return home.isLegacyBase || (land != null && land[index] >= (byte)CellStage.Moss);
        }

        private static void AddCenter(List<Vector2> list, Vector2Int cell)
        {
            var center = GrowthWorldPlacement.CellCenterWorld(
                HomeChunkX, HomeChunkZ, ChunkMaskCodec.CellIndex(cell.x, cell.y));
            list.Add(new Vector2(center.x, center.z));
        }
    }
}
