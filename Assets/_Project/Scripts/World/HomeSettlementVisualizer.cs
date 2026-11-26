using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;
using UnityEngine.Rendering;

namespace AfkIsland.World
{
    /// <summary>
    /// Draws settlement mask props (clearing / path / stone / hut) on home + grown chunks.
    /// Authored starter Settlement stays; mask props grow around it. Soft-dims authored
    /// paths/huts when the home mask already has matching stages.
    /// </summary>
    public sealed class HomeSettlementVisualizer : MonoBehaviour
    {
        [SerializeField] private SaveService saveService;
        [SerializeField] private ChunkManager chunkManager;
        [SerializeField] private float yBase = GrowthWorldPlacement.VisualYBase;
        [SerializeField] private bool softDimAuthored = true;
        [Header("Stage C-3E — opt-in continuous path network")]
        [SerializeField] private bool useContinuousPathNetworkRenderer = false;
        [SerializeField] private SettlementPathMeshBuilder.MeshBuildConfig continuousPathMeshConfig =
            new SettlementPathMeshBuilder.MeshBuildConfig();

        private readonly WorldTerrainSampleCache _terrainSamples = new WorldTerrainSampleCache();
        private SettlementPathNetworkRenderer _continuousPathRenderer;
        private Transform _continuousPathRoot;

        private Transform _root;
        private readonly Dictionary<long, ChunkView> _views = new();
        private GameObject[] _rockPrefabs;
        private GameObject[] _hutPrefabs;
        private GameObject _pathPrefab;
        private GameObject _clearingPrefab;
        private Material _pathMat;
        private Material _clearingMat;
        private Material _hutBodyMat;
        private Material _hutRoofMat;
        private Material _hutDoorMat;
        private Material _stoneMat;
        private string _lastSignature;
        private GameSaveData _cachedData;
        private Transform _authoredSettlement;
        private readonly List<AuthoredFade> _authoredPathFades = new();
        private readonly List<AuthoredFade> _authoredHutFades = new();
        private bool _authoredCacheReady;

        private sealed class AuthoredFade
        {
            public Renderer renderer;
            public Color[] baseColors;
            public bool useBaseColor;
        }

        private sealed class ChunkView
        {
            public GameObject root;
            public string signature;
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
            if (data?.worldGrowth?.chunks == null || chunkManager == null)
            {
                ClearAll();
                _lastSignature = null;
                SoftDimAuthored(0, 0);
                return;
            }

            // CIF-A5: visualizer reads only — stone rebalance lives in WorldGrowthService commit path.
            var signature = BuildSignature(data);
            if (signature == _lastSignature && _views.Count > 0)
            {
                return;
            }

            _lastSignature = signature;
            EnsureRoot();
            _terrainSamples.Rebuild(data);
            EnsureMaterials();
            EnsureRockPrefabs();
            EnsureSettlementPrefabs();

            var keep = new HashSet<long>();
            var size = chunkManager.ChunkSizeMeters;
            var homePaths = 0;
            var homeHuts = 0;

            foreach (var growth in data.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                {
                    continue;
                }

                if (!HasAnySettlement(settle))
                {
                    continue;
                }

                var key = Pack(growth.chunkX, growth.chunkZ);
                keep.Add(key);
                var chunkSig = $"{growth.settlementMaskBase64}:{growth.stageMaskBase64}:{growth.isLegacyBase}";
                var view = EnsureView(key, growth.chunkX, growth.chunkZ);
                if (view.signature == chunkSig)
                {
                    view.root.SetActive(true);
                    CountHomeStages(data, growth, settle, ref homePaths, ref homeHuts);
                    continue;
                }

                ClearChildren(view.root.transform);
                view.signature = chunkSig;
                PlaceChunkProps(view.root.transform, growth, settle, size);
                view.root.SetActive(true);
                CountHomeStages(data, growth, settle, ref homePaths, ref homeHuts);
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

            if (useContinuousPathNetworkRenderer)
            {
                RebuildContinuousPathNetwork(data);
            }
            else
            {
                ClearContinuousPathNetwork();
            }

            SoftDimAuthored(homePaths, homeHuts);
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

        private void PlaceChunkProps(
            Transform parent,
            ChunkGrowthSaveData growth,
            byte[] settle,
            float chunkSize)
        {
            byte[] land = null;
            if (!growth.isLegacyBase)
            {
                ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out land, out _);
            }

            var seed = growth.growthSeed != 0
                ? growth.growthSeed
                : growth.chunkX * 73856093 ^ growth.chunkZ * 19349663;

            for (var i = 0; i < settle.Length; i++)
            {
                var stage = settle[i];
                if (stage == (byte)SettlementStage.None)
                {
                    continue;
                }

                if (WaterSourceLayout.BlocksSettlementCell(growth.chunkX, growth.chunkZ, i))
                {
                    continue;
                }

                var landStage = growth.isLegacyBase
                    ? (byte)CellStage.Moss
                    : (land != null && i < land.Length ? land[i] : (byte)CellStage.Void);
                if (!growth.isLegacyBase && landStage < (byte)CellStage.Moss)
                {
                    continue;
                }

                var local = GrowthWorldPlacement.CellLocalOnChunk(
                    growth.chunkX, growth.chunkZ, i, landStage, yBase, chunkSize);
                if (growth.isLegacyBase)
                {
                    // CellLocalOnChunk assumes grown height; lift to authored yard (~1.85).
                    local.y = GrowthWorldPlacement.ApproximateSurfaceY(landStage, isLegacyBase: true) - yBase;
                }

                var yaw = Hash01(seed, i, 7) * 360f;
                switch ((SettlementStage)stage)
                {
                    case SettlementStage.Clearing:
                        PlaceClearing(parent, local, i, seed);
                        break;
                    case SettlementStage.Path:
                        if (!useContinuousPathNetworkRenderer) if (!useContinuousPathNetworkRenderer) PlacePath(parent, local, i, yaw);
                        break;
                    case SettlementStage.Stone:
                        if (!useContinuousPathNetworkRenderer) PlacePath(parent, local, i, yaw);
                        PlaceStone(parent, local, i, seed, yaw);
                        break;
                    case SettlementStage.Hut:
                        if (!useContinuousPathNetworkRenderer) PlacePath(parent, local, i, yaw);
                        if (!SettlementCellQuery.IsHutVisualSuppressed(
                                _cachedData, growth.chunkX, growth.chunkZ, i))
                        {
                            PlaceHut(parent, local, i, yaw);
                        }
                        break;
                }
            }
        }

        private void PlaceClearing(Transform parent, Vector3 local, int cellIndex, int seed)
        {
            GameObject go;
            if (_clearingPrefab != null)
            {
                go = Instantiate(_clearingPrefab, parent);
                go.name = $"Clearing_{cellIndex}";
                go.transform.localPosition = local + new Vector3(0f, 0.01f, 0f);
                go.transform.localRotation = Quaternion.Euler(0f, Hash01(seed, cellIndex, 21) * 40f, 0f);
                var s = 0.92f + Hash01(seed, cellIndex, 19) * 0.12f;
                go.transform.localScale = Vector3.one * s;
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Clearing_{cellIndex}";
                go.transform.SetParent(parent, false);
                go.transform.localPosition = local + new Vector3(0f, 0.01f, 0f);
                go.transform.localScale = new Vector3(0.85f, 0.04f, 0.85f);
                go.transform.localRotation = Quaternion.Euler(0f, Hash01(seed, cellIndex, 21) * 40f, 0f);
                ApplyMat(go, _clearingMat);
                StripCollider(go);
            }
        }

        private void PlacePath(Transform parent, Vector3 local, int cellIndex, float yaw)
        {
            GameObject go;
            if (_pathPrefab != null)
            {
                go = Instantiate(_pathPrefab, parent);
                go.name = $"Path_{cellIndex}";
                // Tall enough to sit above grass noise; wide so roads read through forest.
                go.transform.localPosition = local + new Vector3(0f, 0.04f, 0f);
                go.transform.localRotation = Quaternion.Euler(0f, yaw * 0.15f, 0f);
                go.transform.localScale = Vector3.one * 1.05f;
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Path_{cellIndex}";
                go.transform.SetParent(parent, false);
                go.transform.localPosition = local + new Vector3(0f, 0.06f, 0f);
                go.transform.localScale = new Vector3(1.15f, 0.1f, 1.15f);
                go.transform.localRotation = Quaternion.Euler(0f, yaw * 0.15f, 0f);
                ApplyMat(go, _pathMat);
                StripCollider(go);
            }
        }

        private void PlaceStone(Transform parent, Vector3 local, int cellIndex, int seed, float yaw)
        {
            GameObject go;
            if (_rockPrefabs != null && _rockPrefabs.Length > 0)
            {
                var prefab = _rockPrefabs[Mathf.FloorToInt(Hash01(seed, cellIndex, 91) * 0.999f * _rockPrefabs.Length)];
                go = Instantiate(prefab, parent);
                go.name = $"Stone_{cellIndex}";
                go.transform.localPosition = local + new Vector3(0f, 0.05f, 0f);
                go.transform.localRotation = Quaternion.Euler(8f, yaw, 4f);
                var s = 0.2f + Hash01(seed, cellIndex, 33) * 0.14f;
                go.transform.localScale = Vector3.one * s;
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"Stone_{cellIndex}";
                go.transform.SetParent(parent, false);
                go.transform.localPosition = local + new Vector3(0f, 0.1f, 0f);
                go.transform.localScale = Vector3.one * (0.28f + Hash01(seed, cellIndex, 33) * 0.12f);
                go.transform.localRotation = Quaternion.Euler(10f, yaw, 5f);
                ApplyMat(go, _stoneMat);
                StripCollider(go);
            }
        }

        private void PlaceHut(Transform parent, Vector3 local, int cellIndex, float yaw)
        {
            // Stage 11G option D: shrink footprint to ~0.9 m so legacy 1 m Hut cells can all draw.
            var visualScale = Vector3.one * SettlementCellQuery.HutVisualUniformScale;

            if (_hutPrefabs != null && _hutPrefabs.Length > 0)
            {
                var prefab = _hutPrefabs[cellIndex % _hutPrefabs.Length];
                var hut = Instantiate(prefab, parent);
                hut.name = $"Hut_{cellIndex}";
                hut.transform.localPosition = local;
                hut.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                hut.transform.localScale = visualScale;
                return;
            }

            var fallback = new GameObject($"Hut_{cellIndex}");
            fallback.transform.SetParent(parent, false);
            fallback.transform.localPosition = local;
            fallback.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            fallback.transform.localScale = visualScale;

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(fallback.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.7f, 0f);
            body.transform.localScale = new Vector3(1.45f, 1.35f, 1.25f);
            ApplyMat(body, _hutBodyMat);
            StripCollider(body);

            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
            roof.name = "Roof";
            roof.transform.SetParent(fallback.transform, false);
            roof.transform.localPosition = new Vector3(0f, 1.55f, 0f);
            roof.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
            roof.transform.localScale = new Vector3(1.85f, 0.42f, 1.85f);
            ApplyMat(roof, _hutRoofMat);
            StripCollider(roof);

            var door = GameObject.CreatePrimitive(PrimitiveType.Cube);
            door.name = "Door";
            door.transform.SetParent(fallback.transform, false);
            door.transform.localPosition = new Vector3(0f, 0.48f, 0.64f);
            door.transform.localScale = new Vector3(0.42f, 0.85f, 0.1f);
            ApplyMat(door, _hutDoorMat);
            StripCollider(door);
        }

        private void SoftDimAuthored(int pathCells, int hutCells)
        {
            if (!softDimAuthored)
            {
                return;
            }

            EnsureAuthoredCache();
            var pathMul = pathCells <= 0 ? 1f : Mathf.Lerp(1f, 0.45f, Mathf.Clamp01(pathCells / 6f));
            var hutMul = hutCells <= 0 ? 1f : Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(hutCells / 3f));

            for (var i = 0; i < _authoredPathFades.Count; i++)
            {
                ApplyFade(_authoredPathFades[i], pathMul);
            }

            for (var i = 0; i < _authoredHutFades.Count; i++)
            {
                ApplyFade(_authoredHutFades[i], hutMul);
            }
        }

        private void EnsureAuthoredCache()
        {
            if (_authoredCacheReady && _authoredSettlement != null)
            {
                return;
            }

            _authoredPathFades.Clear();
            _authoredHutFades.Clear();
            _authoredSettlement = null;

            var island = GameObject.Find("World/Island") ?? GameObject.Find("Island");
            if (island == null)
            {
                _authoredCacheReady = true;
                return;
            }

            var settlement = island.transform.Find("Settlement");
            if (settlement == null)
            {
                _authoredCacheReady = true;
                return;
            }

            _authoredSettlement = settlement;
            for (var i = 0; i < settlement.childCount; i++)
            {
                var child = settlement.GetChild(i);
                var name = child.name;
                if (name.StartsWith("Path_") || name.StartsWith("Hearth") || name.StartsWith("Stone_"))
                {
                    CollectFades(child, _authoredPathFades);
                }
                else if (name.StartsWith("Hut_"))
                {
                    CollectFades(child, _authoredHutFades);
                }
            }

            _authoredCacheReady = true;
        }

        private static void CollectFades(Transform root, List<AuthoredFade> list)
        {
            var rs = root.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null)
                {
                    continue;
                }

                var mats = r.sharedMaterials;
                var colors = new Color[mats.Length];
                var useBase = false;
                for (var m = 0; m < mats.Length; m++)
                {
                    var mat = mats[m];
                    if (mat == null)
                    {
                        colors[m] = Color.white;
                        continue;
                    }

                    if (mat.HasProperty("_BaseColor"))
                    {
                        colors[m] = mat.GetColor("_BaseColor");
                        useBase = true;
                    }
                    else if (mat.HasProperty("_Color"))
                    {
                        colors[m] = mat.GetColor("_Color");
                    }
                    else
                    {
                        colors[m] = Color.white;
                    }
                }

                list.Add(new AuthoredFade
                {
                    renderer = r,
                    baseColors = colors,
                    useBaseColor = useBase
                });
            }
        }

        private static void ApplyFade(AuthoredFade fade, float mul)
        {
            if (fade?.renderer == null || fade.baseColors == null)
            {
                return;
            }

            // Instance materials once so we don't mutate shared authored mats permanently wrong.
            var mats = fade.renderer.materials;
            for (var i = 0; i < mats.Length && i < fade.baseColors.Length; i++)
            {
                var mat = mats[i];
                if (mat == null)
                {
                    continue;
                }

                var c = fade.baseColors[i];
                c.r *= Mathf.Lerp(0.7f, 1f, mul);
                c.g *= Mathf.Lerp(0.7f, 1f, mul);
                c.b *= Mathf.Lerp(0.7f, 1f, mul);
                c.a = Mathf.Clamp01(mul);
                if (fade.useBaseColor && mat.HasProperty("_BaseColor"))
                {
                    mat.SetColor("_BaseColor", c);
                }
                else if (mat.HasProperty("_Color"))
                {
                    mat.SetColor("_Color", c);
                }
            }
        }

        private static void CountHomeStages(
            GameSaveData save,
            ChunkGrowthSaveData growth,
            byte[] settle,
            ref int paths,
            ref int huts)
        {
            if (!growth.isLegacyBase)
            {
                return;
            }

            for (var i = 0; i < settle.Length; i++)
            {
                if (settle[i] >= (byte)SettlementStage.Path)
                {
                    paths++;
                }

                if (settle[i] == (byte)SettlementStage.Hut &&
                    !SettlementCellQuery.IsHutVisualSuppressed(save, growth.chunkX, growth.chunkZ, i))
                {
                    huts++;
                }
            }
        }

        private ChunkView EnsureView(long key, int chunkX, int chunkZ)
        {
            var pose = GrowthWorldPlacement.ChunkRootLocalPosition(chunkX, chunkZ, yBase);
            if (_views.TryGetValue(key, out var existing) && existing?.root != null)
            {
                existing.root.transform.localPosition = pose;
                existing.root.transform.localRotation = Quaternion.identity;
                existing.root.transform.localScale = Vector3.one;
                return existing;
            }

            var go = new GameObject($"SettlementChunk_{chunkX}_{chunkZ}");
            go.transform.SetParent(_root, false);
            go.transform.localPosition = pose;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            var view = new ChunkView { root = go };
            _views[key] = view;
            return view;
        }

        private void DestroyView(long key)
        {
            if (!_views.TryGetValue(key, out var view))
            {
                return;
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
                _root.localPosition = Vector3.zero;
                _root.localRotation = Quaternion.identity;
                _root.localScale = Vector3.one;
                return;
            }

            var go = new GameObject("SettlementGrown");
            Transform parent = transform;
            var worldGo = GameObject.Find("World");
            if (worldGo != null)
            {
                parent = worldGo.transform;
            }

            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            _root = go.transform;
        }

        private void EnsureMaterials()
        {
            if (_pathMat != null)
            {
                return;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                         ?? Shader.Find("Standard");

            // High-contrast vs green forest canopy.
            _pathMat = MakeMat(shader, "Settle_Path", new Color(0.78f, 0.62f, 0.38f));
            _clearingMat = MakeMat(shader, "Settle_Clearing", new Color(0.5f, 0.4f, 0.28f));
            _hutBodyMat = MakeMat(shader, "Settle_HutBody", new Color(0.72f, 0.42f, 0.22f));
            _hutRoofMat = MakeMat(shader, "Settle_HutRoof", new Color(0.55f, 0.18f, 0.12f));
            _hutDoorMat = MakeMat(shader, "Settle_HutDoor", new Color(0.22f, 0.12f, 0.08f));
            _stoneMat = MakeMat(shader, "Settle_Stone", new Color(0.55f, 0.52f, 0.5f));
        }

        private static Material MakeMat(Shader shader, string name, Color color)
        {
            var mat = new Material(shader) { name = name };
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }

            if (mat.HasProperty("_Color"))
            {
                mat.SetColor("_Color", color);
            }

            if (mat.HasProperty("_Smoothness"))
            {
                mat.SetFloat("_Smoothness", 0.12f);
            }

            return mat;
        }

        private void EnsureRockPrefabs()
        {
            if (_rockPrefabs != null)
            {
                return;
            }

            var list = new List<GameObject>(4);
            TryAdd(list, "AfkIsland/Decor/Rock_A");
            TryAdd(list, "AfkIsland/Decor/Rock_B");
            TryAdd(list, "AfkIsland/Decor/Rock_C");
            TryAdd(list, "AfkIsland/Decor/Rock_D");
            _rockPrefabs = list.ToArray();
        }

        private void EnsureSettlementPrefabs()
        {
            if (_hutPrefabs != null)
            {
                return;
            }

            _pathPrefab = Resources.Load<GameObject>("AfkIsland/Settlement/Path_Tile");
            _clearingPrefab = Resources.Load<GameObject>("AfkIsland/Settlement/Clearing_Pad");
            var huts = new List<GameObject>(2);
            TryAdd(huts, "AfkIsland/Settlement/Hut_A");
            TryAdd(huts, "AfkIsland/Settlement/Hut_B");
            _hutPrefabs = huts.Count > 0 ? huts.ToArray() : System.Array.Empty<GameObject>();
        }

        private static void TryAdd(List<GameObject> list, string path)
        {
            var p = Resources.Load<GameObject>(path);
            if (p != null)
            {
                list.Add(p);
            }
        }

        private static void ApplyMat(GameObject go, Material mat)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null && mat != null)
            {
                r.sharedMaterial = mat;
                r.shadowCastingMode = ShadowCastingMode.On;
                r.receiveShadows = true;
            }
        }

        private static void StripCollider(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                Destroy(col);
            }
        }

        private static void ClearChildren(Transform t)
        {
            for (var i = t.childCount - 1; i >= 0; i--)
            {
                Destroy(t.GetChild(i).gameObject);
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

        private static bool HasAnySettlement(byte[] settle)
        {
            for (var i = 0; i < settle.Length; i++)
            {
                if (settle[i] != (byte)SettlementStage.None)
                {
                    return true;
                }
            }

            return false;
        }

        private string BuildSignature(GameSaveData data)
        {
            if (data?.worldGrowth?.chunks == null)
            {
                return "empty";
            }

            var parts = new List<string>(data.worldGrowth.chunks.Count);
            foreach (var growth in data.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                parts.Add(useContinuousPathNetworkRenderer
                    ? growth.chunkX + ":" + growth.chunkZ + ":" + growth.settlementMaskBase64 + ":" + growth.stageMaskBase64 + ":" + growth.biomeMaskBase64
                    : growth.chunkX + ":" + growth.chunkZ + ":" + growth.settlementMaskBase64);
            }

            parts.Sort();
            return string.Join("|", parts);
        }

        private void RebuildContinuousPathNetwork(GameSaveData data)
        {
            if (data?.worldGrowth?.chunks == null || chunkManager == null)
            {
                ClearContinuousPathNetwork();
                return;
            }

            EnsureMaterials();
            EnsureContinuousPathRenderer();

            var config = new SettlementPathTopology.TopologyConfig
            {
                ChunkSizeMeters = chunkManager.ChunkSizeMeters,
                GridSize = ChunkMaskCodec.GridSize
            };
            var input = new SettlementPathTopology.TopologyInput
            {
                Config = config,
                StarterPaths = SettlementPathTopology.CreateProductionStarterPaths(config),
                Huts = SettlementPathTopology.CreateProductionStarterHuts(),
                IsInsideLand = _terrainSamples.IsInsideLand,
                IsBlockedByWater = IsBlockedByWaterForPath
            };

            foreach (var growth in data.worldGrowth.chunks)
            {
                if (growth == null ||
                    !ChunkMaskCodec.TryDecodeBase64(growth.settlementMaskBase64, out var settle, out _))
                    continue;

                var seed = growth.growthSeed != 0
                    ? growth.growthSeed
                    : growth.chunkX * 73856093 ^ growth.chunkZ * 19349663;

                for (var i = 0; i < settle.Length; i++)
                {
                    var stage = (SettlementStage)settle[i];
                    if (stage == SettlementStage.Path || stage == SettlementStage.Stone)
                    {
                        ChunkMaskCodec.IndexToLocal(i, out var lx, out var lz);
                        input.RoadCells.Add(new SettlementPathTopology.MaskCellInput
                        {
                            WorldCellX = growth.chunkX * ChunkMaskCodec.GridSize + lx,
                            WorldCellZ = growth.chunkZ * ChunkMaskCodec.GridSize + lz,
                            Stage = settle[i]
                        });
                    }
                    else if (stage == SettlementStage.Hut &&
                             !SettlementCellQuery.IsHutVisualSuppressed(data, growth.chunkX, growth.chunkZ, i))
                    {
                        var center = GrowthWorldPlacement.CellCenterWorld(growth.chunkX, growth.chunkZ, i);
                        var yaw = Hash01(seed, i, 7) * 360f;
                        input.Huts.Add(new SettlementPathTopology.HutInput
                        {
                            Id = "MaskHut_" + growth.chunkX + "_" + growth.chunkZ + "_" + i.ToString("D3"),
                            Center = new Vector2(center.x, center.z),
                            YawDegrees = yaw,
                            Scale = SettlementCellQuery.HutVisualUniformScale,
                            FootprintHalfExtents = new Vector2(0.47f, 0.44f),
                            DoorLocalZ = 0.598f,
                            AllowVisualDoorCorrection = true
                        });
                    }
                }
            }

            var plan = SettlementPathTopology.Build(input);
            continuousPathMeshConfig.ChunkSizeMeters = chunkManager.ChunkSizeMeters;
            continuousPathMeshConfig.LocalChunkYBase = yBase;
            _continuousPathRenderer.ApplyPlan(
                plan,
                _terrainSamples.SampleHeight,
                continuousPathMeshConfig,
                forceFullRebuild: false,
                terrainVersion: _terrainSamples.Revision);
        }

        private static bool IsBlockedByWaterForPath(float worldX, float worldZ)
        {
            var center = WaterSourceLayout.GetWorldCenter();
            const float clearance = 0.35f;
            var rx = WaterSourceLayout.WaterHalfExtentX + clearance;
            var rz = WaterSourceLayout.WaterHalfExtentZ + clearance;
            var dx = (worldX - center.x) / rx;
            var dz = (worldZ - center.z) / rz;
            return dx * dx + dz * dz <= 1f;
        }

        private void EnsureContinuousPathRenderer()
        {
            if (_continuousPathRenderer != null)
                return;

            var world = GameObject.Find("World");
            var parent = world != null ? world.transform : transform;
            var root = new GameObject("SettlementPathNetwork");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            _continuousPathRoot = root.transform;
            _continuousPathRenderer = root.AddComponent<SettlementPathNetworkRenderer>();
            _continuousPathRenderer.ConfigureMaterials(_pathMat, _pathMat);
        }

        private void ClearContinuousPathNetwork()
        {
            if (_continuousPathRenderer != null)
                _continuousPathRenderer.Clear();

            if (_continuousPathRoot != null)
            {
                Destroy(_continuousPathRoot.gameObject);
                _continuousPathRoot = null;
            }

            _continuousPathRenderer = null;
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
            ClearContinuousPathNetwork();
            DestroyMat(ref _pathMat);
            DestroyMat(ref _clearingMat);
            DestroyMat(ref _hutBodyMat);
            DestroyMat(ref _hutRoofMat);
            DestroyMat(ref _hutDoorMat);
            DestroyMat(ref _stoneMat);
        }

        private static void DestroyMat(ref Material mat)
        {
            if (mat != null)
            {
                Destroy(mat);
                mat = null;
            }
        }
    }
}
