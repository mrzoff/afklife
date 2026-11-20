using System;
using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;
using UnityEngine.Rendering;

namespace AfkIsland.World
{
    /// <summary>
    /// Draws AFK-grown land as organic per-chunk meshes (not 1×1 cubes).
    /// Legacy home chunk keeps the authored island; only new growth is meshed.
    /// </summary>
    public sealed class IslandMeshVisualizer : MonoBehaviour
    {
        [SerializeField] private SaveService saveService;
        [SerializeField] private ChunkManager chunkManager;
        [SerializeField] private float yBase = GrowthWorldPlacement.VisualYBase;
        [SerializeField] private bool parentGrownMeshUnderWorld = true;
        [SerializeField] private bool placeDecor = true;
        /// <summary>In-cell decor spread as a fraction of a cell. Must stay below 1.0.</summary>
        private const float DecorJitterFraction = 0.8f;

        private Transform _root;
        private readonly Dictionary<long, ChunkView> _views = new();
        private Material _topMaterial;
        private Material _sideMaterial;
        private GameObject[] _decorPrefabs;
        private string _lastSignature;
        private GameSaveData _cachedData;
        private readonly Dictionary<long, byte[]> _stageSamples = new();
        private readonly Dictionary<long, byte[]> _biomeSamples = new();

        private sealed class ChunkView
        {
            public GameObject root;
            public MeshFilter filter;
            public MeshRenderer renderer;
            public Transform decorRoot;
            public Mesh mesh;
            public string stageSignature;
        }

        public void Configure(SaveService save, ChunkManager chunks)
        {
            saveService = save;
            chunkManager = chunks;
        }

        public void RebuildFromSave(GameSaveData data = null)
        {
            data ??= saveService != null ? saveService.AuthoritativeSnapshot : null;
            _cachedData = data;
            RebuildSampleCaches(data);
            if (data?.worldGrowth?.chunks == null || chunkManager == null)
            {
                ClearAll();
                _lastSignature = null;
                return;
            }

            var signature = BuildSignature(data);
            if (signature == _lastSignature && _views.Count > 0)
            {
                return;
            }

            _lastSignature = signature;
            EnsureRoot();
            EnsureMaterials();
            EnsureDecorPrefabs();

            var keep = new HashSet<long>();
            var size = chunkManager.ChunkSizeMeters;
            var changed = new HashSet<long>();
            var pending = new List<(ChunkGrowthSaveData growth, byte[] stages, long key, string chunkSig, int seed)>();
            var worldSeed = ResolveWorldScatterSeed(data);

            foreach (var growth in data.worldGrowth.chunks)
            {
                if (growth == null || growth.isLegacyBase)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                if (!HasAnyLand(stages))
                {
                    continue;
                }

                var key = Pack(growth.chunkX, growth.chunkZ);
                keep.Add(key);
                var seed = growth.growthSeed != 0
                    ? growth.growthSeed
                    : growth.chunkX * 73856093 ^ growth.chunkZ * 19349663;
                var chunkSig =
                    $"{growth.stageMaskBase64}:{growth.biomeMaskBase64}:{growth.settlementMaskBase64}:{seed}:{worldSeed}:{ChunkTerrainMesher.Subdiv}:r{ChunkTerrainMesher.MesherRevision}";
                pending.Add((growth, stages, key, chunkSig, seed));

                _views.TryGetValue(key, out var existing);
                if (existing?.mesh == null || existing.stageSignature != chunkSig)
                {
                    changed.Add(key);
                }
            }

            for (var i = 0; i < pending.Count; i++)
            {
                var (growth, stages, key, chunkSig, seed) = pending[i];
                var view = EnsureView(key, growth.chunkX, growth.chunkZ);
                var dirty = view.mesh == null
                            || changed.Contains(key)
                            || HasChangedNeighbor(growth.chunkX, growth.chunkZ, changed);
                if (!dirty)
                {
                    view.root.SetActive(true);
                    continue;
                }

                if (view.mesh != null)
                {
                    LandHitCollider.Apply(view.root, null);
                    Destroy(view.mesh);
                    view.mesh = null;
                }

                view.mesh = ChunkTerrainMesher.Build(
                    growth.chunkX,
                    growth.chunkZ,
                    size,
                    SampleWorldStage,
                    seed,
                    SampleWorldBiome);
                view.stageSignature = chunkSig;
                view.filter.sharedMesh = view.mesh;
                view.renderer.sharedMaterials = new[] { _topMaterial, _sideMaterial };
                view.renderer.enabled = true;
                view.root.SetActive(true);
                var verts = view.mesh != null ? view.mesh.vertexCount : 0;
                LandHitCollider.Apply(view.root, verts >= 3 ? view.mesh : null);
                if (verts < 3)
                {
                    Debug.LogWarning(
                        $"[AFK Island] GrownLand_{growth.chunkX}_{growth.chunkZ} mesh empty (verts={verts}).");
                    ClearDecor(view);
                }
                else
                {
                    // Decor only when terrain actually exists — otherwise trees float in the ocean void.
                    RebuildDecor(view, growth, stages, size);
                }
            }

            var remove = new List<long>();
            foreach (var kv in _views)
            {
                if (!keep.Contains(kv.Key))
                {
                    remove.Add(kv.Key);
                }
            }

            for (var i = 0; i < remove.Count; i++)
            {
                DestroyView(remove[i]);
            }
        }

        private void LateUpdate()
        {
            var data = saveService != null ? saveService.AuthoritativeSnapshot : null;
            if (data == null)
            {
                return;
            }

            var signature = BuildSignature(data);
            if (signature != _lastSignature)
            {
                RebuildFromSave(data);
            }
        }

        private void RebuildSampleCaches(GameSaveData data)
        {
            _stageSamples.Clear();
            _biomeSamples.Clear();
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

                var key = Pack(growth.chunkX, growth.chunkZ);
                byte[] stages;
                if (growth.isLegacyBase)
                {
                    stages = new byte[ChunkMaskCodec.CellCount];
                    for (var i = 0; i < stages.Length; i++)
                    {
                        stages[i] = (byte)CellStage.Moss;
                    }
                }
                else if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out stages, out _))
                {
                    stages = Array.Empty<byte>();
                }

                _stageSamples[key] = stages;
                _biomeSamples[key] = BiomeMaskCodec.DecodeOrEmpty(growth.biomeMaskBase64);
            }
        }

        private byte SampleWorldStage(int worldCellX, int worldCellZ)
        {
            var grid = ChunkMaskCodec.GridSize;
            var chunkX = FloorDiv(worldCellX, grid);
            var chunkZ = FloorDiv(worldCellZ, grid);
            var lx = worldCellX - chunkX * grid;
            var lz = worldCellZ - chunkZ * grid;
            if (!_stageSamples.TryGetValue(Pack(chunkX, chunkZ), out var stages))
            {
                return (byte)CellStage.Void;
            }

            var index = ChunkMaskCodec.CellIndex(lx, lz);
            return index >= 0 && index < stages.Length ? stages[index] : (byte)CellStage.Void;
        }

        private BiomeType SampleWorldBiome(int worldCellX, int worldCellZ)
        {
            var grid = ChunkMaskCodec.GridSize;
            var chunkX = FloorDiv(worldCellX, grid);
            var chunkZ = FloorDiv(worldCellZ, grid);
            var lx = worldCellX - chunkX * grid;
            var lz = worldCellZ - chunkZ * grid;
            if (!_biomeSamples.TryGetValue(Pack(chunkX, chunkZ), out var biomes))
            {
                return BiomeType.None;
            }

            var index = ChunkMaskCodec.CellIndex(lx, lz);
            return index >= 0 && index < biomes.Length ? (BiomeType)biomes[index] : BiomeType.None;
        }

        private static int FloorDiv(int a, int b)
        {
            var q = a / b;
            var r = a % b;
            if (r != 0 && ((r > 0) != (b > 0)))
            {
                q--;
            }

            return q;
        }

        private static void ClearDecor(ChunkView view)
        {
            if (view?.decorRoot == null)
            {
                return;
            }

            for (var i = view.decorRoot.childCount - 1; i >= 0; i--)
            {
                var child = view.decorRoot.GetChild(i);
                if (child != null)
                {
                    Object.Destroy(child.gameObject);
                }
            }
        }

        private void RebuildDecor(ChunkView view, ChunkGrowthSaveData growth, byte[] stages, float chunkSize)
        {
            ClearDecor(view);

            if (!placeDecor || _decorPrefabs == null || _decorPrefabs.Length == 0)
            {
                return;
            }

            if (view.decorRoot == null)
            {
                var go = new GameObject("Decor");
                go.transform.SetParent(view.root.transform, false);
                view.decorRoot = go.transform;
            }

            var seed = growth.growthSeed != 0
                ? growth.growthSeed
                : growth.chunkX * 73856093 ^ growth.chunkZ * 19349663;

            ClassifyDecor(out var rocks, out var plants, out var scrub);
            var biomes = BiomeMaskCodec.DecodeOrEmpty(growth.biomeMaskBase64);
            ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settlement, out _);

            for (var i = 0; i < stages.Length; i++)
            {
                var stage = stages[i];
                // Roots / void: no props. Soil+: sparse rocks. Moss/Vegetation: plants.
                if (stage < (byte)CellStage.Soil)
                {
                    continue;
                }

                ChunkMaskCodec.IndexToLocal(i, out var lx, out var lz);
                var worldCellX = growth.chunkX * ChunkMaskCodec.GridSize + lx;
                var worldCellZ = growth.chunkZ * ChunkMaskCodec.GridSize + lz;

                // Hard ban on shore / water edge using WORLD neighbors (seam-safe).
                if (IsWorldShoreCell(worldCellX, worldCellZ))
                {
                    continue;
                }

                // Stage 11E: no chunk decor inside authored WaterSource reserve.
                var cellWorld = GrowthWorldPlacement.CellWorldPosition(
                    growth.chunkX, growth.chunkZ, i, stage, growth.isLegacyBase);
                if (WaterSourceLayout.IsInsideReserveXZ(cellWorld))
                {
                    continue;
                }

                var biome = (BiomeType)biomes[i];
                if (biome == BiomeType.None)
                {
                    biome = SampleWorldBiome(worldCellX, worldCellZ);
                }

                var h = Hash01(seed, i, 17);
                float chance;
                GameObject[] pool;
                switch (biome)
                {
                    case BiomeType.Forest:
                        chance = stage >= (byte)CellStage.Vegetation ? 0.14f : 0.08f;
                        pool = plants.Length > 0 ? plants : _decorPrefabs;
                        break;
                    case BiomeType.Mossy:
                        chance = 0.09f;
                        pool = h < 0.4f && rocks.Length > 0
                            ? rocks
                            : (scrub.Length > 0 ? scrub : (plants.Length > 0 ? plants : _decorPrefabs));
                        break;
                    case BiomeType.Coastal:
                        chance = 0.05f;
                        pool = rocks.Length > 0 ? rocks : (scrub.Length > 0 ? scrub : _decorPrefabs);
                        break;
                    default:
                        chance = 0.04f;
                        pool = rocks.Length > 0 ? rocks : _decorPrefabs;
                        break;
                }

                if (h > chance || pool.Length == 0)
                {
                    continue;
                }

                var prefab = pool[Mathf.FloorToInt(Hash01(seed, i, 91) * 0.999f * pool.Length)];
                if (prefab == null)
                {
                    continue;
                }

                var isRock = IsRockDecor(prefab.name);
                // Tall trees are generated by WorldScatterField in a separate world-space pass.
                if (IsTallDecor(prefab.name))
                {
                    continue;
                }

                // Hut cell keeps its roof. Only low rocks may stay; tall canopy and bushes go.
                if (settlement != null && i < settlement.Length &&
                    settlement[i] == (byte)SettlementStage.Hut &&
                    IsScrubDecor(prefab.name))
                {
                    continue;
                }

                var isScrub = IsScrubDecor(prefab.name);
                float scale;
                if (isRock)
                {
                    scale = 0.22f + h * 0.18f;
                }
                else if (isScrub)
                {
                    scale = 0.30f + h * 0.22f;
                }
                else if (stage >= (byte)CellStage.Vegetation)
                {
                    scale = 0.36f + h * 0.28f;
                }
                else
                {
                    scale = 0.26f + h * 0.18f;
                }

                // Break the grid: deterministic in-cell jitter. Offset stays under half a cell,
                // so the cell's land/shore/hut classification above remains valid.
                var cellSize = chunkSize / ChunkMaskCodec.GridSize;
                var jitterX = (Hash01(seed, i, 41) - 0.5f) * DecorJitterFraction * cellSize;
                var jitterZ = (Hash01(seed, i, 43) - 0.5f) * DecorJitterFraction * cellSize;
                var propWorldX = cellWorld.x + jitterX;
                var propWorldZ = cellWorld.z + jitterZ;

                if (WaterSourceLayout.BlocksTrunk(
                        propWorldX, propWorldZ, WaterSourceLayout.TrunkRadius(scale)))
                {
                    continue;
                }

                var inst = Instantiate(prefab, view.decorRoot);
                // Chunk root is at ChunkOrigin+yBase; decor is local to that root.
                var local = GrowthWorldPlacement.CellLocalOnChunk(
                    growth.chunkX, growth.chunkZ, i, stage, yBase, chunkSize);
                local.x += jitterX;
                local.z += jitterZ;
                local.y += (Hash01(seed, i, 33) - 0.5f) * 0.08f;
                inst.transform.localPosition = local;
                inst.transform.localRotation = Quaternion.Euler(0f, h * 360f, 0f);
                inst.transform.localScale = Vector3.one * scale;
                VegetationWindApplicator.ApplyToInstance(inst, isRock, prefab.name);
                inst.name = $"Decor_{growth.chunkX}_{growth.chunkZ}_{i}";
            }

            RebuildTallTrees(view, growth, chunkSize, plants);
        }

        private void RebuildTallTrees(
            ChunkView view,
            ChunkGrowthSaveData growth,
            float chunkSize,
            GameObject[] treePrefabs)
        {
            if (treePrefabs == null || treePrefabs.Length == 0 || _cachedData?.worldGrowth == null)
            {
                return;
            }

            var origin = GrowthWorldPlacement.ChunkOriginXZ(growth.chunkX, growth.chunkZ, chunkSize);
            var bounds = new Rect(origin.x, origin.y, chunkSize, chunkSize);
            var worldSeed = ResolveWorldScatterSeed(_cachedData);
            if (worldSeed == 0)
            {
                worldSeed = unchecked((int)0x11B0A11);
            }

            var blockers = new List<Vector2>(32)
            {
                new Vector2(-2.4f, -0.4f) // authored yard cedar
            };
            CollectSavedPlantBlockers(blockers);

            float DensityAt(Vector2 point)
            {
                if (!TryGetWorldCell(point.x, point.y, out _, out _, out var stage, out var biome))
                {
                    return 0f;
                }

                if (stage < (byte)CellStage.Vegetation || biome != BiomeType.Forest)
                {
                    return 0f;
                }

                var broad = WorldScatterField.SampleValueNoise(point.x, point.y, 18f, worldSeed, 113u);
                var detail = WorldScatterField.SampleValueNoise(point.x, point.y, 7f, worldSeed, 127u);
                return 0.28f * Mathf.Lerp(0.72f, 1.18f, broad) * Mathf.Lerp(0.82f, 1.12f, detail);
            }

            bool IsValid(Vector2 point)
            {
                if (!TryGetWorldCell(point.x, point.y, out var worldCellX, out var worldCellZ,
                        out var stage, out var biome))
                {
                    return false;
                }

                if (stage < (byte)CellStage.Vegetation || biome != BiomeType.Forest)
                {
                    return false;
                }

                if (IsNearShore(worldCellX, worldCellZ, 2))
                {
                    return false;
                }

                if (SettlementCellQuery.IsHutFootprintBlocked(\n                        _cachedData, point.x, point.y, 2.0f))
                {
                    return false;
                }

                if (WaterSourceLayout.BlocksTrunk(point.x, point.y,
                        WaterSourceLayout.TrunkRadius(0.9f)))
                {
                    return false;
                }

                return true;
            }

            var points = new List<WorldScatterField.ScatterPoint>(16);
            WorldScatterField.Generate(
                bounds,
                worldSeed,
                WorldScatterField.DefaultTreeMinimumSpacing,
                WorldScatterField.DefaultCandidateSpacing,
                DensityAt,
                IsValid,
                blockers,
                points);

            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                var pick = (int)(WorldScatterField.Hash(worldSeed, point.GridX, point.GridZ, 191u) % (uint)treePrefabs.Length);
                var prefab = treePrefabs[pick];
                if (prefab == null)
                {
                    continue;
                }

                var scaleRoll = WorldScatterField.Hash01(worldSeed, point.GridX, point.GridZ, 197u);
                var scale = 0.58f + scaleRoll * 0.30f;
                var inst = Instantiate(prefab, view.decorRoot);
                var worldY = ChunkTerrainMesher.SampleVisualHeight(
                    point.Position.x,
                    point.Position.y,
                    SampleWorldStage,
                    SampleWorldBiome,
                    chunkSize);
                inst.transform.localPosition = new Vector3(
                    point.Position.x - origin.x,
                    worldY - yBase,
                    point.Position.y - origin.y);
                inst.transform.localRotation = Quaternion.Euler(
                    0f,
                    WorldScatterField.Hash01(worldSeed, point.GridX, point.GridZ, 211u) * 360f,
                    0f);
                inst.transform.localScale = Vector3.one * scale;
                VegetationWindApplicator.ApplyToInstance(inst, false, prefab.name);
                inst.name = $"Tree_{point.GridX}_{point.GridZ}";
            }
        }

        private bool TryGetWorldCell(
            float worldX,
            float worldZ,
            out int worldCellX,
            out int worldCellZ,
            out byte stage,
            out BiomeType biome)
        {
            var cellSize = GrowthWorldPlacement.CellSizeMeters;
            var half = GrowthWorldPlacement.ChunkSizeMeters * 0.5f;
            worldCellX = Mathf.FloorToInt((worldX + half) / cellSize);
            worldCellZ = Mathf.FloorToInt((worldZ + half) / cellSize);
            stage = SampleWorldStage(worldCellX, worldCellZ);
            biome = SampleWorldBiome(worldCellX, worldCellZ);
            return stage != (byte)CellStage.Void;
        }

        private static int ResolveWorldScatterSeed(GameSaveData data)
        {
            // WorldGrowthSaveData.growthSeed advances during simulation. It must not drive
            // persistent visual placement or trees would jump after every offline session.
            if (data?.chunks != null)
            {
                for (var i = 0; i < data.chunks.Count; i++)
                {
                    var chunk = data.chunks[i];
                    if (chunk != null && chunk.chunkX == IslandChunk.HomeChunkX &&
                        chunk.chunkZ == IslandChunk.HomeChunkZ && chunk.randomSeed != 0)
                    {
                        return chunk.randomSeed;
                    }
                }
            }

            // Legacy saves can have a zero home randomSeed. A fixed fallback keeps their
            // world-space scatter stable without adding fields or migrating Save v8.
            return unchecked((int)0x11B0A11);
        }

        private void CollectSavedPlantBlockers(List<Vector2> into)
        {
            if (_cachedData == null)
            {
                return;
            }

            AddPlantBlockers(_cachedData.plants, into);
            if (_cachedData.chunks == null)
            {
                return;
            }

            for (var i = 0; i < _cachedData.chunks.Count; i++)
            {
                AddPlantBlockers(_cachedData.chunks[i]?.plants, into);
            }
        }

        private static void AddPlantBlockers(List<PlantSaveData> plants, List<Vector2> into)
        {
            if (plants == null)
            {
                return;
            }

            for (var i = 0; i < plants.Count; i++)
            {
                var plant = plants[i];
                if (plant != null)
                {
                    into.Add(new Vector2(plant.posX, plant.posZ));
                }
            }
        }

        private void ClassifyDecor(out GameObject[] rocks, out GameObject[] plants, out GameObject[] scrub)
        {
            var rockList = new List<GameObject>(4);
            var plantList = new List<GameObject>(8);
            var scrubList = new List<GameObject>(4);
            for (var i = 0; i < _decorPrefabs.Length; i++)
            {
                var p = _decorPrefabs[i];
                if (p == null)
                {
                    continue;
                }

                if (IsRockDecor(p.name))
                {
                    rockList.Add(p);
                }
                else if (IsScrubDecor(p.name))
                {
                    scrubList.Add(p);
                }
                else
                {
                    plantList.Add(p);
                }
            }

            rocks = rockList.ToArray();
            plants = plantList.ToArray();
            scrub = scrubList.ToArray();
        }

        private static bool IsRockDecor(string name) =>
            !string.IsNullOrEmpty(name) &&
            name.IndexOf("Rock", System.StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsScrubDecor(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return name.IndexOf("Mugwort", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Viola", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Eurya", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("FallenLeaves", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTallDecor(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return name.IndexOf("Laurel", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Bamboo", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Cedar", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Pine", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Oak", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Cypress", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Redwood", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("PlaneTree", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Cherry", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsWorldShoreCell(int worldCellX, int worldCellZ) =>
            IsNearShore(worldCellX, worldCellZ, 1);

        private bool IsNearShore(int worldCellX, int worldCellZ, int radius)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    if (SampleWorldStage(worldCellX + dx, worldCellZ + dz) == (byte)CellStage.Void)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private ChunkView EnsureView(long key, int chunkX, int chunkZ)
        {
            var pose = GrowthWorldPlacement.ChunkRootLocalPosition(chunkX, chunkZ, yBase);
            if (_views.TryGetValue(key, out var existing) && existing != null && existing.root != null)
            {
                existing.root.transform.localPosition = pose;
                existing.root.transform.localRotation = Quaternion.identity;
                existing.root.transform.localScale = Vector3.one;
                return existing;
            }

            var go = new GameObject($"GrownLand_{chunkX}_{chunkZ}");
            go.transform.SetParent(_root, false);
            go.transform.localPosition = pose;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            var view = new ChunkView
            {
                root = go,
                filter = filter,
                renderer = renderer
            };
            _views[key] = view;
            return view;
        }

        private void DestroyView(long key)
        {
            if (!_views.TryGetValue(key, out var view))
            {
                return;
            }

            if (view.mesh != null)
            {
                Destroy(view.mesh);
            }

            if (view.root != null)
            {
                Destroy(view.root);
            }

            _views.Remove(key);
        }

        private void EnsureRoot()
        {
            if (_root != null)
            {
                // Keep identity under World so chunk-local meshes stay aligned with authored Island.
                _root.localPosition = Vector3.zero;
                _root.localRotation = Quaternion.identity;
                _root.localScale = Vector3.one;
                return;
            }

            var go = new GameObject("IslandGrownMesh");
            Transform parent = transform;
            if (parentGrownMeshUnderWorld)
            {
                var worldGo = GameObject.Find("World");
                if (worldGo != null)
                {
                    parent = worldGo.transform;
                }
            }

            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            _root = go.transform;
        }

        private void EnsureMaterials()
        {
            if (_topMaterial != null && _sideMaterial != null)
            {
                return;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                         ?? Shader.Find("Standard");

            _topMaterial = new Material(shader) { name = "GrownTerrain_Top" };
            _sideMaterial = new Material(shader) { name = "GrownTerrain_Side" };

            var grass = Resources.Load<Texture2D>("AfkIsland/Terrain/GrassColor");
            var soil = Resources.Load<Texture2D>("AfkIsland/Terrain/SoilColor");
            var sand = Resources.Load<Texture2D>("AfkIsland/Terrain/SandColor");
            var moss = Resources.Load<Texture2D>("AfkIsland/Terrain/MossColor");

            ApplyTexture(_topMaterial, grass != null ? grass : moss, new Color(0.35f, 0.55f, 0.28f));
            ApplyTexture(_sideMaterial, soil != null ? soil : sand, new Color(0.4f, 0.26f, 0.12f));

            if (_topMaterial.HasProperty("_Smoothness"))
            {
                _topMaterial.SetFloat("_Smoothness", 0.18f);
                _sideMaterial.SetFloat("_Smoothness", 0.12f);
            }
        }

        private static void ApplyTexture(Material mat, Texture2D tex, Color fallback)
        {
            // Always tint with a solid fallback. BlueStacks GLES often rejects compressed
            // terrain textures (GL 0x822a) — white*brokenTex makes land invisible while trees stay.
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", fallback);
            }

            if (mat.HasProperty("_Color"))
            {
                mat.SetColor("_Color", fallback);
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            return;
#else
            if (tex == null)
            {
                return;
            }

            if (mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", tex);
            }

            if (mat.HasProperty("_MainTex"))
            {
                mat.SetTexture("_MainTex", tex);
            }

            // Keep albedo readable if texture is dark / missing mips.
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", Color.white);
            }
#endif
        }

        private void EnsureDecorPrefabs()
        {
            if (_decorPrefabs != null)
            {
                return;
            }

            // Stage 9.1 Nature kit (Polyeler). Keep names for ClassifyDecor / wind.
            var list = new List<GameObject>(16);
            TryAddDecor(list, "AfkIsland/Decor/Rock_A");
            TryAddDecor(list, "AfkIsland/Decor/Rock_B");
            TryAddDecor(list, "AfkIsland/Decor/Rock_C");
            TryAddDecor(list, "AfkIsland/Decor/Rock_D");
            TryAddDecor(list, "AfkIsland/Decor/Mugwort");
            TryAddDecor(list, "AfkIsland/Decor/ViolaGrypoceras");
            TryAddDecor(list, "AfkIsland/Decor/JapaneseEurya");
            TryAddDecor(list, "AfkIsland/Decor/JapaneseLaurel");
            TryAddDecor(list, "AfkIsland/Decor/LittleBamboo");
            TryAddDecor(list, "AfkIsland/Decor/Bamboo");
            TryAddDecor(list, "AfkIsland/Decor/JapaneseCedar");
            TryAddDecor(list, "AfkIsland/Decor/Pine");
            TryAddDecor(list, "AfkIsland/Decor/SawtoothOak");
            TryAddDecor(list, "AfkIsland/Decor/JapaneseCypress");
            _decorPrefabs = list.ToArray();
        }

        private static void TryAddDecor(List<GameObject> list, string path)
        {
            var prefab = Resources.Load<GameObject>(path);
            if (prefab != null)
            {
                list.Add(prefab);
            }
        }

        private void ClearAll()
        {
            var keys = new List<long>(_views.Keys);
            for (var i = 0; i < keys.Count; i++)
            {
                DestroyView(keys[i]);
            }

            if (_root != null)
            {
                Destroy(_root.gameObject);
                _root = null;
            }
        }

        private static bool HasAnyLand(byte[] stages)
        {
            for (var i = 0; i < stages.Length; i++)
            {
                if (stages[i] != (byte)CellStage.Void)
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildSignature(GameSaveData data)
        {
            if (data?.worldGrowth?.chunks == null)
            {
                return "empty";
            }

            var parts = new List<string>(data.worldGrowth.chunks.Count);
            foreach (var growth in data.worldGrowth.chunks)
            {
                if (growth == null || growth.isLegacyBase)
                {
                    continue;
                }

                parts.Add($"{growth.chunkX}:{growth.chunkZ}:{growth.stageMaskBase64}:{growth.biomeMaskBase64}:{growth.settlementMaskBase64}:{growth.growthSeed}");
            }

            parts.Sort();

            // Existing saved plants are hard blockers for new decorative tree trunks.
            // Include their canonical positions so a planting/removal rebuilds scatter.
            var plantParts = new List<string>();
            var hasChunkPlants = false;
            if (data.chunks != null)
            {
                foreach (var chunk in data.chunks)
                {
                    if (chunk?.plants == null)
                    {
                        continue;
                    }

                    for (var i = 0; i < chunk.plants.Count; i++)
                    {
                        var plant = chunk.plants[i];
                        if (plant == null)
                        {
                            continue;
                        }

                        hasChunkPlants = true;
                        plantParts.Add($"{plant.instanceId}:{plant.posX:R}:{plant.posZ:R}");
                    }
                }
            }

            if (!hasChunkPlants && data.plants != null)
            {
                for (var i = 0; i < data.plants.Count; i++)
                {
                    var plant = data.plants[i];
                    if (plant != null)
                    {
                        plantParts.Add($"{plant.instanceId}:{plant.posX:R}:{plant.posZ:R}");
                    }
                }
            }

            plantParts.Sort(StringComparer.Ordinal);
            return $"{ResolveWorldScatterSeed(data)}|{string.Join("|", parts)}|plants:{string.Join("|", plantParts)}";
        }

        private static bool HasChangedNeighbor(int chunkX, int chunkZ, HashSet<long> changed)
        {
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    if (changed.Contains(Pack(chunkX + dx, chunkZ + dz)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static long Pack(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static float Hash01(int seed, int a, int b)
        {
            unchecked
            {
                var h = (uint)(seed * 374761393 + a * 668265263 + b * 2147483647);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }

        private void OnDestroy()
        {
            ClearAll();
            if (_topMaterial != null)
            {
                Destroy(_topMaterial);
            }

            if (_sideMaterial != null)
            {
                Destroy(_sideMaterial);
            }
        }
    }
}
