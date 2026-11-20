using System.Collections.Generic;
using AfkIsland.Plants;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Logical chunk bound to the existing island. Does not relocate scene visuals.
    /// </summary>
    public sealed class IslandChunk : MonoBehaviour
    {
        public const int HomeChunkX = 0;
        public const int HomeChunkZ = 0;
        public const float DefaultSizeMeters = 16f;

        [SerializeField] private int chunkX = HomeChunkX;
        [SerializeField] private int chunkZ = HomeChunkZ;
        [SerializeField] private bool isUnlocked = true;
        [SerializeField] private string biomeId = "starter_island";
        [SerializeField] private int randomSeed;
        [SerializeField] private float sizeMeters = DefaultSizeMeters;
        [SerializeField] private List<PlantPlot> plots = new();

        public int ChunkX => chunkX;
        public int ChunkZ => chunkZ;
        public bool IsUnlocked => isUnlocked;
        public string BiomeId => biomeId;
        public int RandomSeed => randomSeed;
        public float SizeMeters => sizeMeters;
        public IReadOnlyList<PlantPlot> Plots => plots;
        public string ChunkId => $"Chunk_{chunkX}_{chunkZ}";

        public void Configure(
            int x,
            int z,
            bool unlocked,
            string biome,
            int seed,
            IEnumerable<PlantPlot> plotList,
            float size = DefaultSizeMeters)
        {
            chunkX = x;
            chunkZ = z;
            isUnlocked = unlocked;
            biomeId = string.IsNullOrWhiteSpace(biome) ? "starter_island" : biome;
            randomSeed = seed;
            sizeMeters = size;
            plots = plotList != null ? new List<PlantPlot>(plotList) : new List<PlantPlot>();
            gameObject.name = ChunkId;
        }

        public void SetUnlocked(bool unlocked)
        {
            isUnlocked = unlocked;
        }

        public ChunkSaveData Capture(long simulatedUtc)
        {
            var save = new ChunkSaveData
            {
                chunkX = chunkX,
                chunkZ = chunkZ,
                isUnlocked = isUnlocked,
                biomeId = biomeId,
                randomSeed = randomSeed,
                lastSimulatedUtc = simulatedUtc,
                plants = new List<PlantSaveData>()
            };

            foreach (var plot in plots)
            {
                if (plot == null || !plot.IsOccupied || plot.CurrentPlant == null)
                {
                    continue;
                }

                var plant = plot.CurrentPlant;
                save.plants.Add(new PlantSaveData
                {
                    instanceId = plant.InstanceId,
                    plantId = plant.Data != null ? plant.Data.plantId : "unknown",
                    plotId = plot.PlotId,
                    stage = plant.StageIndex,
                    growthProgress = plant.StageProgress,
                    posX = plant.transform.position.x,
                    posY = plant.transform.position.y,
                    posZ = plant.transform.position.z
                });
            }

            return save;
        }
    }
}
