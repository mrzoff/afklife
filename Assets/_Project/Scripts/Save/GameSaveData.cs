using System;
using System.Collections.Generic;

namespace AfkIsland.Save
{
    [Serializable]
    public sealed class GameSaveData
    {
        public int version = 8;
        public long lastSaveUtc;

        /// <summary>Legacy/root mirror. Canonical plant ownership is in chunks.</summary>
        public List<PlantSaveData> plants = new();

        public List<ChunkSaveData> chunks = new();
        public WorldGrowthSaveData worldGrowth;

        /// <summary>Living ecosystem metrics (v6+).</summary>
        public EcosystemSaveData ecosystem;

        /// <summary>Day/night + simple weather (v6+).</summary>
        public WeatherSaveData weather;

        /// <summary>Spawned fauna instances (v6+).</summary>
        public List<AnimalSaveData> animals = new();

        /// <summary>Species habitats / populations (v7+).</summary>
        public List<HabitatSaveData> habitats = new();

        /// <summary>
        /// Discovery progress only (Contract 0.3). Catalog traits live in FishData / other SOs.
        /// Additive on save v8 — missing field → EnsureCollectionAndFishingDefaults.
        /// </summary>
        public CollectionSaveData collection;

        /// <summary>
        /// Active fishing aggregates only. Additive on save v8.
        /// </summary>
        public FishingSaveData fishing;
    }

    /// <summary>Progress journal. Sole discovery canon for Contract 0.3.</summary>
    [Serializable]
    public sealed class CollectionSaveData
    {
        public List<CollectionEntrySaveData> entries = new();
    }

    [Serializable]
    public sealed class CollectionEntrySaveData
    {
        public string entryId;
        public bool discovered;
        public int discoveryCount;
        public long firstDiscoveredUtc;
        public long lastDiscoveredUtc;
    }

    [Serializable]
    public sealed class FishingSaveData
    {
        /// <summary>Successful catch count only — no fish ID list.</summary>
        public int totalCatches;
    }

    [Serializable]
    public sealed class EcosystemSaveData
    {
        public float humidity = 0.5f;
        public float fertility = 0.5f;
        public float biodiversity = 0.2f;
        public float balance = 0.5f;
    }

    [Serializable]
    public sealed class WeatherSaveData
    {
        /// <summary>0 = midnight, 0.5 = noon, wraps daily.</summary>
        public float timeOfDay01 = 0.5f;

        /// <summary>"sunny", "rain", "fog", or "wind".</summary>
        public string weatherId = "sunny";

        public long lastAdvanceUtc;

        /// <summary>Session accumulators reset/read for return report (v7+).</summary>
        public float rainHours;
        public int windEvents;
        public float fogHours;
    }

    [Serializable]
    public sealed class AnimalSaveData
    {
        public string instanceId;
        public string animalId;
        public float posX;
        public float posY;
        public float posZ;
        public byte state;
        public long spawnedUtc;
        public int habitatChunkX;
        public int habitatChunkZ;
        public int generation;
        public string populationId;
    }

    [Serializable]
    public sealed class HabitatSaveData
    {
        public int chunkX;
        public int chunkZ;
        public string speciesId;
        public string biomeAffinity;
        public int capacity = 4;
        public int count;
        public string populationId;
    }

    public enum AnimalMoveState : byte
    {
        Idle = 0,
        Move = 1
    }

    [Serializable]
    public sealed class PlantSaveData
    {
        public string instanceId;
        public string plantId;
        public string plotId;
        public int stage;
        public float growthProgress;
        public float posX;
        public float posY;
        public float posZ;
    }

    [Serializable]
    public sealed class ChunkSaveData
    {
        public int chunkX;
        public int chunkZ;
        public bool isUnlocked;
        public string biomeId;
        public int randomSeed;
        public long lastSimulatedUtc;
        public List<PlantSaveData> plants = new();
    }

    [Serializable]
    public sealed class WorldGrowthSaveData
    {
        public long lastSimulationUtc;
        public long totalSimulatedSeconds;
        public float edgeGrowthProgress;
        public float[] growthSectorWeights = new float[8];
        public int growthSeed;
        public float totalExpandedArea;
        /// <summary>
        /// Idempotency: colonization already applied for this lastSimulationUtc watermark.
        /// </summary>
        public long natureColonizationWatermarkUtc;

        /// <summary>Idempotency: settlement growth applied for this lastSimulationUtc (v8+).</summary>
        public long settlementWatermarkUtc;

        public List<ChunkGrowthSaveData> chunks = new();
        public List<NatureEventSaveData> events = new();
    }

    [Serializable]
    public sealed class ChunkGrowthSaveData
    {
        public int chunkX;
        public int chunkZ;

        /// <summary>Base64 of exactly 96 bytes: 256 cells × 3 bits.</summary>
        public string stageMaskBase64;

        /// <summary>Base64 of exactly 64 bytes: 256 cells × 2 bits (BiomeType). Sticky; v5+.</summary>
        public string biomeMaskBase64;

        /// <summary>Base64 of 96 bytes: 256 cells × 3 bits (SettlementStage). v8+.</summary>
        public string settlementMaskBase64;

        public List<CellGrowthProgressSaveData> cellProgress = new();
        public int growthSeed;
        public bool isLegacyBase;
    }

    [Serializable]
    public sealed class CellGrowthProgressSaveData
    {
        public int cellIndex;
        public int progressQ16;
    }

    [Serializable]
    public sealed class NatureEventSaveData
    {
        public string eventId;
        public string instanceId;
        public byte status;
        public long createdUtc;
        public long expiresUtc;
        public int rewardSeed;
        public bool rewardClaimed;
        public int chunkX;
        public int chunkZ;
        public float localX;
        public float localZ;

        /// <summary>Player-facing title stored so reports stay stable after catalog edits.</summary>
        public string titleRu;

        /// <summary>Player-facing detail line.</summary>
        public string detailRu;

        public int cellsAffected;
    }

    public enum CellStage : byte
    {
        Void = 0,
        Roots = 1,
        Soil = 2,
        Moss = 3,
        Vegetation = 4
    }

    /// <summary>Settlement build-up on land (separate from CellStage). Packed via ChunkMaskCodec.</summary>
    public enum SettlementStage : byte
    {
        None = 0,
        Clearing = 1,
        Path = 2,
        Stone = 3,
        Hut = 4
    }

    public enum NatureEventStatus : byte
    {
        Created = 0,
        Available = 1,
        Completed = 2,
        Expired = 3,
        Resolved = 4
    }
}
