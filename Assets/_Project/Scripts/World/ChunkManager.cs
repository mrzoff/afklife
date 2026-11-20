using System.Collections.Generic;
using AfkIsland.Plants;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    public sealed class ChunkManager : MonoBehaviour
    {
        [SerializeField] private IslandChunk homeChunk;
        [SerializeField] private List<IslandChunk> loadedChunks = new();
        [SerializeField] private float chunkSizeMeters = IslandChunk.DefaultSizeMeters;

        private readonly Dictionary<long, ChunkSaveData> _logical = new();
        private readonly Dictionary<long, IslandChunk> _loaded = new();

        public IslandChunk HomeChunk => homeChunk;
        public float ChunkSizeMeters => chunkSizeMeters;
        public IReadOnlyCollection<IslandChunk> LoadedChunks => _loaded.Values;

        public void ConfigureHomeChunk(IslandChunk chunk, IEnumerable<PlantPlot> plots)
        {
            homeChunk = chunk;
            if (chunk == null)
            {
                return;
            }

            chunk.Configure(
                IslandChunk.HomeChunkX,
                IslandChunk.HomeChunkZ,
                true,
                "starter_island",
                0,
                plots,
                chunkSizeMeters);

            RegisterLoadedChunk(chunk);
            EnsureLogicalHomeRecord(chunk);
        }

        public IslandChunk GetLoadedChunk(int x, int z)
        {
            _loaded.TryGetValue(Pack(x, z), out var chunk);
            return chunk;
        }

        public IslandChunk GetChunk(int x, int z) => GetLoadedChunk(x, z);

        public bool RegisterLoadedChunk(IslandChunk chunk)
        {
            if (chunk == null)
            {
                return false;
            }

            var key = Pack(chunk.ChunkX, chunk.ChunkZ);
            _loaded[key] = chunk;
            if (!loadedChunks.Contains(chunk))
            {
                loadedChunks.Add(chunk);
            }

            return true;
        }

        public bool UnregisterLoadedChunk(IslandChunk chunk)
        {
            if (chunk == null)
            {
                return false;
            }

            var key = Pack(chunk.ChunkX, chunk.ChunkZ);
            var removed = _loaded.Remove(key);
            loadedChunks.Remove(chunk);
            return removed;
        }

        public bool TryGetLogicalChunk(int x, int z, out ChunkSaveData data)
        {
            return _logical.TryGetValue(Pack(x, z), out data) && data != null;
        }

        public bool AddLogicalChunk(ChunkSaveData data)
        {
            if (data == null)
            {
                return false;
            }

            var key = Pack(data.chunkX, data.chunkZ);
            if (_logical.ContainsKey(key))
            {
                return false;
            }

            if (data.plants == null)
            {
                data.plants = new List<PlantSaveData>();
            }

            _logical[key] = CloneChunk(data);
            return true;
        }

        public bool RestoreLogicalChunks(IReadOnlyList<ChunkSaveData> saved)
        {
            _logical.Clear();
            if (saved == null)
            {
                return true;
            }

            foreach (var chunk in saved)
            {
                if (chunk == null)
                {
                    continue;
                }

                var key = Pack(chunk.chunkX, chunk.chunkZ);
                if (_logical.ContainsKey(key))
                {
                    Debug.LogError(
                        $"[AFK Island] Duplicate logical chunk on restore ({chunk.chunkX},{chunk.chunkZ}).");
                    return false;
                }

                _logical[key] = CloneChunk(chunk);
            }

            return true;
        }

        public List<ChunkSaveData> CaptureLogicalChunks(long capturedUtc)
        {
            // Sync plants from loaded views into logical records first.
            foreach (var kv in _loaded)
            {
                var view = kv.Value;
                if (view == null)
                {
                    continue;
                }

                var key = Pack(view.ChunkX, view.ChunkZ);
                if (!_logical.TryGetValue(key, out var logical) || logical == null)
                {
                    logical = new ChunkSaveData
                    {
                        chunkX = view.ChunkX,
                        chunkZ = view.ChunkZ,
                        isUnlocked = view.IsUnlocked,
                        biomeId = view.BiomeId,
                        randomSeed = view.RandomSeed,
                        plants = new List<PlantSaveData>()
                    };
                    _logical[key] = logical;
                }

                // CIF-A1: plot Capture is plot-only; merge so wild_* plants in logical survive autosave.
                var plotCapture = view.Capture(capturedUtc);
                logical.plants = MergeCapturedPlants(logical.plants, plotCapture.plants);
                logical.isUnlocked = view.IsUnlocked;
                logical.biomeId = view.BiomeId;
                logical.randomSeed = view.RandomSeed;
                logical.lastSimulatedUtc = capturedUtc;
            }

            var list = new List<ChunkSaveData>();
            foreach (var logical in _logical.Values)
            {
                if (logical == null)
                {
                    continue;
                }

                list.Add(CloneChunk(logical));
            }

            if (list.Count == 0 && homeChunk != null)
            {
                list.Add(homeChunk.Capture(capturedUtc));
            }

            return list;
        }

        /// <summary>Backward-compatible alias used by older callers. </summary>
        public List<ChunkSaveData> CaptureChunks(long simulatedUtc) => CaptureLogicalChunks(simulatedUtc);

        public bool TryBindLoadedView(IslandChunk view)
        {
            if (view == null)
            {
                return false;
            }

            RegisterLoadedChunk(view);
            var key = Pack(view.ChunkX, view.ChunkZ);
            if (!_logical.ContainsKey(key))
            {
                EnsureLogicalHomeRecord(view);
            }

            return true;
        }

        public List<PlantSaveData> FlattenPlantsFromSave(GameSaveData data)
        {
            var result = new List<PlantSaveData>();
            if (data == null)
            {
                return result;
            }

            if (data.chunks != null)
            {
                foreach (var chunk in data.chunks)
                {
                    if (chunk?.plants != null)
                    {
                        result.AddRange(chunk.plants);
                    }
                }
            }

            if (result.Count == 0 && data.plants != null)
            {
                result.AddRange(data.plants);
            }

            return result;
        }

        public void ApplyLogicalState(GameSaveData data)
        {
            if (data?.chunks == null)
            {
                return;
            }

            RestoreLogicalChunks(data.chunks);
        }

        public IslandChunk EnsureHomeChunkInScene(IEnumerable<PlantPlot> plots)
        {
            var existing = GetLoadedChunk(IslandChunk.HomeChunkX, IslandChunk.HomeChunkZ);
            if (existing != null)
            {
                ConfigureHomeChunk(existing, plots);
                return existing;
            }

            Transform island = null;
            var world = GameObject.Find("World");
            if (world != null)
            {
                island = world.transform.Find("Island");
            }

            var parent = island != null ? island : transform;
            var go = new GameObject($"Chunk_{IslandChunk.HomeChunkX}_{IslandChunk.HomeChunkZ}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            var chunk = go.AddComponent<IslandChunk>();
            ConfigureHomeChunk(chunk, plots);
            return chunk;
        }

        private void EnsureLogicalHomeRecord(IslandChunk chunk)
        {
            var key = Pack(IslandChunk.HomeChunkX, IslandChunk.HomeChunkZ);
            if (_logical.ContainsKey(key))
            {
                return;
            }

            _logical[key] = new ChunkSaveData
            {
                chunkX = IslandChunk.HomeChunkX,
                chunkZ = IslandChunk.HomeChunkZ,
                isUnlocked = true,
                biomeId = chunk != null ? chunk.BiomeId : "starter_island",
                randomSeed = chunk != null ? chunk.RandomSeed : 0,
                lastSimulatedUtc = 0,
                plants = new List<PlantSaveData>()
            };
        }

        private static ChunkSaveData CloneChunk(ChunkSaveData source)
        {
            var plants = new List<PlantSaveData>();
            if (source.plants != null)
            {
                foreach (var p in source.plants)
                {
                    if (p != null)
                    {
                        plants.Add(SaveMigrationService.ClonePlant(p));
                    }
                }
            }

            return new ChunkSaveData
            {
                chunkX = source.chunkX,
                chunkZ = source.chunkZ,
                isUnlocked = source.isUnlocked,
                biomeId = source.biomeId,
                randomSeed = source.randomSeed,
                lastSimulatedUtc = source.lastSimulatedUtc,
                plants = plants
            };
        }

        /// <summary>
        /// CIF-A1: keep wild/unbound logical plants; refresh plot-bound from IslandChunk.Capture.
        /// </summary>
        public static List<PlantSaveData> MergeCapturedPlants(
            List<PlantSaveData> logicalPlants,
            List<PlantSaveData> plotCaptured)
        {
            var result = new List<PlantSaveData>();
            var plotInstanceIds = new HashSet<string>();

            if (plotCaptured != null)
            {
                for (var i = 0; i < plotCaptured.Count; i++)
                {
                    var p = plotCaptured[i];
                    if (p == null)
                    {
                        continue;
                    }

                    result.Add(SaveMigrationService.ClonePlant(p));
                    if (!string.IsNullOrEmpty(p.instanceId))
                    {
                        plotInstanceIds.Add(p.instanceId);
                    }
                }
            }

            if (logicalPlants != null)
            {
                for (var i = 0; i < logicalPlants.Count; i++)
                {
                    var p = logicalPlants[i];
                    if (p == null || IsPlotBoundPlant(p))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(p.instanceId) && plotInstanceIds.Contains(p.instanceId))
                    {
                        continue;
                    }

                    result.Add(SaveMigrationService.ClonePlant(p));
                }
            }

            return result;
        }

        public static bool IsPlotBoundPlant(PlantSaveData plant)
        {
            if (plant == null || string.IsNullOrEmpty(plant.plotId))
            {
                return false;
            }

            return !PlantColonizationSimulation.IsWildPlotId(plant.plotId);
        }

        private static long Pack(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
