using System;
using System.Collections.Generic;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.Save
{
    public static class SaveMigrationService
    {
        public const int CurrentVersion = 8;

        public static GameSaveData MigrateIfNeeded(GameSaveData data)
        {
            if (data == null)
            {
                return null;
            }

            if (data.plants == null)
            {
                data.plants = new List<PlantSaveData>();
            }

            if (data.chunks == null)
            {
                data.chunks = new List<ChunkSaveData>();
            }

            if (data.animals == null)
            {
                data.animals = new List<AnimalSaveData>();
            }

            if (data.habitats == null)
            {
                data.habitats = new List<HabitatSaveData>();
            }

            // Additive v8 fields: null-safe before version gates (old JSON may omit them).
            EnsureCollectionAndFishingDefaults(data);

            if (data.version <= 0)
            {
                data.version = 1;
            }

            if (data.version > CurrentVersion)
            {
                Debug.LogError(
                    $"[AFK Island] Save version {data.version} is newer than supported {CurrentVersion}. " +
                    "Refusing migration/load to avoid data loss.");
                return null;
            }

            if (data.version == 1)
            {
                Debug.Log("[AFK Island] Migrating save v1 -> v2.");
                data.version = 2;
            }

            if (data.version == 2)
            {
                Debug.Log("[AFK Island] Migrating save v2 -> v3 (plants into Chunk_0_0).");
                MigrateV2ToV3(data);
                data.version = 3;
            }

            if (data.version == 3)
            {
                Debug.Log("[AFK Island] Migrating save v3 -> v4 (worldGrowth + masks).");
                if (!MigrateV3ToV4(data))
                {
                    return null;
                }

                data.version = 4;
            }

            if (data.version == 4)
            {
                if (!EnsureWorldGrowthInitialized(data))
                {
                    return null;
                }

                if (!EnsureUniqueChunkCoordinates(data))
                {
                    return null;
                }

                Debug.Log("[AFK Island] Migrating save v4 -> v5 (sticky biome masks).");
                if (!MigrateV4ToV5(data))
                {
                    return null;
                }

                data.version = 5;
            }

            if (data.version == 5)
            {
                if (!EnsureWorldGrowthInitialized(data))
                {
                    return null;
                }

                if (!EnsureUniqueChunkCoordinates(data))
                {
                    return null;
                }

                EnsureAllBiomeMasks(data);
                Debug.Log("[AFK Island] Migrating save v5 -> v6 (ecosystem / weather / fauna).");
                MigrateV5ToV6(data);
                data.version = 6;
            }

            if (data.version == 6)
            {
                if (!EnsureWorldGrowthInitialized(data))
                {
                    return null;
                }

                if (!EnsureUniqueChunkCoordinates(data))
                {
                    return null;
                }

                EnsureAllBiomeMasks(data);
                EnsureEcosystemWeatherAnimals(data);
                Debug.Log("[AFK Island] Migrating save v6 -> v7 (habitats / living offline).");
                MigrateV6ToV7(data);
                data.version = 7;
            }

            if (data.version == 7)
            {
                if (!EnsureWorldGrowthInitialized(data))
                {
                    return null;
                }

                if (!EnsureUniqueChunkCoordinates(data))
                {
                    return null;
                }

                EnsureAllBiomeMasks(data);
                EnsureEcosystemWeatherAnimals(data);
                Debug.Log("[AFK Island] Migrating save v7 -> v8 (settlement masks).");
                MigrateV7ToV8(data);
                data.version = 8;
            }

            if (data.version == 8)
            {
                if (!EnsureWorldGrowthInitialized(data))
                {
                    return null;
                }

                if (!EnsureUniqueChunkCoordinates(data))
                {
                    return null;
                }

                EnsureAllBiomeMasks(data);
                EnsureEcosystemWeatherAnimals(data);
                EnsureAllSettlementMasks(data);
                WaterSourceLayout.ScrubSettlementInPond(data);
                EnsureCollectionAndFishingDefaults(data);
            }

            foreach (var chunk in data.chunks)
            {
                if (chunk?.plants != null)
                {
                    SanitizePlants(chunk.plants);
                }
            }

            SanitizePlants(data.plants);
            SanitizeAnimals(data.animals);
            SanitizeCollectionEntries(data.collection);
            return data;
        }

        /// <summary>
        /// Additive Contract 0.3 defaults on save v8. Never clears existing entries or totalCatches.
        /// Safe to call repeatedly.
        /// </summary>
        public static void EnsureCollectionAndFishingDefaults(GameSaveData data)
        {
            if (data == null)
            {
                return;
            }

            if (data.collection == null)
            {
                data.collection = new CollectionSaveData
                {
                    entries = new List<CollectionEntrySaveData>()
                };
            }
            else if (data.collection.entries == null)
            {
                data.collection.entries = new List<CollectionEntrySaveData>();
            }

            if (data.fishing == null)
            {
                data.fishing = new FishingSaveData
                {
                    totalCatches = 0
                };
            }
            else if (data.fishing.totalCatches < 0)
            {
                data.fishing.totalCatches = 0;
            }
        }

        public static CollectionEntrySaveData CloneCollectionEntry(CollectionEntrySaveData entry)
        {
            return new CollectionEntrySaveData
            {
                entryId = entry.entryId,
                discovered = entry.discovered,
                discoveryCount = entry.discoveryCount,
                firstDiscoveredUtc = entry.firstDiscoveredUtc,
                lastDiscoveredUtc = entry.lastDiscoveredUtc
            };
        }

        private static void SanitizeCollectionEntries(CollectionSaveData collection)
        {
            if (collection?.entries == null)
            {
                return;
            }

            for (var i = collection.entries.Count - 1; i >= 0; i--)
            {
                var entry = collection.entries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.entryId))
                {
                    collection.entries.RemoveAt(i);
                    continue;
                }

                if (entry.discoveryCount < 0)
                {
                    entry.discoveryCount = 0;
                }
            }
        }

        private static void MigrateV5ToV6(GameSaveData data)
        {
            EnsureEcosystemWeatherAnimals(data);
            // Seed metrics from current world/plants once; later sessions recompute in sim.
            EcosystemSimulation.Recalculate(data, raining: false);
        }

        private static void MigrateV7ToV8(GameSaveData data)
        {
            EnsureAllSettlementMasks(data);
            if (data.worldGrowth != null)
            {
                data.worldGrowth.settlementWatermarkUtc = 0;
            }
        }

        public static void EnsureAllSettlementMasks(GameSaveData data)
        {
            if (data?.worldGrowth?.chunks == null)
            {
                return;
            }

            foreach (var growth in data.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(growth.settlementMaskBase64) ||
                    !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out _, out _))
                {
                    growth.settlementMaskBase64 =
                        ChunkMaskCodec.EncodeToBase64(new byte[ChunkMaskCodec.CellCount]);
                }
            }
        }

        private static void MigrateV6ToV7(GameSaveData data)
        {
            EnsureEcosystemWeatherAnimals(data);
            data.habitats ??= new List<HabitatSaveData>();
            if (data.weather != null)
            {
                data.weather.rainHours = 0f;
                data.weather.windEvents = 0;
                data.weather.fogHours = 0f;
            }

            RebuildHabitatsFromAnimals(data);
        }

        public static void RebuildHabitatsFromAnimals(GameSaveData data)
        {
            if (data == null)
            {
                return;
            }

            data.habitats ??= new List<HabitatSaveData>();
            data.habitats.Clear();
            if (data.animals == null)
            {
                return;
            }

            const float chunkSize = 16f;
            foreach (var animal in data.animals)
            {
                if (animal == null || string.IsNullOrWhiteSpace(animal.animalId))
                {
                    continue;
                }

                var cx = animal.habitatChunkX;
                var cz = animal.habitatChunkZ;
                if (cx == 0 && cz == 0 && (Mathf.Abs(animal.posX) > 0.01f || Mathf.Abs(animal.posZ) > 0.01f))
                {
                    cx = Mathf.FloorToInt((animal.posX + chunkSize * 0.5f) / chunkSize);
                    cz = Mathf.FloorToInt((animal.posZ + chunkSize * 0.5f) / chunkSize);
                    animal.habitatChunkX = cx;
                    animal.habitatChunkZ = cz;
                }

                if (string.IsNullOrWhiteSpace(animal.populationId))
                {
                    animal.populationId = $"p_{animal.animalId}_{cx}_{cz}";
                }

                var habitat = FindHabitat(data.habitats, cx, cz, animal.animalId);
                if (habitat == null)
                {
                    habitat = new HabitatSaveData
                    {
                        chunkX = cx,
                        chunkZ = cz,
                        speciesId = animal.animalId,
                        biomeAffinity = AffinityForSpecies(animal.animalId),
                        capacity = 4,
                        count = 0,
                        populationId = animal.populationId
                    };
                    data.habitats.Add(habitat);
                }

                habitat.count++;
                animal.populationId = habitat.populationId;
            }
        }

        public static HabitatSaveData FindHabitat(
            List<HabitatSaveData> habitats, int chunkX, int chunkZ, string speciesId)
        {
            if (habitats == null)
            {
                return null;
            }

            foreach (var h in habitats)
            {
                if (h != null &&
                    h.chunkX == chunkX &&
                    h.chunkZ == chunkZ &&
                    string.Equals(h.speciesId, speciesId, StringComparison.Ordinal))
                {
                    return h;
                }
            }

            return null;
        }

        public static string AffinityForSpecies(string speciesId)
        {
            return speciesId switch
            {
                "butterfly" => "mossy",
                "bird" => "forest",
                "frog" => "coastal",
                _ => "mossy"
            };
        }

        public static void EnsureEcosystemWeatherAnimals(GameSaveData data)
        {
            if (data == null)
            {
                return;
            }

            if (data.ecosystem == null)
            {
                data.ecosystem = CreateDefaultEcosystem();
            }

            if (data.weather == null)
            {
                data.weather = CreateDefaultWeather(data.lastSaveUtc);
            }

            if (data.animals == null)
            {
                data.animals = new List<AnimalSaveData>();
            }

            if (data.habitats == null)
            {
                data.habitats = new List<HabitatSaveData>();
            }

            if (string.IsNullOrWhiteSpace(data.weather.weatherId))
            {
                data.weather.weatherId = "sunny";
            }

            data.weather.timeOfDay01 = Mathf.Repeat(data.weather.timeOfDay01, 1f);
            if (data.weather.lastAdvanceUtc <= 0 && data.lastSaveUtc > 0)
            {
                data.weather.lastAdvanceUtc = data.lastSaveUtc;
            }
        }

        public static EcosystemSaveData CreateDefaultEcosystem()
        {
            return new EcosystemSaveData
            {
                humidity = 0.5f,
                fertility = 0.45f,
                biodiversity = 0.2f,
                balance = 0.5f
            };
        }

        public static WeatherSaveData CreateDefaultWeather(long utc)
        {
            return new WeatherSaveData
            {
                timeOfDay01 = 0.5f,
                weatherId = "sunny",
                lastAdvanceUtc = utc > 0 ? utc : 0
            };
        }

        private static void MigrateV2ToV3(GameSaveData data)
        {
            var home = FindChunk(data.chunks, IslandChunk.HomeChunkX, IslandChunk.HomeChunkZ);
            if (home == null)
            {
                home = CreateHomeChunkSave();
                data.chunks.Add(home);
            }

            if (home.plants == null)
            {
                home.plants = new List<PlantSaveData>();
            }

            if (home.plants.Count == 0 && data.plants.Count > 0)
            {
                home.plants.AddRange(ClonePlants(data.plants));
            }

            home.isUnlocked = true;
            if (string.IsNullOrWhiteSpace(home.biomeId))
            {
                home.biomeId = "starter_island";
            }

            data.plants = ClonePlants(home.plants);
        }

        private static bool MigrateV3ToV4(GameSaveData data)
        {
            if (!EnsureUniqueChunkCoordinates(data))
            {
                return false;
            }

            return EnsureWorldGrowthInitialized(data);
        }

        private static bool MigrateV4ToV5(GameSaveData data)
        {
            if (!EnsureWorldGrowthInitialized(data))
            {
                return false;
            }

            EnsureAllBiomeMasks(data);
            return true;
        }

        private static void EnsureAllBiomeMasks(GameSaveData data)
        {
            if (data.worldGrowth?.chunks == null)
            {
                return;
            }

            foreach (var growth in data.worldGrowth.chunks)
            {
                BiomeSampling.EnsureAndBackfill(growth);
            }
        }

        private static bool EnsureWorldGrowthInitialized(GameSaveData data)
        {
            if (data.worldGrowth == null)
            {
                data.worldGrowth = CreateDefaultWorldGrowth(data.lastSaveUtc);
            }

            if (data.worldGrowth.growthSectorWeights == null ||
                data.worldGrowth.growthSectorWeights.Length != 8)
            {
                data.worldGrowth.growthSectorWeights = CreateDefaultSectorWeights();
            }

            if (data.worldGrowth.chunks == null)
            {
                data.worldGrowth.chunks = new List<ChunkGrowthSaveData>();
            }

            if (data.worldGrowth.events == null)
            {
                data.worldGrowth.events = new List<NatureEventSaveData>();
            }

            if (data.worldGrowth.lastSimulationUtc <= 0 && data.lastSaveUtc > 0)
            {
                data.worldGrowth.lastSimulationUtc = data.lastSaveUtc;
            }

            foreach (var logical in data.chunks)
            {
                if (logical == null)
                {
                    continue;
                }

                if (FindGrowthChunk(data.worldGrowth.chunks, logical.chunkX, logical.chunkZ) != null)
                {
                    continue;
                }

                var isHome = logical.chunkX == IslandChunk.HomeChunkX &&
                             logical.chunkZ == IslandChunk.HomeChunkZ;
                data.worldGrowth.chunks.Add(CreateGrowthRecord(logical.chunkX, logical.chunkZ, isHome));
            }

            // Integrity: every growth record must have a logical chunk in normalized v4.
            for (var i = data.worldGrowth.chunks.Count - 1; i >= 0; i--)
            {
                var growth = data.worldGrowth.chunks[i];
                if (growth == null)
                {
                    data.worldGrowth.chunks.RemoveAt(i);
                    continue;
                }

                if (FindChunk(data.chunks, growth.chunkX, growth.chunkZ) != null)
                {
                    continue;
                }

                Debug.LogError(
                    $"[AFK Island] Growth record without logical chunk ({growth.chunkX},{growth.chunkZ}).");
                return false;
            }

            return true;
        }

        private static bool EnsureUniqueChunkCoordinates(GameSaveData data)
        {
            var seenLogical = new HashSet<long>();
            foreach (var chunk in data.chunks)
            {
                if (chunk == null)
                {
                    continue;
                }

                var key = PackKey(chunk.chunkX, chunk.chunkZ);
                if (!seenLogical.Add(key))
                {
                    Debug.LogError(
                        $"[AFK Island] Duplicate logical chunk coordinates ({chunk.chunkX},{chunk.chunkZ}).");
                    return false;
                }
            }

            if (data.worldGrowth?.chunks == null)
            {
                return true;
            }

            var seenGrowth = new HashSet<long>();
            foreach (var growth in data.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                var key = PackKey(growth.chunkX, growth.chunkZ);
                if (!seenGrowth.Add(key))
                {
                    Debug.LogError(
                        $"[AFK Island] Duplicate growth chunk coordinates ({growth.chunkX},{growth.chunkZ}).");
                    return false;
                }
            }

            return true;
        }

        public static WorldGrowthSaveData CreateDefaultWorldGrowth(long lastSaveUtc)
        {
            return new WorldGrowthSaveData
            {
                lastSimulationUtc = lastSaveUtc > 0 ? lastSaveUtc : 0,
                totalSimulatedSeconds = 0,
                edgeGrowthProgress = 0f,
                growthSectorWeights = CreateDefaultSectorWeights(),
                growthSeed = unchecked((int)(lastSaveUtc ^ 0xA5A5A5A5)),
                totalExpandedArea = ChunkMaskCodec.CellCount,
                chunks = new List<ChunkGrowthSaveData>(),
                events = new List<NatureEventSaveData>()
            };
        }

        public static ChunkGrowthSaveData CreateGrowthRecord(int chunkX, int chunkZ, bool matureLegacyBase)
        {
            var stages = new byte[ChunkMaskCodec.CellCount];
            if (matureLegacyBase)
            {
                for (var i = 0; i < stages.Length; i++)
                {
                    stages[i] = (byte)CellStage.Vegetation;
                }
            }

            var record = new ChunkGrowthSaveData
            {
                chunkX = chunkX,
                chunkZ = chunkZ,
                stageMaskBase64 = ChunkMaskCodec.EncodeToBase64(stages),
                cellProgress = new List<CellGrowthProgressSaveData>(),
                growthSeed = chunkX * 73856093 ^ chunkZ * 19349663,
                isLegacyBase = matureLegacyBase
            };
            BiomeSampling.EnsureAndBackfill(record);
            return record;
        }

        private static ChunkSaveData CreateHomeChunkSave()
        {
            return new ChunkSaveData
            {
                chunkX = IslandChunk.HomeChunkX,
                chunkZ = IslandChunk.HomeChunkZ,
                isUnlocked = true,
                biomeId = "starter_island",
                randomSeed = 0,
                lastSimulatedUtc = 0,
                plants = new List<PlantSaveData>()
            };
        }

        private static float[] CreateDefaultSectorWeights()
        {
            return new float[] { 1f, 0.85f, 1f, 0.85f, 1f, 0.85f, 1f, 0.85f };
        }

        private static ChunkSaveData FindChunk(List<ChunkSaveData> chunks, int x, int z)
        {
            foreach (var chunk in chunks)
            {
                if (chunk != null && chunk.chunkX == x && chunk.chunkZ == z)
                {
                    return chunk;
                }
            }

            return null;
        }

        private static ChunkGrowthSaveData FindGrowthChunk(List<ChunkGrowthSaveData> chunks, int x, int z)
        {
            foreach (var chunk in chunks)
            {
                if (chunk != null && chunk.chunkX == x && chunk.chunkZ == z)
                {
                    return chunk;
                }
            }

            return null;
        }

        private static long PackKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        private static List<PlantSaveData> ClonePlants(List<PlantSaveData> source)
        {
            var list = new List<PlantSaveData>(source.Count);
            foreach (var plant in source)
            {
                if (plant == null)
                {
                    continue;
                }

                list.Add(ClonePlant(plant));
            }

            return list;
        }

        public static PlantSaveData ClonePlant(PlantSaveData plant)
        {
            return new PlantSaveData
            {
                instanceId = plant.instanceId,
                plantId = plant.plantId,
                plotId = plant.plotId,
                stage = plant.stage,
                growthProgress = plant.growthProgress,
                posX = plant.posX,
                posY = plant.posY,
                posZ = plant.posZ
            };
        }

        private static void SanitizePlants(List<PlantSaveData> plants)
        {
            for (var i = plants.Count - 1; i >= 0; i--)
            {
                var plant = plants[i];
                if (plant == null)
                {
                    plants.RemoveAt(i);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(plant.instanceId) ||
                    string.IsNullOrWhiteSpace(plant.plantId))
                {
                    plants.RemoveAt(i);
                    continue;
                }

                plant.growthProgress = Mathf.Clamp01(plant.growthProgress);
                if (plant.stage < 0)
                {
                    plant.stage = 0;
                }

                // Stage 11E: drop plants in the pond reserve or floating off the authored home.
                if (WaterSourceLayout.IsInsideReserveXZ(plant.posX, plant.posZ))
                {
                    plants.RemoveAt(i);
                    continue;
                }

                // Home wild_* plants must stay on the sculpted island footprint (not full 16×16 mask).
                if (PlantColonizationSimulation.IsWildPlotId(plant.plotId) &&
                    plant.plotId.StartsWith("wild_0_0_", System.StringComparison.Ordinal) &&
                    !GrowthWorldPlacement.IsInsideAuthoredHomeFootprint(plant.posX, plant.posZ))
                {
                    plants.RemoveAt(i);
                }
            }
        }

        private static void SanitizeAnimals(List<AnimalSaveData> animals)
        {
            if (animals == null)
            {
                return;
            }

            for (var i = animals.Count - 1; i >= 0; i--)
            {
                var a = animals[i];
                if (a == null ||
                    string.IsNullOrWhiteSpace(a.instanceId) ||
                    string.IsNullOrWhiteSpace(a.animalId))
                {
                    animals.RemoveAt(i);
                }
            }
        }

        public static AnimalSaveData CloneAnimal(AnimalSaveData a)
        {
            return new AnimalSaveData
            {
                instanceId = a.instanceId,
                animalId = a.animalId,
                posX = a.posX,
                posY = a.posY,
                posZ = a.posZ,
                state = a.state,
                spawnedUtc = a.spawnedUtc,
                habitatChunkX = a.habitatChunkX,
                habitatChunkZ = a.habitatChunkZ,
                generation = a.generation,
                populationId = a.populationId
            };
        }

        public static HabitatSaveData CloneHabitat(HabitatSaveData h)
        {
            return new HabitatSaveData
            {
                chunkX = h.chunkX,
                chunkZ = h.chunkZ,
                speciesId = h.speciesId,
                biomeAffinity = h.biomeAffinity,
                capacity = h.capacity,
                count = h.count,
                populationId = h.populationId
            };
        }
    }
}
