#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>CIF-A1: CaptureLogicalChunks must not wipe wild plants when syncing plot Capture.</summary>
    public static class ChunkWildPlantCaptureSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "chunk_wild_plant_capture_smoke_result.txt");

        [MenuItem("AFK Island/Smoke CIF-A1 Wild Plant Capture")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("CIF-A1 wild plant capture smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] CIF-A1 wild plant capture smoke OK.");
        }

        public static void RunBatch()
        {
            Debug.Log("[AFK Island] CIF-A1 wild plant capture smoke START");
            var code = RunInternal();
            try
            {
                var dir = Path.GetDirectoryName(ResultPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(ResultPath, code == 0 ? "OK" : ("FAIL:" + code));
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] Failed to write CIF-A1 smoke result: " + e.Message);
            }

            EditorApplication.Exit(code);
        }

        private static int RunInternal()
        {
            try
            {
                var wild = new PlantSaveData
                {
                    instanceId = "p_wild_0_0_7",
                    plantId = "starter_tree",
                    plotId = "wild_0_0_7",
                    stage = 2,
                    growthProgress = 0.4f,
                    posX = 1f,
                    posY = 2f,
                    posZ = 3f
                };
                var plotA = new PlantSaveData
                {
                    instanceId = "p_plot_01",
                    plantId = "starter_tree",
                    plotId = "plot_01",
                    stage = 1,
                    growthProgress = 0.1f
                };
                var plotB = new PlantSaveData
                {
                    instanceId = "p_plot_02",
                    plantId = "starter_tree",
                    plotId = "plot_02",
                    stage = 3,
                    growthProgress = 0.9f
                };

                // Keep wild + refresh plots when both present.
                var merged = ChunkManager.MergeCapturedPlants(
                    new List<PlantSaveData> { wild, plotA },
                    new List<PlantSaveData> { plotA, plotB });
                if (merged.Count != 3 ||
                    !merged.Exists(p => p.instanceId == "p_wild_0_0_7") ||
                    !merged.Exists(p => p.instanceId == "p_plot_01") ||
                    !merged.Exists(p => p.instanceId == "p_plot_02"))
                {
                    Debug.LogError("A: merge must keep wild and both plot plants.");
                    return 2;
                }

                // Plot emptied → drop old plot plant, keep wild.
                merged = ChunkManager.MergeCapturedPlants(
                    new List<PlantSaveData> { wild, plotA },
                    new List<PlantSaveData>());
                if (merged.Count != 1 || merged[0].instanceId != "p_wild_0_0_7")
                {
                    Debug.LogError("B: empty plot capture must keep wild only.");
                    return 3;
                }

                // Plot-bound must not be treated as wild.
                if (!ChunkManager.IsPlotBoundPlant(plotA) ||
                    ChunkManager.IsPlotBoundPlant(wild) ||
                    ChunkManager.IsPlotBoundPlant(new PlantSaveData { plotId = "" }))
                {
                    Debug.LogError("C: IsPlotBoundPlant classification wrong.");
                    return 4;
                }

                // Unbound (empty plotId) preserved like wild.
                var unbound = new PlantSaveData
                {
                    instanceId = "p_unbound",
                    plantId = "starter_tree",
                    plotId = "",
                    stage = 0
                };
                merged = ChunkManager.MergeCapturedPlants(
                    new List<PlantSaveData> { unbound },
                    new List<PlantSaveData> { plotB });
                if (!merged.Exists(p => p.instanceId == "p_unbound") ||
                    !merged.Exists(p => p.instanceId == "p_plot_02"))
                {
                    Debug.LogError("D: empty plotId plants must be preserved.");
                    return 5;
                }

                // Clone independence.
                merged = ChunkManager.MergeCapturedPlants(
                    new List<PlantSaveData> { wild },
                    new List<PlantSaveData>());
                merged[0].stage = 99;
                if (wild.stage == 99)
                {
                    Debug.LogError("E: merge must clone plants.");
                    return 6;
                }

                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] CIF-A1 smoke exception: " + e);
                return 1;
            }
        }
    }
}
#endif
