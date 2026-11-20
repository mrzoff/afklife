#if UNITY_EDITOR
using System;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    public static class HutCellSmoke
    {
        [MenuItem("AFK Island/Smoke Hut Cell")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Hut cell smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Hut cell smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            var settle = new byte[ChunkMaskCodec.CellCount];
            settle[4] = (byte)SettlementStage.Hut;
            settle[5] = (byte)SettlementStage.Path;
            var growth = new ChunkGrowthSaveData
            {
                chunkX = 1,
                chunkZ = -2,
                settlementMaskBase64 = ChunkMaskCodec.EncodeToBase64(settle)
            };

            if (!SettlementCellQuery.IsHut(growth, 4))
            {
                return 2;
            }

            if (SettlementCellQuery.IsHut(growth, 5))
            {
                return 3;
            }

            if (SettlementCellQuery.IsHut(growth, 1000))
            {
                return 4;
            }

            var plotId = PlantColonizationSimulation.MakeWildPlotId(1, -2, 4);
            if (!SettlementCellQuery.TryParseWildCell(plotId, out var chunkX, out var chunkZ, out var cell) ||
                chunkX != 1 || chunkZ != -2 || cell != 4)
            {
                return 5;
            }

            if (SettlementCellQuery.TryParseWildCell(
                    PlantColonizationSimulation.MakeWildPlotId(0, 0, 1000), out _, out _, out _))
            {
                return 6;
            }

            var save = new GameSaveData
            {
                worldGrowth = new WorldGrowthSaveData
                {
                    chunks = new System.Collections.Generic.List<ChunkGrowthSaveData> { growth }
                }
            };
            if (!SettlementCellQuery.IsHut(save, 1, -2, 4) || SettlementCellQuery.IsHut(save, 0, 0, 4))
            {
                return 7;
            }

            var hutCenter = GrowthWorldPlacement.CellCenterWorld(1, -2, 4);
            if (!SettlementCellQuery.IsHutAtWorldXZ(save, hutCenter.x, hutCenter.z))
            {
                return 8;
            }

            var pathCenter = GrowthWorldPlacement.CellCenterWorld(1, -2, 5);
            if (SettlementCellQuery.IsHutAtWorldXZ(save, pathCenter.x, pathCenter.z))
            {
                return 9;
            }

            if (!SettlementCellQuery.TryWorldToCell(
                    hutCenter.x, hutCenter.z, out var roundChunkX, out var roundChunkZ, out var roundCell) ||
                roundChunkX != 1 || roundChunkZ != -2 || roundCell != 4)
            {
                return 10;
            }

            Debug.Log("[AFK Island] Hut cell: tall decor and wild plants skip the hut cell.");
            return 0;
        }
    }
}
#endif
