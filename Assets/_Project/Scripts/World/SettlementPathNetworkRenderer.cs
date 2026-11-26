using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AfkIsland.World
{
    /// <summary>
    /// Stage C-3D: standalone visual renderer for the settlement path network.
    /// Consumes a pre-computed SettlementPathTopology.TopologyPlan + terrain height sampler
    /// and maintains per-chunk Core/Fringe meshes with incremental dirty-chunk updates,
    /// deterministic mesh lifecycle (zero Mesh/Material leaks on rebuilds), and clean teardown.
    /// Intentionally decoupled from HomeSettlementVisualizer, Save v8, and ResidentPathGraph.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SettlementPathNetworkRenderer : MonoBehaviour
    {
        [Header("Materials (assigned via sharedMaterial — never duplicated)")]
        [SerializeField] private Material coreMaterial;
        [SerializeField] private Material fringeMaterial;

        [Header("Default Mesh Build Config")]
        [SerializeField] private SettlementPathMeshBuilder.MeshBuildConfig defaultConfig =
            new SettlementPathMeshBuilder.MeshBuildConfig();

        public sealed class RendererStats
        {
            public int ActiveChunkCount;
            public int ActiveMeshCount;
            public int TotalApplyCalls;
            public int LastRebuiltChunkCount;
            public int LastReusedChunkCount;
            public int LastRemovedChunkCount;
            public int TotalMeshesCreated;
            public int TotalMeshesDestroyed;
            public int LiveOwnedMeshCount;
            public int TotalCoreVertices;
            public int TotalCoreTriangles;
            public int TotalFringeVertices;
            public int TotalFringeTriangles;
            public bool UsesSharedMaterialsOnly;
        }

        private sealed class ChunkVisualSlot
        {
            public Vector2Int Coord;
            public string InputSignature;
            public int TerrainVersion;
            public GameObject Root;
            public MeshFilter CoreFilter;
            public MeshRenderer CoreRenderer;
            public MeshFilter FringeFilter;
            public MeshRenderer FringeRenderer;
            public Mesh OwnedCoreMesh;
            public Mesh OwnedFringeMesh;
            public int CoreVertices;
            public int CoreTriangles;
            public int FringeVertices;
            public int FringeTriangles;
        }

        private readonly Dictionary<Vector2Int, ChunkVisualSlot> _slots =
            new Dictionary<Vector2Int, ChunkVisualSlot>();

        private readonly HashSet<Mesh> _ownedMeshes = new HashSet<Mesh>();

        private int _totalApplyCalls;
        private int _lastRebuilt;
        private int _lastReused;
        private int _lastRemoved;
        private int _totalCreated;
        private int _totalDestroyed;

        public SettlementPathMeshBuilder.MeshBuildReport LastBuildReport { get; private set; }
        public string LastTopologyHash { get; private set; }
        public Material CoreMaterial => coreMaterial;
        public Material FringeMaterial => fringeMaterial;

        public void ConfigureMaterials(Material core, Material fringe)
        {
            coreMaterial = core;
            fringeMaterial = fringe;

            foreach (var kv in _slots)
            {
                var slot = kv.Value;
                if (slot.CoreRenderer != null) slot.CoreRenderer.sharedMaterial = coreMaterial;
                if (slot.FringeRenderer != null) slot.FringeRenderer.sharedMaterial = fringeMaterial;
            }
        }

        /// <summary>
        /// Applies a TopologyPlan to the rendered chunk hierarchy.
        /// Rebuilds only chunks whose input signature or terrainVersion changed (unless forceFullRebuild is true),
        /// reuses unchanged chunks without reallocating meshes, and destroys meshes for removed/rebuilt chunks.
        /// </summary>
        public SettlementPathMeshBuilder.MeshBuildReport ApplyPlan(
            SettlementPathTopology.TopologyPlan plan,
            Func<float, float, float> sampleWorldY,
            SettlementPathMeshBuilder.MeshBuildConfig config = null,
            bool forceFullRebuild = false,
            int terrainVersion = 0)
        {
            _totalApplyCalls++;
            _lastRebuilt = 0;
            _lastReused = 0;
            _lastRemoved = 0;

            var cfg = config ?? defaultConfig ?? new SettlementPathMeshBuilder.MeshBuildConfig();
            if (plan == null || plan.Edges.Count == 0)
            {
                ClearSlotsOnly();
                LastTopologyHash = plan != null ? plan.TopologyHash : string.Empty;
                LastBuildReport = SettlementPathMeshBuilder.BuildPerChunk(plan, sampleWorldY, cfg);
                return LastBuildReport;
            }

            // 1. Compute per-chunk input signatures to determine exactly which chunks need rebuilding
            var newSigs = SettlementPathMeshBuilder.ComputeChunkInputSignatures(plan, cfg);

            // Remove chunks no longer present in the plan
            var toRemove = new List<Vector2Int>();
            foreach (var existingKey in _slots.Keys)
            {
                if (!newSigs.ContainsKey(existingKey))
                {
                    toRemove.Add(existingKey);
                }
            }

            for (var i = 0; i < toRemove.Count; i++)
            {
                DestroySlot(toRemove[i]);
                _lastRemoved++;
            }

            // Determine dirty chunks
            HashSet<Vector2Int> dirtyChunks = null;
            if (!forceFullRebuild)
            {
                dirtyChunks = new HashSet<Vector2Int>();
                foreach (var kv in newSigs)
                {
                    if (!_slots.TryGetValue(kv.Key, out var slot) ||
                        slot.Root == null ||
                        slot.OwnedCoreMesh == null ||
                        slot.TerrainVersion != terrainVersion ||
                        !string.Equals(slot.InputSignature, kv.Value, StringComparison.Ordinal))
                    {
                        dirtyChunks.Add(kv.Key);
                    }
                    else
                    {
                        _lastReused++;
                    }
                }

                if (dirtyChunks.Count == 0)
                {
                    LastTopologyHash = plan.TopologyHash;
                    return LastBuildReport;
                }
            }

            // 2. Build fresh meshes ONLY for dirty chunks (or all chunks when forceFullRebuild == true)
            var buildReport = SettlementPathMeshBuilder.BuildPerChunk(plan, sampleWorldY, cfg, dirtyChunks);

            // Handle any dirty chunk that ended up with no emitted geometry (e.g. only padded overlap)
            if (dirtyChunks != null)
            {
                foreach (var ck in dirtyChunks)
                {
                    if (!buildReport.Chunks.ContainsKey(ck) && _slots.ContainsKey(ck))
                    {
                        DestroySlot(ck);
                        _lastRemoved++;
                    }
                }
            }

            // 3. Apply newly built meshes to slots in deterministic sorted order
            var builtKeys = new List<Vector2Int>(buildReport.Chunks.Keys);
            builtKeys.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));

            for (var i = 0; i < builtKeys.Count; i++)
            {
                var ck = builtKeys[i];
                var data = buildReport.Chunks[ck];
                var slot = GetOrCreateSlot(ck, cfg);

                // Destroy previous owned meshes first (prevents leaks & avoids Unity 6 stale GPU buffer caching)
                ReleaseOwnedMesh(slot.OwnedCoreMesh);
                ReleaseOwnedMesh(slot.OwnedFringeMesh);

                slot.OwnedCoreMesh = data.CoreMesh;
                slot.OwnedFringeMesh = data.FringeMesh;
                TrackOwnedMesh(slot.OwnedCoreMesh);
                TrackOwnedMesh(slot.OwnedFringeMesh);

                slot.CoreFilter.sharedMesh = slot.OwnedCoreMesh;
                slot.FringeFilter.sharedMesh = slot.OwnedFringeMesh;

                // Always use sharedMaterial to guarantee zero material duplication
                slot.CoreRenderer.sharedMaterial = coreMaterial;
                slot.FringeRenderer.sharedMaterial = fringeMaterial;

                slot.CoreVertices = data.CoreVertices;
                slot.CoreTriangles = data.CoreTriangles;
                slot.FringeVertices = data.FringeVertices;
                slot.FringeTriangles = data.FringeTriangles;
                slot.InputSignature = newSigs.TryGetValue(ck, out var sig) ? sig : string.Empty;
                slot.TerrainVersion = terrainVersion;
                _lastRebuilt++;
            }

            LastTopologyHash = plan.TopologyHash;
            LastBuildReport = buildReport;
            return buildReport;
        }

        public bool TryGetChunkMeshes(Vector2Int chunk, out Mesh coreMesh, out Mesh fringeMesh)
        {
            if (_slots.TryGetValue(chunk, out var slot))
            {
                coreMesh = slot.OwnedCoreMesh;
                fringeMesh = slot.OwnedFringeMesh;
                return true;
            }

            coreMesh = null;
            fringeMesh = null;
            return false;
        }

        public RendererStats GetStats()
        {
            var stats = new RendererStats
            {
                ActiveChunkCount = _slots.Count,
                TotalApplyCalls = _totalApplyCalls,
                LastRebuiltChunkCount = _lastRebuilt,
                LastReusedChunkCount = _lastReused,
                LastRemovedChunkCount = _lastRemoved,
                TotalMeshesCreated = _totalCreated,
                TotalMeshesDestroyed = _totalDestroyed,
                LiveOwnedMeshCount = _ownedMeshes.Count,
                UsesSharedMaterialsOnly = true
            };

            foreach (var kv in _slots)
            {
                var s = kv.Value;
                if (s.OwnedCoreMesh != null) stats.ActiveMeshCount++;
                if (s.OwnedFringeMesh != null) stats.ActiveMeshCount++;
                stats.TotalCoreVertices += s.CoreVertices;
                stats.TotalCoreTriangles += s.CoreTriangles;
                stats.TotalFringeVertices += s.FringeVertices;
                stats.TotalFringeTriangles += s.FringeTriangles;

                if (s.CoreRenderer != null && s.CoreRenderer.sharedMaterial != coreMaterial)
                {
                    stats.UsesSharedMaterialsOnly = false;
                }

                if (s.FringeRenderer != null && s.FringeRenderer.sharedMaterial != fringeMaterial)
                {
                    stats.UsesSharedMaterialsOnly = false;
                }
            }

            return stats;
        }

        /// <summary>
        /// Destroys all rendered chunk GameObjects and owned runtime Mesh instances.
        /// Never destroys shared Material assets.
        /// </summary>
        public void Clear()
        {
            ClearSlotsOnly();
            LastBuildReport = null;
            LastTopologyHash = string.Empty;
        }

        private void OnDestroy()
        {
            ClearSlotsOnly();
        }

        // ------------------------------------------------------------------ internals

        private ChunkVisualSlot GetOrCreateSlot(Vector2Int coord, SettlementPathMeshBuilder.MeshBuildConfig cfg)
        {
            if (_slots.TryGetValue(coord, out var existing) && existing.Root != null)
            {
                existing.Root.transform.localPosition = cfg.LocalToChunkOrigin
                    ? GrowthWorldPlacement.ChunkRootLocalPosition(coord.x, coord.y, cfg.LocalChunkYBase)
                    : Vector3.zero;
                return existing;
            }

            var root = new GameObject($"PathChunk_{coord.x}_{coord.y}");
            root.transform.SetParent(transform, false);
            root.transform.localPosition = cfg.LocalToChunkOrigin
                ? GrowthWorldPlacement.ChunkRootLocalPosition(coord.x, coord.y, cfg.LocalChunkYBase)
                : Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            CreateLayer(root.transform, "Fringe", fringeMaterial, out var fringeFilter, out var fringeRenderer);
            CreateLayer(root.transform, "Core", coreMaterial, out var coreFilter, out var coreRenderer);

            var slot = new ChunkVisualSlot
            {
                Coord = coord,
                Root = root,
                CoreFilter = coreFilter,
                CoreRenderer = coreRenderer,
                FringeFilter = fringeFilter,
                FringeRenderer = fringeRenderer
            };
            _slots[coord] = slot;
            return slot;
        }

        private static void CreateLayer(
            Transform parent,
            string layerName,
            Material sharedMat,
            out MeshFilter filter,
            out MeshRenderer renderer)
        {
            var go = new GameObject(layerName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            filter = go.AddComponent<MeshFilter>();
            renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = sharedMat;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }

        private void DestroySlot(Vector2Int coord)
        {
            if (!_slots.TryGetValue(coord, out var slot)) return;
            _slots.Remove(coord);

            ReleaseOwnedMesh(slot.OwnedCoreMesh);
            ReleaseOwnedMesh(slot.OwnedFringeMesh);
            slot.OwnedCoreMesh = null;
            slot.OwnedFringeMesh = null;

            if (slot.Root != null)
            {
                DestroyUnityObject(slot.Root);
                slot.Root = null;
            }
        }

        private void ClearSlotsOnly()
        {
            var keys = new List<Vector2Int>(_slots.Keys);
            for (var i = 0; i < keys.Count; i++)
            {
                DestroySlot(keys[i]);
            }

            // Safety sweep for any tracked mesh
            if (_ownedMeshes.Count > 0)
            {
                var leftover = new List<Mesh>(_ownedMeshes);
                for (var i = 0; i < leftover.Count; i++)
                {
                    ReleaseOwnedMesh(leftover[i]);
                }
            }
        }

        private void TrackOwnedMesh(Mesh mesh)
        {
            if (mesh == null) return;
            if (_ownedMeshes.Add(mesh))
            {
                _totalCreated++;
            }
        }

        private void ReleaseOwnedMesh(Mesh mesh)
        {
            if (mesh == null) return;
            if (_ownedMeshes.Remove(mesh))
            {
                _totalDestroyed++;
                DestroyUnityObject(mesh);
            }
        }

        private static void DestroyUnityObject(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying)
            {
                Destroy(obj);
            }
            else
            {
                DestroyImmediate(obj);
            }
        }
    }
}
