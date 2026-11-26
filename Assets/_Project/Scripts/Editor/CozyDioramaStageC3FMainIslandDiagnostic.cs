using AfkIsland.Core;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.Editor
{
    /// <summary>
    /// Read-only Stage C-3F runtime wiring diagnostic.
    /// Run while MainIsland is in Play Mode after Continue / Start Fresh has completed.
    /// Does not write, clone, migrate, or mutate the active save.
    /// </summary>
    public static class CozyDioramaStageC3FMainIslandDiagnostic
    {
        [MenuItem("AFK Island/Cozy Diorama/Stage C-3F/Report MainIsland Runtime Wiring")]
        public static void ReportRuntimeWiring()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("[C-3F] Enter Play Mode in MainIsland, complete Continue/Start Fresh, then run this diagnostic.");
                return;
            }

            var bootstraps = Object.FindObjectsByType<GameBootstrap>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var saves = Object.FindObjectsByType<SaveService>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var chunks = Object.FindObjectsByType<ChunkManager>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var growthServices = Object.FindObjectsByType<WorldGrowthService>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Debug.Log($"[C-3F] MainIsland runtime wiring: bootstraps={bootstraps.Length}, saveServices={saves.Length}, chunkManagers={chunks.Length}, worldGrowthServices={growthServices.Length}");

            for (var i = 0; i < bootstraps.Length; i++)
            {
                var bootstrap = bootstraps[i];
                if (bootstrap == null) continue;

                var visualizer = bootstrap.GetComponent<HomeSettlementVisualizer>();
                var meshVisualizer = bootstrap.GetComponent<IslandMeshVisualizer>();
                var save = bootstrap.GetComponent<SaveService>();
                var chunkManager = bootstrap.GetComponent<ChunkManager>();
                var growth = bootstrap.GetComponent<WorldGrowthService>();

                // GameBootstrap supports serialized references, so report both same-object
                // components and the global SaveService instance without changing ownership.
                var authoritative = SaveService.Instance != null
                    ? SaveService.Instance.AuthoritativeSnapshot
                    : save != null ? save.AuthoritativeSnapshot : null;

                var chunkRecords = authoritative?.worldGrowth?.chunks;
                var activeGrowthChunks = 0;
                if (chunkRecords != null)
                {
                    for (var c = 0; c < chunkRecords.Count; c++)
                    {
                        if (chunkRecords[c] != null) activeGrowthChunks++;
                    }
                }

                var plan = visualizer != null ? visualizer.LastContinuousPathPlan : null;
                var renderer = visualizer != null ? visualizer.ContinuousPathRenderer : null;
                var stats = renderer != null ? renderer.GetStats() : null;

                Debug.Log(
                    $"[C-3F] Bootstrap[{i}] object={bootstrap.gameObject.name}, " +
                    $"visualizer={(visualizer != null)}, islandMeshVisualizer={(meshVisualizer != null)}, " +
                    $"sameObjectSave={(save != null)}, sameObjectChunkManager={(chunkManager != null)}, " +
                    $"sameObjectWorldGrowthService={(growth != null)}, " +
                    $"featureFlag={(visualizer != null && visualizer.UseContinuousPathNetworkRenderer)}, " +
                    $"saveVersion={(authoritative != null ? authoritative.version : -1)}, " +
                    $"growthChunks={activeGrowthChunks}, " +
                    $"lastSimulationUtc={(authoritative?.worldGrowth != null ? authoritative.worldGrowth.lastSimulationUtc : 0)}, " +
                    $"settlementWatermarkUtc={(authoritative?.worldGrowth != null ? authoritative.worldGrowth.settlementWatermarkUtc : 0)}, " +
                    $"planNodes={(plan != null ? plan.Nodes.Count : 0)}, " +
                    $"planEdges={(plan != null ? plan.Edges.Count : 0)}, " +
                    $"hutReports={(plan != null ? plan.HutReports.Count : 0)}, " +
                    $"connectedHuts={(plan != null ? CountConnectedHuts(plan) : 0)}, " +
                    $"rendererChunks={(stats != null ? stats.ActiveChunkCount : 0)}, " +
                    $"ownedMeshes={(stats != null ? stats.LiveOwnedMeshCount : 0)}");
            }

            if (bootstraps.Length != 1)
            {
                Debug.LogWarning("[C-3F] Expected exactly one active GameBootstrap in MainIsland.");
            }

            if (saves.Length != 1 || chunks.Length != 1 || growthServices.Length != 1)
            {
                Debug.LogWarning("[C-3F] Runtime system counts differ from the expected single-instance MainIsland setup. Inspect scene/bootstrap wiring.");
            }

            Debug.Log("[C-3F] Read-only diagnostic complete. No save fields or gameplay state were changed.");
        }

        private static int CountConnectedHuts(SettlementPathTopology.TopologyPlan plan)
        {
            var count = 0;
            for (var i = 0; i < plan.HutReports.Count; i++)
            {
                var status = plan.HutReports[i].Status;
                if (status == SettlementPathTopology.HutConnectionStatus.Connected ||
                    status == SettlementPathTopology.HutConnectionStatus.ConnectedWithVisualDoorCorrection)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
