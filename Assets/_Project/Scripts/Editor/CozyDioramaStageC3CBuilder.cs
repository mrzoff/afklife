#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Stage C-3C: isolated terrain-aware two-chunk path-network review scene.
    /// Uses real ChunkTerrainMesher.Build + ChunkTerrainMesher.SampleVisualHeight across
    /// chunks (0,0) and (1,0), SettlementPathTopology and SettlementPathMeshBuilder.
    /// Does not modify any production code, Save v8, ResidentPathGraph, or prior review scenes (B-2 / C-2).
    /// </summary>
    public static class CozyDioramaStageC3CBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3C.unity";
        private const string StageB2ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageB2.unity";
        private const string StageC2ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC2.unity";
        private const string MeshFolder = "Assets/_Project/Art/CozyDiorama/StageC3C";
        private const string MaterialsFolder = "Assets/_Project/Art/CozyDiorama/Materials";
        private const string SettlementPrefabs = "Assets/_Project/Resources/AfkIsland/Settlement/";
        private const string NaturePrefabs = "Assets/Polyeler/EssentialNaturePack/Prefabs/";
        private const string BuildReportDir = "Builds/cozy-diorama";
        private const string BuildReportPath = "Builds/cozy-diorama/stage-c3c-report.txt";
        private const string BuildShotsDir = "Builds/cozy-diorama/stage-c3c-shots";
        private const string DocsShotsDir = "Docs/CozyDiorama/StageC3C-Shots";

        private static readonly Vector2 PondCenter = new Vector2(WaterSourceLayout.LocalPosition.x, WaterSourceLayout.LocalPosition.z);
        private const float PondRawRx = WaterSourceLayout.WaterHalfExtentX; // 1.1
        private const float PondRawRz = WaterSourceLayout.WaterHalfExtentZ; // 0.8

        private static readonly Vector2 HearthXZ = new Vector2(0.15f, -1.50f);

        // ------------------------------------------------------------------ menu

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3/Build Terrain-Aware Review Scene (C-3C)")]
        public static void BuildMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            try
            {
                var b2Before = FileHash(StageB2ScenePath);
                var c2Before = FileHash(StageC2ScenePath);

                Directory.CreateDirectory(MeshFolder);
                Directory.CreateDirectory(MaterialsFolder);
                Directory.CreateDirectory(BuildReportDir);
                Directory.CreateDirectory(BuildShotsDir);
                Directory.CreateDirectory(DocsShotsDir);

                // 1. Open fresh empty scene FIRST so newly built Mesh instances are not destroyed by scene unload
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.76f, 0.85f, 0.92f);
                RenderSettings.ambientEquatorColor = new Color(0.66f, 0.72f, 0.65f);
                RenderSettings.ambientGroundColor = new Color(0.42f, 0.44f, 0.36f);

                // 2. Build two-chunk organic land mask & cached sampler
                var landMasks = BuildTwoChunkLandMasks();
                byte SampleStage(int wx, int wz)
                {
                    var cx = FloorDiv(wx, ChunkMaskCodec.GridSize);
                    var cz = FloorDiv(wz, ChunkMaskCodec.GridSize);
                    if (!landMasks.TryGetValue(new Vector2Int(cx, cz), out var mask))
                    {
                        return (byte)CellStage.Void;
                    }

                    var lx = wx - cx * ChunkMaskCodec.GridSize;
                    var lz = wz - cz * ChunkMaskCodec.GridSize;
                    return mask[ChunkMaskCodec.CellIndex(lx, lz)];
                }

                float SampleHeight(float wx, float wz)
                {
                    return ChunkTerrainMesher.SampleVisualHeight(wx, wz, SampleStage);
                }

                bool IsInsideLand(float wx, float wz)
                {
                    var gx = Mathf.FloorToInt(wx + 8f);
                    var gz = Mathf.FloorToInt(wz + 8f);
                    if (SampleStage(gx, gz) < (byte)CellStage.Soil) return false;
                    // Keep 1-cell inland margin from ocean drop-off
                    return SampleStage(gx + 1, gz) >= (byte)CellStage.Roots
                           && SampleStage(gx - 1, gz) >= (byte)CellStage.Roots
                           && SampleStage(gx, gz + 1) >= (byte)CellStage.Roots
                           && SampleStage(gx, gz - 1) >= (byte)CellStage.Roots;
                }

                bool IsBlockedByWater(float wx, float wz)
                {
                    var nx = (wx - PondCenter.x) / PondRawRx;
                    var nz = (wz - PondCenter.y) / PondRawRz;
                    return nx * nx + nz * nz <= 1f;
                }

                // 2. Build topology & per-chunk meshes TWICE to verify 100% determinism
                var topoInput = CreateC3CTopologyInput(IsInsideLand, IsBlockedByWater);
                var meshCfg = new SettlementPathMeshBuilder.MeshBuildConfig
                {
                    ChunkSizeMeters = 16f,
                    CoreYOffset = 0.036f,
                    FringeYOffset = 0.022f,
                    FringeExtraWidth = 0.095f,
                    WidthVariationAmplitude = 0.05f,
                    LocalToChunkOrigin = true,
                    LocalChunkYBase = GrowthWorldPlacement.VisualYBase
                };

                var plan1 = SettlementPathTopology.Build(topoInput);
                var plan2 = SettlementPathTopology.Build(topoInput);
                var meshRep1 = SettlementPathMeshBuilder.BuildPerChunk(plan1, SampleHeight, meshCfg);
                var meshRep2 = SettlementPathMeshBuilder.BuildPerChunk(plan2, SampleHeight, meshCfg);

                var planDeterministic = plan1.TopologyHash == plan2.TopologyHash;
                var meshDeterministic = meshRep1.MeshHash == meshRep2.MeshHash;

                // Destroy temporary second build meshes
                foreach (var kv in meshRep2.Chunks)
                {
                    if (kv.Value.CoreMesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.CoreMesh);
                    if (kv.Value.FringeMesh != null) UnityEngine.Object.DestroyImmediate(kv.Value.FringeMesh);
                }

                // 3. Populate lighting & materials in the fresh isolated scene

                // Lighting
                var lightGo = new GameObject("Sun_Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.25f;
                light.color = new Color(1.0f, 0.96f, 0.89f);
                light.shadows = LightShadows.Soft;
                lightGo.transform.rotation = Quaternion.Euler(48f, -38f, 0f);

                // Materials (Stage C-3C isolated)
                var matGrass = EnsureLitMat("Cozy_C3C_TerrainTop", new Color(0.49f, 0.63f, 0.33f), 0.08f);
                var matCliff = EnsureLitMat("Cozy_C3C_TerrainSide", new Color(0.56f, 0.49f, 0.37f), 0.06f);
                var matWater = EnsureLitMat("Cozy_C3C_Ocean", new Color(0.32f, 0.61f, 0.69f), 0.25f);
                var matPond = EnsureLitMat("Cozy_C3C_Pond", new Color(0.24f, 0.56f, 0.72f), 0.35f);
                var matCore = EnsureLitMat("Cozy_C3C_PathCore", new Color(0.68f, 0.57f, 0.39f), 0.07f);
                var matFringe = EnsureLitMat("Cozy_C3C_PathFringe", new Color(0.57f, 0.57f, 0.37f), 0.05f);
                var matHearthStone = EnsureLitMat("Cozy_C3C_HearthStone", new Color(0.36f, 0.35f, 0.34f), 0.10f);
                var matHearthFire = EnsureLitMat("Cozy_C3C_HearthFire", new Color(0.96f, 0.46f, 0.14f), 0.20f);
                var matBorderPin = EnsureLitMat("Cozy_C3C_BorderMarker", new Color(0.95f, 0.82f, 0.22f), 0.15f);

                // Ocean plane
                var ocean = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ocean.name = "Ocean_Water";
                ocean.transform.position = new Vector3(4f, 0.18f, 0f);
                ocean.transform.localScale = new Vector3(12f, 1f, 12f);
                ApplyMat(ocean, matWater, castShadows: false);
                StripColliders(ocean);

                // 4. Build and save real ChunkTerrainMesher chunks (0,0) and (1,0)
                var worldRoot = new GameObject("World_StageC3C");
                var terrainRoot = new GameObject("TerrainChunks");
                terrainRoot.transform.SetParent(worldRoot.transform, false);

                Vector2Int[] coords = { new Vector2Int(0, 0), new Vector2Int(1, 0) };
                for (var i = 0; i < coords.Length; i++)
                {
                    var c = coords[i];
                    var terrainMesh = ChunkTerrainMesher.Build(c.x, c.y, 16f, SampleStage, seed: 100 + i);
                    var terrainAsset = SaveMeshAsset(terrainMesh, $"{MeshFolder}/TerrainChunk_{c.x}_{c.y}.asset");

                    var chunkGo = new GameObject($"TerrainChunk_{c.x}_{c.y}");
                    chunkGo.transform.SetParent(terrainRoot.transform, false);
                    chunkGo.transform.position = GrowthWorldPlacement.ChunkRootLocalPosition(c.x, c.y, GrowthWorldPlacement.VisualYBase);
                    var mf = chunkGo.AddComponent<MeshFilter>();
                    mf.sharedMesh = terrainAsset;
                    var mr = chunkGo.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = new[] { matGrass, matCliff };
                    mr.shadowCastingMode = ShadowCastingMode.On;
                    mr.receiveShadows = true;
                }

                // 5. Save & attach per-chunk path network meshes
                var pathsRoot = new GameObject("PathNetworkChunks");
                pathsRoot.transform.SetParent(worldRoot.transform, false);
                var sortedChunks = new List<Vector2Int>(meshRep1.Chunks.Keys);
                sortedChunks.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
                for (var i = 0; i < sortedChunks.Count; i++)
                {
                    var ck = sortedChunks[i];
                    var data = meshRep1.Chunks[ck];

                    var chunkPathsGo = new GameObject($"PathChunk_{ck.x}_{ck.y}");
                    chunkPathsGo.transform.SetParent(pathsRoot.transform, false);
                    chunkPathsGo.transform.position = GrowthWorldPlacement.ChunkRootLocalPosition(ck.x, ck.y, GrowthWorldPlacement.VisualYBase);

                    var coreAsset = SaveMeshAsset(data.CoreMesh, $"{MeshFolder}/PathCore_{ck.x}_{ck.y}.asset");
                    var fringeAsset = SaveMeshAsset(data.FringeMesh, $"{MeshFolder}/PathFringe_{ck.x}_{ck.y}.asset");

                    CreateMeshChild(chunkPathsGo.transform, "Fringe", fringeAsset, matFringe);
                    CreateMeshChild(chunkPathsGo.transform, "Core", coreAsset, matCore);
                }

                // 6. Authored Pond, Hearth, Huts (using effective visual yaw from planner!), Rocks, Trees, Border Markers
                var propsRoot = new GameObject("SettlementProps");
                propsRoot.transform.SetParent(worldRoot.transform, false);

                // Pond at WaterSourceLayout.LocalPosition XZ
                var pondY = SampleHeight(PondCenter.x, PondCenter.y);
                var pond = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pond.name = "WaterSource_Pond";
                pond.transform.SetParent(propsRoot.transform, false);
                pond.transform.position = new Vector3(PondCenter.x, pondY + 0.015f, PondCenter.y);
                pond.transform.localScale = new Vector3(PondRawRx * 2f, 0.04f, PondRawRz * 2f);
                ApplyMat(pond, matPond, castShadows: false);
                StripColliders(pond);

                // Hearth
                var hearthY = SampleHeight(HearthXZ.x, HearthXZ.y);
                var hearth = GameObject.CreatePrimitive(PrimitiveType.Cube);
                hearth.name = "Hearth";
                hearth.transform.SetParent(propsRoot.transform, false);
                hearth.transform.position = new Vector3(HearthXZ.x, hearthY + 0.08f, HearthXZ.y);
                hearth.transform.localScale = new Vector3(0.86f, 0.16f, 0.86f);
                ApplyMat(hearth, matHearthStone, castShadows: true);
                StripColliders(hearth);

                var fire = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fire.name = "HearthFire";
                fire.transform.SetParent(hearth.transform, false);
                fire.transform.localPosition = new Vector3(0f, 0.85f, 0f);
                fire.transform.localScale = new Vector3(0.38f, 0.7f, 0.38f);
                ApplyMat(fire, matHearthFire, castShadows: false);
                StripColliders(fire);

                // Huts (place at exact simulation center, apply effective visual yaw from topology report!)
                var reportById = new Dictionary<string, SettlementPathTopology.HutBranchReport>(StringComparer.Ordinal);
                for (var i = 0; i < plan1.HutReports.Count; i++)
                {
                    reportById[plan1.HutReports[i].HutId] = plan1.HutReports[i];
                }

                var hutAPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SettlementPrefabs + "Hut_A.prefab");
                var hutBPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SettlementPrefabs + "Hut_B.prefab");

                for (var i = 0; i < topoInput.Huts.Count; i++)
                {
                    var h = topoInput.Huts[i];
                    var useB = h.Id.Contains("Hut_B") || h.Id.Contains("East");
                    var prefab = useB ? hutBPrefab : hutAPrefab;
                    var effectiveYaw = reportById.TryGetValue(h.Id, out var hr) ? hr.EffectiveYawDegrees : h.YawDegrees;
                    var y = SampleHeight(h.Center.x, h.Center.y);

                    GameObject inst;
                    if (prefab != null)
                    {
                        inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, propsRoot.transform);
                    }
                    else
                    {
                        inst = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        inst.transform.SetParent(propsRoot.transform, false);
                    }

                    inst.name = h.Id;
                    inst.transform.position = new Vector3(h.Center.x, y, h.Center.y);
                    inst.transform.rotation = Quaternion.Euler(0f, effectiveYaw, 0f);
                    inst.transform.localScale = Vector3.one * h.Scale;
                    StripColliders(inst);
                }

                // Rocks (matching obstacles)
                var rockPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(NaturePrefabs + "Rock_A.prefab");
                for (var i = 0; i < topoInput.Obstacles.Count; i++)
                {
                    var ob = topoInput.Obstacles[i];
                    if (ob.Id == "Hearth") continue;
                    var y = SampleHeight(ob.Center.x, ob.Center.y);
                    var go = rockPrefab != null
                        ? (GameObject)PrefabUtility.InstantiatePrefab(rockPrefab, propsRoot.transform)
                        : GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = ob.Id;
                    go.transform.SetParent(propsRoot.transform, false);
                    go.transform.position = new Vector3(ob.Center.x, y, ob.Center.y);
                    go.transform.localScale = Vector3.one * 0.42f;
                    StripColliders(go);
                }

                // Perimeter trees (V2 approved scale, outside road/hut/pond clearances)
                PlacePerimeterTrees(propsRoot.transform, SampleHeight);

                // Two subtle border marker stakes on pure grass along x = 8.0 m (outside all roads & huts)
                PlaceBorderMarker(propsRoot.transform, "ChunkBorderStake_S", 8.0f, -2.0f, SampleHeight, matBorderPin);
                PlaceBorderMarker(propsRoot.transform, "ChunkBorderStake_N", 8.0f, 4.9f, SampleHeight, matBorderPin);

                // Camera
                var camGo = new GameObject("StageC3C_Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 13f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.32f, 0.61f, 0.69f);
                cam.nearClipPlane = 0.5f;
                cam.farClipPlane = 120f;
                PositionOrthoCamera(camGo.transform, new Vector3(4.5f, 0.8f, 0.0f), 45f, 45f, 34f);

                // 7. Save scene & verify B-2 / C-2 untouched
                AssetDatabase.SaveAssets();
                EditorSceneManager.SaveScene(scene, ScenePath);

                var b2After = FileHash(StageB2ScenePath);
                var c2After = FileHash(StageC2ScenePath);
                WriteReport(plan1, meshRep1, planDeterministic, meshDeterministic,
                    b2Before, b2After, c2Before, c2After);

                // Reload saved scene and defer shot rendering by 2 ticks (avoids GPU Resident Drawer ghost meshes)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                EditorApplication.delayCall += () => EditorApplication.delayCall += RenderAllShots;

                Debug.Log($"[AFK Island] Stage C-3C review scene built: {ScenePath}. Deterministic={planDeterministic && meshDeterministic}. Rendering shots...");
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] Stage C-3C build failed: " + e);
            }
        }

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3/Render C-3C Review Shots")]
        public static void RenderShotsMenu()
        {
            if (SceneManager.GetActiveScene().path != ScenePath)
            {
                if (!File.Exists(ScenePath))
                {
                    Debug.LogError("[AFK Island] C-3C scene does not exist yet: " + ScenePath);
                    return;
                }

                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            EditorApplication.delayCall += () => EditorApplication.delayCall += RenderAllShots;
        }

        // ------------------------------------------------------------------ Fixture Setup

        private static Dictionary<Vector2Int, byte[]> BuildTwoChunkLandMasks()
        {
            var dict = new Dictionary<Vector2Int, byte[]>
            {
                [new Vector2Int(0, 0)] = new byte[ChunkMaskCodec.CellCount],
                [new Vector2Int(1, 0)] = new byte[ChunkMaskCodec.CellCount]
            };

            // Organic rounded two-chunk island spanning x in [-6.5, +17.5], z in [-5.8, +6.2]
            // Inner hills get Vegetation (higher elevation) while outer ring gets Moss & Soil for real slope transitions!
            for (var cx = 0; cx <= 1; cx++)
            {
                var mask = dict[new Vector2Int(cx, 0)];
                for (var lz = 0; lz < 16; lz++)
                {
                    for (var lx = 0; lx < 16; lx++)
                    {
                        var wx = cx * 16 + lx;
                        var wz = lz;
                        var center = SettlementPathTopology.CellCenterXZ(wx, wz);

                        // Elliptical island centered at (5.0, 0.2), rx = 12.6, rz = 6.9 with gentle organic wave
                        var dx = (center.x - 5.0f) / 12.6f;
                        var dz = (center.y - 0.2f) / 6.9f;
                        var ang = Mathf.Atan2(dz, dx);
                        var wave = 1.0f + 0.06f * Mathf.Sin(3f * ang + 0.4f) + 0.04f * Mathf.Cos(5f * ang - 0.7f);
                        var r = Mathf.Sqrt(dx * dx + dz * dz) / wave;

                        byte stage;
                        if (r > 1.02f) stage = (byte)CellStage.Void;
                        else if (r > 0.94f) stage = (byte)CellStage.Roots;
                        else if (r > 0.86f) stage = (byte)CellStage.Soil;
                        else
                        {
                            // Gentle elevation variation across chunk border and east hill
                            var hillWave = Mathf.Sin(center.x * 0.42f) * Mathf.Cos(center.y * 0.45f);
                            stage = hillWave > 0.15f ? (byte)CellStage.Vegetation : (byte)CellStage.Moss;
                        }

                        mask[ChunkMaskCodec.CellIndex(lx, lz)] = stage;
                    }
                }
            }

            return dict;
        }

        private static SettlementPathTopology.TopologyInput CreateC3CTopologyInput(
            Func<float, float, bool> isInsideLand,
            Func<float, float, bool> isBlockedByWater)
        {
            var cfg = new SettlementPathTopology.TopologyConfig
            {
                SampleStepMeters = 0.12f,
                AllowVisualDoorCorrection = true
            };

            // Production Starter_Main (exact) + Starter_Cross gently curved 0.25 m north of pond edge (-0.60)
            // so the path fringe stays cleanly out of the pond water!
            var starters = new List<SettlementPathTopology.StarterPolylineInput>
            {
                new SettlementPathTopology.StarterPolylineInput
                {
                    Id = "Starter_Main",
                    HalfWidth = cfg.StarterMainHalfWidth,
                    SmoothCatmullRom = true,
                    ControlPoints = new List<Vector2>
                    {
                        new Vector2(0.05f, -0.95f),
                        new Vector2(0.00f, -0.50f),
                        new Vector2(0.00f, 0.20f),
                        new Vector2(0.00f, 1.30f),
                        new Vector2(0.00f, 2.30f)
                    }
                },
                new SettlementPathTopology.StarterPolylineInput
                {
                    Id = "Starter_Cross",
                    HalfWidth = cfg.StarterCrossHalfWidth,
                    SmoothCatmullRom = true,
                    ControlPoints = new List<Vector2>
                    {
                        new Vector2(-0.30f, -0.58f),
                        new Vector2(0.55f, -0.42f),
                        new Vector2(1.45f, -0.22f)
                    }
                }
            };

            // Road cells spanning chunk (0,0) -> chunk (1,0) across border x=8.0 (cells wx=15 -> wx=16)
            var roadCells = new List<SettlementPathTopology.MaskCellInput>();
            void AddRoad(int wx, int wz, byte stage = 2) =>
                roadCells.Add(new SettlementPathTopology.MaskCellInput { WorldCellX = wx, WorldCellZ = wz, Stage = stage });

            // East-west trunk crossing chunk border x = 8.0 m (wx=8..21, wz=8)
            for (var wx = 8; wx <= 21; wx++) AddRoad(wx, 8, wx == 13 ? (byte)3 : (byte)2);

            // 4-way crossroads at (18, 8): north arm (18, 9..11) and south arm (18, 5..7)
            AddRoad(18, 9); AddRoad(18, 10); AddRoad(18, 11);
            AddRoad(18, 7); AddRoad(18, 6); AddRoad(18, 5);

            // T-junction at (18, 7) leading east toward shore (19..20, 7) & (19..20, 6)
            AddRoad(19, 7); AddRoad(20, 7);
            AddRoad(19, 6); AddRoad(20, 6);

            // Pond-adjacent south-west lane wrapping cleanly WEST of the Hearth (x=-1.5) down to (8, 4)
            AddRoad(6, 7); AddRoad(6, 6); AddRoad(6, 5); AddRoad(7, 5); AddRoad(7, 4); AddRoad(8, 4);

            // Huts: 2 production starter poses (cozy diorama scale 0.62) + 4 grown test huts
            var huts = new List<SettlementPathTopology.HutInput>
            {
                new SettlementPathTopology.HutInput
                {
                    Id = "Starter_Hut_A",
                    Center = new Vector2(-1.60f, 0.90f),
                    YawDegrees = 12f,
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(1.35f * 0.5f, 1.15f * 0.5f),
                    DoorLocalZ = 1.15f * 0.52f
                },
                new SettlementPathTopology.HutInput
                {
                    Id = "Starter_Hut_B",
                    Center = new Vector2(1.80f, -0.20f),
                    YawDegrees = -25f,
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(1.55f * 0.5f, 1.25f * 0.5f),
                    DoorLocalZ = 1.25f * 0.52f
                },
                new SettlementPathTopology.HutInput
                {
                    Id = "Hut_Grown_North",
                    Center = SettlementPathTopology.CellCenterXZ(18, 12, cfg),
                    YawDegrees = 180f,
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(1.35f * 0.5f, 1.15f * 0.5f),
                    DoorLocalZ = 1.15f * 0.52f
                },
                new SettlementPathTopology.HutInput
                {
                    Id = "Hut_Grown_East_Reversed",
                    Center = SettlementPathTopology.CellCenterXZ(22, 8, cfg),
                    YawDegrees = 90f, // facing away (+X) -> demonstrates visual-only door correction!
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(1.55f * 0.5f, 1.25f * 0.5f),
                    DoorLocalZ = 1.25f * 0.52f
                },
                new SettlementPathTopology.HutInput
                {
                    Id = "Hut_Grown_BorderN",
                    Center = SettlementPathTopology.CellCenterXZ(15, 11, cfg),
                    YawDegrees = 150f,
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(1.35f * 0.5f, 1.15f * 0.5f),
                    DoorLocalZ = 1.15f * 0.52f
                },
                new SettlementPathTopology.HutInput
                {
                    Id = "Hut_Grown_PondSW",
                    Center = SettlementPathTopology.CellCenterXZ(8, 3, cfg),
                    YawDegrees = 0f,
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(1.35f * 0.5f, 1.15f * 0.5f),
                    DoorLocalZ = 1.15f * 0.52f
                }
            };

            return new SettlementPathTopology.TopologyInput
            {
                Config = cfg,
                StarterPaths = starters,
                RoadCells = roadCells,
                Huts = huts,
                Obstacles = new List<SettlementPathTopology.CircleObstacle>
                {
                    new SettlementPathTopology.CircleObstacle { Id = "Hearth", Center = HearthXZ, Radius = 0.52f },
                    new SettlementPathTopology.CircleObstacle { Id = "Rock_West", Center = new Vector2(-2.7f, -1.2f), Radius = 0.36f },
                    new SettlementPathTopology.CircleObstacle { Id = "Rock_Mid", Center = new Vector2(5.4f, -1.6f), Radius = 0.36f }
                },
                IsInsideLand = isInsideLand,
                IsBlockedByWater = isBlockedByWater
            };
        }

        private static void PlacePerimeterTrees(Transform parent, Func<float, float, float> sampleHeight)
        {
            (string prefab, float x, float z, float scale, float yaw)[] trees =
            {
                ("JapaneseCedar.prefab", -4.2f, 2.4f, 0.34f, 20f),
                ("CherryBlossom.prefab", -3.4f, 3.6f, 0.42f, 80f),
                ("SawtoothOak.prefab", -4.6f, -1.5f, 0.38f, 145f),
                ("Pine.prefab", 2.2f, 3.9f, 0.36f, 40f),
                ("PlaneTree.prefab", 6.2f, 4.1f, 0.40f, 110f),
                ("JapaneseCedar.prefab", 13.6f, 3.6f, 0.34f, 195f),
                ("SawtoothOak.prefab", 13.2f, -3.7f, 0.38f, 260f),
                ("Pine.prefab", 3.5f, -4.4f, 0.35f, 310f)
            };

            for (var i = 0; i < trees.Length; i++)
            {
                var t = trees[i];
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(NaturePrefabs + t.prefab);
                if (prefab == null) continue;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                inst.name = $"Tree_{i:00}_{Path.GetFileNameWithoutExtension(t.prefab)}";
                inst.transform.position = new Vector3(t.x, sampleHeight(t.x, t.z), t.z);
                inst.transform.rotation = Quaternion.Euler(0f, t.yaw, 0f);
                inst.transform.localScale = Vector3.one * t.scale;
                StripColliders(inst);
            }
        }

        private static void PlaceBorderMarker(
            Transform parent, string name, float x, float z, Func<float, float, float> sampleHeight, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(x, sampleHeight(x, z) + 0.28f, z);
            go.transform.localScale = new Vector3(0.08f, 0.28f, 0.08f);
            ApplyMat(go, mat, castShadows: true);
            StripColliders(go);
        }

        // ------------------------------------------------------------------ Shot rendering

        private static void RenderAllShots()
        {
            var camGo = GameObject.Find("StageC3C_Camera");
            if (camGo == null)
            {
                Debug.LogError("[AFK Island] StageC3C_Camera not found in active scene.");
                return;
            }

            var cam = camGo.GetComponent<Camera>();
            var origPos = cam.transform.position;
            var origRot = cam.transform.rotation;
            var origSize = cam.orthographicSize;
            var origOrtho = cam.orthographic;
            var origFov = cam.fieldOfView;

            Directory.CreateDirectory(BuildShotsDir);
            Directory.CreateDirectory(DocsShotsDir);

            // Standard 45/45 ortho shots + dedicated detail shots for every C-3C gate
            CaptureOrtho(cam, "overview_22", new Vector3(5.0f, 0.8f, 0.2f), 45f, 45f, 36f, 13.5f);
            CaptureOrtho(cam, "medium_13", new Vector3(5.2f, 0.8f, 0.3f), 45f, 45f, 34f, 8.8f);
            CaptureOrtho(cam, "close_7_5", new Vector3(0.8f, 0.8f, 0.0f), 45f, 45f, 30f, 5.2f);
            CaptureOrtho(cam, "detail_pond_and_starter", new Vector3(1.1f, 0.8f, -0.7f), 50f, 35f, 28f, 3.2f);
            CaptureOrtho(cam, "detail_chunk_border", new Vector3(8.0f, 0.8f, 1.0f), 48f, 25f, 26f, 3.8f);
            CaptureOrtho(cam, "detail_junctions_4way", new Vector3(11.0f, 0.8f, -0.2f), 52f, 35f, 26f, 3.6f);
            CaptureOrtho(cam, "detail_hut_doors_grown", new Vector3(12.2f, 0.8f, 1.4f), 48f, -35f, 26f, 4.2f);
            // Low-angle perspective shot to verify terrain following (zero floating / zero clipping)
            CapturePerspective(cam, "detail_low_angle_terrain", new Vector3(8.8f, 0.65f, 0.5f), new Vector3(5.4f, 2.25f, -4.8f), 38f);

            cam.transform.position = origPos;
            cam.transform.rotation = origRot;
            cam.orthographicSize = origSize;
            cam.orthographic = origOrtho;
            cam.fieldOfView = origFov;

            AssetDatabase.Refresh();
            Debug.Log($"[AFK Island] Stage C-3C shots written to {BuildShotsDir} and {DocsShotsDir}");
        }

        private static void CaptureOrtho(
            Camera cam, string name, Vector3 target, float pitch, float yaw, float dist, float orthoSize)
        {
            cam.orthographic = true;
            cam.orthographicSize = orthoSize;
            PositionOrthoCamera(cam.transform, target, pitch, yaw, dist);
            RenderToPng(cam, name, 1280, 720);
        }

        private static void CapturePerspective(
            Camera cam, string name, Vector3 lookAt, Vector3 camPos, float fov)
        {
            cam.orthographic = false;
            cam.fieldOfView = fov;
            cam.transform.position = camPos;
            cam.transform.LookAt(lookAt, Vector3.up);
            RenderToPng(cam, name, 1280, 720);
        }

        private static void PositionOrthoCamera(Transform t, Vector3 target, float pitch, float yaw, float dist)
        {
            var rot = Quaternion.Euler(pitch, yaw, 0f);
            t.rotation = rot;
            t.position = target + rot * new Vector3(0f, 0f, -dist);
        }

        private static void RenderToPng(Camera cam, string name, int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            cam.targetTexture = rt;
            // 3 warm-up renders for clean shadow/URP state
            for (var i = 0; i < 3; i++) cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply(false, false);

            var png = tex.EncodeToPNG();
            File.WriteAllBytes(Path.Combine(BuildShotsDir, name + ".png"), png);
            File.WriteAllBytes(Path.Combine(DocsShotsDir, name + ".png"), png);

            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }

        // ------------------------------------------------------------------ Report & Asset helpers

        private static void WriteReport(
            SettlementPathTopology.TopologyPlan plan,
            SettlementPathMeshBuilder.MeshBuildReport mesh,
            bool planDet,
            bool meshDet,
            string b2Before,
            string b2After,
            string c2Before,
            string c2After)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(4096);
            sb.AppendLine("Stage C-3C — Terrain-Aware Two-Chunk Path Network Report");
            sb.AppendLine($"Scene: {ScenePath}");
            sb.AppendLine($"B-2 scene hash before={b2Before} after={b2After} unchanged={b2Before == b2After}");
            sb.AppendLine($"C-2 scene hash before={c2Before} after={c2After} unchanged={c2Before == c2After}");
            sb.AppendLine($"Deterministic rebuild: plan hash {plan.TopologyHash} identical={planDet}; mesh hash {mesh.MeshHash} identical={meshDet}");
            sb.AppendLine();
            sb.AppendLine($"Topology: nodes={plan.Nodes.Count}, edges={plan.Edges.Count}, junctions={plan.Junctions.Count}, components={plan.ConnectedComponents}, starterIssues={plan.StarterIssues.Count}");
            sb.AppendLine($"Chunks generated: {mesh.Chunks.Count} (Total Core: {mesh.TotalCoreVertices} verts / {mesh.TotalCoreTriangles} tris; Fringe: {mesh.TotalFringeVertices} verts / {mesh.TotalFringeTriangles} tris; JunctionPatches: {mesh.TotalJunctionPatches})");
            foreach (var kv in mesh.Chunks)
            {
                var d = kv.Value;
                sb.AppendLine($"  Chunk ({d.ChunkCoord.x},{d.ChunkCoord.y}): core={d.CoreVertices}v/{d.CoreTriangles}t fringe={d.FringeVertices}v/{d.FringeTriangles}t junctionPatches={d.JunctionPatches}");
            }

            sb.AppendLine();
            sb.AppendLine("Terrain & Geometry Verification:");
            sb.AppendLine(string.Format(ci, "  Core ground clearance (vertexY - SampleVisualHeight): min={0:F4} m, max={1:F4} m", mesh.MinCoreGroundClearance, mesh.MaxCoreGroundClearance));
            sb.AppendLine(string.Format(ci, "  Max path terrain slope: {0:F2} deg (violations > 28 deg: {1})", mesh.MaxSlopeDegrees, mesh.SlopeViolations));
            sb.AppendLine(string.Format(ci, "  Upward winding check: minUpwardNormalDot={0:F4}, downwardTriangles={1}", mesh.MinUpwardNormalDot, mesh.DownwardTriangles));
            sb.AppendLine(string.Format(ci, "  Chunk-border continuity: sharedPairs={0}, maxPosDelta={1:F5} m, maxHeightDelta={2:F5} m",
                mesh.BorderSharedPointPairs, mesh.MaxBorderPositionDelta, mesh.MaxBorderHeightDelta));

            sb.AppendLine();
            sb.AppendLine("Hut Branches:");
            for (var i = 0; i < plan.HutReports.Count; i++)
            {
                var r = plan.HutReports[i];
                sb.AppendLine(string.Format(ci,
                    "  {0}: status={1} origYaw={2:F1} effYaw={3:F1} len={4:F2}m dot={5:F2} clear={6:F2}m join={7} ({8}) violations=[{9}]",
                    r.HutId, r.Status, r.OriginalYawDegrees, r.EffectiveYawDegrees,
                    r.RouteLength, r.StartAlignmentDot, r.MinHutClearance,
                    r.JoinedEdgeId ?? "-", r.Variant ?? "-", string.Join(",", r.HardViolations)));
            }

            var text = sb.ToString();
            File.WriteAllText(BuildReportPath, text, Encoding.UTF8);
            File.WriteAllText(Path.Combine(DocsShotsDir, "terrain-report.txt"), text, Encoding.UTF8);
        }

        private static Mesh SaveMeshAsset(Mesh mesh, string assetPath)
        {
            // Recreate asset cleanly so Unity 6 / GPU Resident Drawer never retains stale vertex buffers
            if (AssetDatabase.LoadAssetAtPath<Mesh>(assetPath) != null)
            {
                AssetDatabase.DeleteAsset(assetPath);
            }

            AssetDatabase.CreateAsset(mesh, assetPath);
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        private static void CreateMeshChild(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = true;
        }

        private static Material EnsureLitMat(string name, Color color, float smoothness)
        {
            var path = $"{MaterialsFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit")
                             ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                             ?? Shader.Find("Standard");
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void ApplyMat(GameObject go, Material mat, bool castShadows)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            r.sharedMaterial = mat;
            r.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            r.receiveShadows = true;
        }

        private static void StripColliders(GameObject go)
        {
            var cols = go.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < cols.Length; i++) UnityEngine.Object.DestroyImmediate(cols[i]);
        }

        private static string FileHash(string path)
        {
            if (!File.Exists(path)) return "missing";
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(File.ReadAllBytes(path));
                var sb = new StringBuilder(16);
                for (var i = 0; i < 8; i++) sb.Append(bytes[i].ToString("X2"));
                return sb.ToString();
            }
        }

        private static int FloorDiv(int a, int b)
        {
            var q = a / b;
            var r = a % b;
            if (r != 0 && ((a < 0) ^ (b < 0))) q--;
            return q;
        }
    }
}
#endif
