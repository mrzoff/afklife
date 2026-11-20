using System.Collections.Generic;

namespace AfkIsland.Save
{
    public static class SaveClone
    {
        public static GameSaveData DeepClone(GameSaveData source)
        {
            if (source == null)
            {
                return null;
            }

            var clone = new GameSaveData
            {
                version = source.version,
                lastSaveUtc = source.lastSaveUtc,
                plants = ClonePlants(source.plants),
                chunks = CloneChunks(source.chunks),
                worldGrowth = CloneWorldGrowth(source.worldGrowth),
                ecosystem = CloneEcosystem(source.ecosystem),
                weather = CloneWeather(source.weather),
                animals = CloneAnimals(source.animals),
                habitats = CloneHabitats(source.habitats),
                collection = CloneCollection(source.collection),
                fishing = CloneFishing(source.fishing)
            };
            return clone;
        }

        private static CollectionSaveData CloneCollection(CollectionSaveData source)
        {
            if (source == null)
            {
                return null;
            }

            var clone = new CollectionSaveData
            {
                entries = new List<CollectionEntrySaveData>()
            };
            if (source.entries == null)
            {
                return clone;
            }

            foreach (var entry in source.entries)
            {
                if (entry != null)
                {
                    clone.entries.Add(SaveMigrationService.CloneCollectionEntry(entry));
                }
            }

            return clone;
        }

        private static FishingSaveData CloneFishing(FishingSaveData source)
        {
            if (source == null)
            {
                return null;
            }

            return new FishingSaveData
            {
                totalCatches = source.totalCatches
            };
        }

        private static EcosystemSaveData CloneEcosystem(EcosystemSaveData source)
        {
            if (source == null)
            {
                return null;
            }

            return new EcosystemSaveData
            {
                humidity = source.humidity,
                fertility = source.fertility,
                biodiversity = source.biodiversity,
                balance = source.balance
            };
        }

        private static WeatherSaveData CloneWeather(WeatherSaveData source)
        {
            if (source == null)
            {
                return null;
            }

            return new WeatherSaveData
            {
                timeOfDay01 = source.timeOfDay01,
                weatherId = source.weatherId,
                lastAdvanceUtc = source.lastAdvanceUtc,
                rainHours = source.rainHours,
                windEvents = source.windEvents,
                fogHours = source.fogHours
            };
        }

        private static List<AnimalSaveData> CloneAnimals(List<AnimalSaveData> source)
        {
            var list = new List<AnimalSaveData>();
            if (source == null)
            {
                return list;
            }

            foreach (var a in source)
            {
                if (a != null)
                {
                    list.Add(SaveMigrationService.CloneAnimal(a));
                }
            }

            return list;
        }

        private static List<HabitatSaveData> CloneHabitats(List<HabitatSaveData> source)
        {
            var list = new List<HabitatSaveData>();
            if (source == null)
            {
                return list;
            }

            foreach (var h in source)
            {
                if (h != null)
                {
                    list.Add(SaveMigrationService.CloneHabitat(h));
                }
            }

            return list;
        }

        private static List<PlantSaveData> ClonePlants(List<PlantSaveData> source)
        {
            var list = new List<PlantSaveData>();
            if (source == null)
            {
                return list;
            }

            foreach (var plant in source)
            {
                if (plant != null)
                {
                    list.Add(SaveMigrationService.ClonePlant(plant));
                }
            }

            return list;
        }

        private static List<ChunkSaveData> CloneChunks(List<ChunkSaveData> source)
        {
            var list = new List<ChunkSaveData>();
            if (source == null)
            {
                return list;
            }

            foreach (var chunk in source)
            {
                if (chunk == null)
                {
                    continue;
                }

                list.Add(new ChunkSaveData
                {
                    chunkX = chunk.chunkX,
                    chunkZ = chunk.chunkZ,
                    isUnlocked = chunk.isUnlocked,
                    biomeId = chunk.biomeId,
                    randomSeed = chunk.randomSeed,
                    lastSimulatedUtc = chunk.lastSimulatedUtc,
                    plants = ClonePlants(chunk.plants)
                });
            }

            return list;
        }

        private static WorldGrowthSaveData CloneWorldGrowth(WorldGrowthSaveData source)
        {
            if (source == null)
            {
                return null;
            }

            var clone = new WorldGrowthSaveData
            {
                lastSimulationUtc = source.lastSimulationUtc,
                totalSimulatedSeconds = source.totalSimulatedSeconds,
                edgeGrowthProgress = source.edgeGrowthProgress,
                growthSeed = source.growthSeed,
                totalExpandedArea = source.totalExpandedArea,
                natureColonizationWatermarkUtc = source.natureColonizationWatermarkUtc,
                settlementWatermarkUtc = source.settlementWatermarkUtc,
                growthSectorWeights = source.growthSectorWeights != null
                    ? (float[])source.growthSectorWeights.Clone()
                    : new float[8],
                chunks = new List<ChunkGrowthSaveData>(),
                events = new List<NatureEventSaveData>()
            };

            if (source.chunks != null)
            {
                foreach (var c in source.chunks)
                {
                    if (c == null)
                    {
                        continue;
                    }

                    clone.chunks.Add(new ChunkGrowthSaveData
                    {
                        chunkX = c.chunkX,
                        chunkZ = c.chunkZ,
                        stageMaskBase64 = c.stageMaskBase64,
                        biomeMaskBase64 = c.biomeMaskBase64,
                        settlementMaskBase64 = c.settlementMaskBase64,
                        growthSeed = c.growthSeed,
                        isLegacyBase = c.isLegacyBase,
                        cellProgress = CloneProgress(c.cellProgress)
                    });
                }
            }

            if (source.events != null)
            {
                foreach (var e in source.events)
                {
                    if (e == null)
                    {
                        continue;
                    }

                    clone.events.Add(new NatureEventSaveData
                    {
                        eventId = e.eventId,
                        instanceId = e.instanceId,
                        status = e.status,
                        createdUtc = e.createdUtc,
                        expiresUtc = e.expiresUtc,
                        rewardSeed = e.rewardSeed,
                        rewardClaimed = e.rewardClaimed,
                        chunkX = e.chunkX,
                        chunkZ = e.chunkZ,
                        localX = e.localX,
                        localZ = e.localZ,
                        titleRu = e.titleRu,
                        detailRu = e.detailRu,
                        cellsAffected = e.cellsAffected
                    });
                }
            }

            return clone;
        }

        private static List<CellGrowthProgressSaveData> CloneProgress(List<CellGrowthProgressSaveData> source)
        {
            var list = new List<CellGrowthProgressSaveData>();
            if (source == null)
            {
                return list;
            }

            foreach (var p in source)
            {
                if (p == null)
                {
                    continue;
                }

                list.Add(new CellGrowthProgressSaveData
                {
                    cellIndex = p.cellIndex,
                    progressQ16 = p.progressQ16
                });
            }

            return list;
        }
    }
}
