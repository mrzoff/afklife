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
    /// Stage C-3D: isolated 2x2 four-chunk review scene & verification suite for the standalone
    /// SettlementPathNetworkRenderer + upgraded SettlementPathMeshBuilder (3-column terrain cross-sections,
    /// concave Bezier junction fillets, foldback protection, X+Z chunk borders, incremental dirty-chunk updates,
    /// zero Mesh/Material leaks).
    /// Zero production integration — does not modify HomeSettlementVisualizer, Save v8, ResidentPathGraph,
    /// or prior review scenes (B-2 / C-2 / C-3C).
    /// </summary>
    public static class CozyDioramaStageC3DBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3D.unity";
        private const string StageB2ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageB2.unity";
        private const string StageC2ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC2.unity";
        private const string StageC3CScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC3C.unity";
        private const string MeshFolder = "Assets/_Project/Art/CozyDiorama/StageC3D";
        private const string MaterialsFolder = "Assets/_Project/Art/CozyDiorama/Materials";
        private const string SettlementPrefabs = "Assets/_Project/Resources/AfkIsland/Settlement/";
        private const string NaturePrefabs = "Assets/Polyeler/EssentialNaturePack/Prefabs/";
        private const string BuildReportDir = "Builds/cozy-diorama";
        private const string BuildReportPath = "Builds/cozy-diorama/stage-c3d-report.txt";
        private const string BuildShotsDir = "Builds/cozy-diorama/stage-c3d-shots";
        private const string DocsShotsDir = "Docs/CozyDiorama/StageC3D-Shots";

        private static readonly Vector2 PondCenter = new Vector2(WaterSourceLayout.LocalPosition.x, WaterSourceLayout.LocalPosition.z);
        private const float PondRawRx = WaterSourceLayout.WaterHalfExtentX;
        private const float PondRawRz = WaterSourceLayout.WaterHalfExtentZ;
        private static readonly Vector2 HearthXZ = new Vector2(0.15f, -1.50f);

        private sealed class LifecycleCheckResult
        {
            public bool Pass1FullBuildOk;
            public bool Pass2NoOpReuseOk;
            public bool Pass3IncrementalSingleChunkOk;
            public bool Pass4ForceFullDeterministicOk;
            public bool Pass5ClearZeroLeaksOk;
            public bool SharedMaterialsOnlyOk;
            public int Pass1Rebuilt;
            public int Pass2Reused;
            public int Pass3Rebuilt;
            public int Pass3Reused;
            public int TotalCreated;
            public int TotalDestroyed;
            public int FinalLiveOwnedMeshes;
            public int FinalActiveMeshes;
            public string Pass3IncrementalHash;
            public string Pass4ForceFullHash;
        }

        // ------------------------------------------------------------------ menu

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3/Build Path Network Renderer Review Scene (C-3D)")]
        public static void BuildMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            try
            {
                var b2Before = FileHash(StageB2ScenePath);
                var c2Before = FileHash(StageC2ScenePath);
                var c3cBefore = FileHash(StageC3CScenePath);

                Directory.CreateDirectory(MeshFolder);
                Directory.CreateDirectory(MaterialsFolder);
                Directory.CreateDirectory(BuildReportDir);
                Directory.CreateDirectory(BuildShotsDir);
                Directory.CreateDirectory(DocsShotsDir);

                // 1. Open fresh empty scene FIRST so newly created meshes are never wiped by scene unload
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.76f, 0.85f, 0.92f);
                RenderSettings.ambientEquatorColor = new Color(0.66f, 0.72f, 0.65f);
                RenderSettings.ambientGroundColor = new Color(0.42f, 0.44f, 0.36f);

                // 2. Build 2x2 four-chunk organic terrain masks: (0,0), (1,0), (0,1), (1,1)
                var landMasks = BuildFourChunkLandMasks();
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

                // 3. Lighting & isolated Stage C-3D materials
                var lightGo = new GameObject("Sun_Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.25f;
                light.color = new Color(1.0f, 0.96f, 0.89f);
                light.shadows = LightShadows.Soft;
                lightGo.transform.rotation = Quaternion.Euler(48f, -38f, 0f);

                var matGrass = EnsureLitMat("Cozy_C3D_TerrainTop", new Color(0.49f, 0.63f, 0.33f), 0.08f);
                var matCliff = EnsureLitMat("Cozy_C3D_TerrainSide", new Color(0.56f, 0.49f, 0.37f), 0.06f);
                var matWater = EnsureLitMat("Cozy_C3D_Ocean", new Color(0.32f, 0.61f, 0.69f), 0.25f);
                var matPond = EnsureLitMat("Cozy_C3D_Pond", new Color(0.24f, 0.56f, 0.72f), 0.35f);
                var matCore = EnsureLitMat("Cozy_C3D_PathCore", new Color(0.68f, 0.57f, 0.39f), 0.07f);
                var matFringe = EnsureLitMat("Cozy_C3D_PathFringe", new Color(0.57f, 0.57f, 0.37f), 0.05f);
                var matHearthStone = EnsureLitMat("Cozy_C3D_HearthStone", new Color(0.36f, 0.35f, 0.34f), 0.10f);
                var matHearthFire = EnsureLitMat("Cozy_C3D_HearthFire", new Color(0.96f, 0.46f, 0.14f), 0.20f);
                var matBorderX = EnsureLitMat("Cozy_C3D_BorderX", new Color(0.95f, 0.82f, 0.22f), 0.15f);
                var matBorderZ = EnsureLitMat("Cozy_C3D_BorderZ", new Color(0.24f, 0.85f, 0.92f), 0.15f);

                // Ocean
                var ocean = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ocean.name = "Ocean_Water";
                ocean.transform.position = new Vector3(8f, 0.18f, 8f);
                ocean.transform.localScale = new Vector3(16f, 1f, 16f);
                ApplyMat(ocean, matWater, castShadows: false);
                StripColliders(ocean);

                // 4. Build and save 4 real ChunkTerrainMesher chunks
                var worldRoot = new GameObject("World_StageC3D");
                var terrainRoot = new GameObject("TerrainChunks");
                terrainRoot.transform.SetParent(worldRoot.transform, false);

                Vector2Int[] coords =
                {
                    new Vector2Int(0, 0),
                    new Vector2Int(1, 0),
                    new Vector2Int(0, 1),
                    new Vector2Int(1, 1)
                };

                for (var i = 0; i < coords.Length; i++)
                {
                    var c = coords[i];
                    var terrainMesh = ChunkTerrainMesher.Build(c.x, c.y, 16f, SampleStage, seed: 200 + i);
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

                // 5. Exercise standalone SettlementPathNetworkRenderer (5-pass lifecycle, incremental dirty chunk & leak tests!)
                var meshCfg = new SettlementPathMeshBuilder.MeshBuildConfig
                {
                    ChunkSizeMeters = 16f,
                    CoreYOffset = 0.036f,
                    FringeYOffset = 0.022f,
                    FringeExtraWidth = 0.095f,
                    WidthVariationAmplitude = 0.055f,
                    JunctionRadiusFactor = 1.32f,
                    JunctionArcSteps = 5,
                    LocalToChunkOrigin = true,
                    LocalChunkYBase = GrowthWorldPlacement.VisualYBase
                };

                var baseInput = CreateC3DTopologyInput(IsInsideLand, IsBlockedByWater, includeExtraNEHut: false);
                var finalInput = CreateC3DTopologyInput(IsInsideLand, IsBlockedByWater, includeExtraNEHut: true);

                var planBase = SettlementPathTopology.Build(baseInput);
                var planFinal1 = SettlementPathTopology.Build(finalInput);
                var planFinal2 = SettlementPathTopology.Build(finalInput);
                var planDeterministic = planFinal1.TopologyHash == planFinal2.TopologyHash;

                var rendererGo = new GameObject("PathNetworkRenderer");
                rendererGo.transform.SetParent(worldRoot.transform, false);
                var pathRenderer = rendererGo.AddComponent<SettlementPathNetworkRenderer>();
                pathRenderer.ConfigureMaterials(matCore, matFringe);

                var lifecycle = RunRendererLifecycleSuite(pathRenderer, planBase, planFinal1, SampleHeight, meshCfg);

                // Final full build via the standalone renderer, then persist mesh assets for saved scene reload
                var finalBuildReport = pathRenderer.ApplyPlan(planFinal1, SampleHeight, meshCfg, forceFullRebuild: true);
                var finalStats = pathRenderer.GetStats();
                lifecycle.TotalCreated = finalStats.TotalMeshesCreated;
                lifecycle.TotalDestroyed = finalStats.TotalMeshesDestroyed;
                lifecycle.FinalLiveOwnedMeshes = finalStats.LiveOwnedMeshCount;
                lifecycle.FinalActiveMeshes = finalStats.ActiveMeshCount;
                lifecycle.SharedMaterialsOnlyOk = finalStats.UsesSharedMaterialsOnly;

                // Persist runtime meshes as assets & bind saved copies so reopening the .unity scene always renders cleanly
                PersistRendererMeshesForSceneReload(rendererGo.transform, finalBuildReport);

                // 6. Props: Pond, Hearth, Huts (using planner effective visual yaw), Rocks, Trees, X & Z Border Stakes
                var propsRoot = new GameObject("SettlementProps");
                propsRoot.transform.SetParent(worldRoot.transform, false);

                var pondY = SampleHeight(PondCenter.x, PondCenter.y);
                var pond = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pond.name = "WaterSource_Pond";
                pond.transform.SetParent(propsRoot.transform, false);
                pond.transform.position = new Vector3(PondCenter.x, pondY + 0.015f, PondCenter.y);
                pond.transform.localScale = new Vector3(PondRawRx * 2f, 0.04f, PondRawRz * 2f);
                ApplyMat(pond, matPond, castShadows: false);
                StripColliders(pond);

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

                var reportById = new Dictionary<string, SettlementPathTopology.HutBranchReport>(StringComparer.Ordinal);
                for (var i = 0; i < planFinal1.HutReports.Count; i++)
                {
                    reportById[planFinal1.HutReports[i].HutId] = planFinal1.HutReports[i];
                }

                var hutAPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SettlementPrefabs + "Hut_A.prefab");
                var hutBPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SettlementPrefabs + "Hut_B.prefab");

                for (var i = 0; i < finalInput.Huts.Count; i++)
                {
                    var h = finalInput.Huts[i];
                    var useB = h.Id.Contains("Hut_B") || h.Id.Contains("East") || h.Id.Contains("NE");
                    var prefab = useB ? hutBPrefab : hutAPrefab;
                    var effectiveYaw = reportById.TryGetValue(h.Id, out var hr) ? hr.EffectiveYawDegrees : h.YawDegrees;
                    var y = SampleHeight(h.Center.x, h.Center.y);

                    var inst = prefab != null
                        ? (GameObject)PrefabUtility.InstantiatePrefab(prefab, propsRoot.transform)
                        : GameObject.CreatePrimitive(PrimitiveType.Cube);
                    inst.transform.SetParent(propsRoot.transform, false);
                    inst.name = h.Id;
                    inst.transform.position = new Vector3(h.Center.x, y, h.Center.y);
                    inst.transform.rotation = Quaternion.Euler(0f, effectiveYaw, 0f);
                    inst.transform.localScale = Vector3.one * h.Scale;
                    StripColliders(inst);
                }

                var rockPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(NaturePrefabs + "Rock_A.prefab");
                for (var i = 0; i < finalInput.Obstacles.Count; i++)
                {
                    var ob = finalInput.Obstacles[i];
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

                PlacePerimeterTrees(propsRoot.transform, SampleHeight);

                // Yellow stakes along X = 8.0 m border; Cyan stakes along Z = 8.0 m border (always on pure grass)
                PlaceBorderMarker(propsRoot.transform, "BorderX_Stake_S", 8.0f, -2.2f, SampleHeight, matBorderX);
                PlaceBorderMarker(propsRoot.transform, "BorderX_Stake_Mid", 8.0f, 5.2f, SampleHeight, matBorderX);
                PlaceBorderMarker(propsRoot.transform, "BorderX_Stake_N", 8.0f, 12.6f, SampleHeight, matBorderX);

                PlaceBorderMarker(propsRoot.transform, "BorderZ_Stake_W", -0.6f, 8.0f, SampleHeight, matBorderZ);
                PlaceBorderMarker(propsRoot.transform, "BorderZ_Stake_Mid", 6.2f, 8.0f, SampleHeight, matBorderZ);
                PlaceBorderMarker(propsRoot.transform, "BorderZ_Stake_E", 14.2f, 8.0f, SampleHeight, matBorderZ);

                // Camera
                var camGo = new GameObject("StageC3D_Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 15.5f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.32f, 0.61f, 0.69f);
                cam.nearClipPlane = 0.5f;
                cam.farClipPlane = 140f;
                PositionOrthoCamera(camGo.transform, new Vector3(6.5f, 0.8f, 4.5f), 45f, 45f, 42f);

                // 7. Save scene & verify prior review scenes B-2 / C-2 / C-3C untouched
                AssetDatabase.SaveAssets();
                EditorSceneManager.SaveScene(scene, ScenePath);

                var b2After = FileHash(StageB2ScenePath);
                var c2After = FileHash(StageC2ScenePath);
                var c3cAfter = FileHash(StageC3CScenePath);

                WriteReport(planFinal1, finalBuildReport, planDeterministic, lifecycle,
                    b2Before, b2After, c2Before, c2After, c3cBefore, c3cAfter);

                // Reload saved scene and defer shot rendering by 2 ticks (avoids GPU Resident Drawer ghost meshes)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                EditorApplication.delayCall += () => EditorApplication.delayCall += RenderAllShots;

                Debug.Log($"[AFK Island] Stage C-3D review scene built: {ScenePath}. Deterministic={planDeterministic && lifecycle.Pass4ForceFullDeterministicOk}. Rendering shots...");
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] Stage C-3D build failed: " + e);
            }
        }

        [MenuItem("AFK Island/Cozy Diorama/Stage C-3/Render C-3D Review Shots")]
        public static void RenderShotsMenu()
        {
            if (SceneManager.GetActiveScene().path != ScenePath)
            {
                if (!File.Exists(ScenePath))
                {
                    Debug.LogError("[AFK Island] C-3D scene does not exist yet: " + ScenePath);
                    return;
                }

                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            EditorApplication.delayCall += () => EditorApplication.delayCall += RenderAllShots;
        }

        // ------------------------------------------------------------------ Lifecycle & leak verification

        private static LifecycleCheckResult RunRendererLifecycleSuite(
            SettlementPathNetworkRenderer renderer,
            SettlementPathTopology.TopologyPlan planBase,
            SettlementPathTopology.TopologyPlan planFinal,
            Func<float, float, float> sampleHeight,
            SettlementPathMeshBuilder.MeshBuildConfig cfg)
        {
            var r = new LifecycleCheckResult();

            // Pass 1: initial full build (all 4 chunks)
            renderer.ApplyPlan(planBase, sampleHeight, cfg);
            var s1 = renderer.GetStats();
            r.Pass1Rebuilt = s1.LastRebuiltChunkCount;
            r.Pass1FullBuildOk = s1.ActiveChunkCount == 4 && s1.LastRebuiltChunkCount == 4 && s1.LastReusedChunkCount == 0
                                 && s1.LiveOwnedMeshCount == s1.ActiveMeshCount;

            // Pass 2: identical plan -> 0 rebuilt, 4 reused, 0 new mesh allocations
            var createdBeforeP2 = s1.TotalMeshesCreated;
            renderer.ApplyPlan(planBase, sampleHeight, cfg);
            var s2 = renderer.GetStats();
            r.Pass2Reused = s2.LastReusedChunkCount;
            r.Pass2NoOpReuseOk = s2.LastRebuiltChunkCount == 0 && s2.LastReusedChunkCount == 4
                                 && s2.TotalMeshesCreated == createdBeforeP2;

            // Pass 3: incremental mutation — adds a new hut in chunk (1,1) ONLY!
            // Expect ONLY chunk (1,1) rebuilt (rebuilt=1) and the other 3 chunks reused (reused=3)!
            renderer.ApplyPlan(planFinal, sampleHeight, cfg);
            var s3 = renderer.GetStats();
            r.Pass3Rebuilt = s3.LastRebuiltChunkCount;
            r.Pass3Reused = s3.LastReusedChunkCount;
            r.Pass3IncrementalSingleChunkOk = s3.LastRebuiltChunkCount == 1 && s3.LastReusedChunkCount == 3
                                              && s3.LiveOwnedMeshCount == s3.ActiveMeshCount;
            r.Pass3IncrementalHash = HashActiveRendererMeshes(renderer);

            // Pass 4: forced full rebuild on planFinal -> must produce bit-identical mesh hash to incremental result!
            renderer.ApplyPlan(planFinal, sampleHeight, cfg, forceFullRebuild: true);
            var s4 = renderer.GetStats();
            r.Pass4ForceFullHash = HashActiveRendererMeshes(renderer);
            r.Pass4ForceFullDeterministicOk = r.Pass3IncrementalHash == r.Pass4ForceFullHash
                                              && s4.LiveOwnedMeshCount == s4.ActiveMeshCount;

            // Pass 5: Clear() -> must destroy 100% of owned meshes (zero leaks)
            renderer.Clear();
            var s5 = renderer.GetStats();
            r.Pass5ClearZeroLeaksOk = s5.ActiveChunkCount == 0 && s5.ActiveMeshCount == 0
                                      && s5.LiveOwnedMeshCount == 0
                                      && s5.TotalMeshesCreated == s5.TotalMeshesDestroyed;

            return r;
        }

        private static string HashActiveRendererMeshes(SettlementPathNetworkRenderer renderer)
        {
            var sb = new StringBuilder(1024);
            var ci = CultureInfo.InvariantCulture;
            Vector2Int[] coords = { new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(0, 1), new Vector2Int(1, 1) };
            for (var i = 0; i < coords.Length; i++)
            {
                if (!renderer.TryGetChunkMeshes(coords[i], out var core, out var fringe)) continue;
                sb.AppendFormat(ci, "C|{0},{1}|{2}|{3}|{4}|{5}\n",
                    coords[i].x, coords[i].y,
                    core != null ? core.vertexCount : 0,
                    core != null ? core.triangles.Length / 3 : 0,
                    fringe != null ? fringe.vertexCount : 0,
                    fringe != null ? fringe.triangles.Length / 3 : 0);
                if (core != null)
                {
                    var verts = core.vertices;
                    var stride = Mathf.Max(1, verts.Length / 64);
                    for (var v = 0; v < verts.Length; v += stride)
                    {
                        sb.AppendFormat(ci, "V|{0:F3},{1:F3},{2:F3}\n", verts[v].x, verts[v].y, verts[v].z);
                    }
                }
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(16);
                for (var i = 0; i < 8; i++) hex.Append(bytes[i].ToString("X2"));
                return hex.ToString();
            }
        }

        private static void PersistRendererMeshesForSceneReload(
            Transform rendererTransform,
            SettlementPathMeshBuilder.MeshBuildReport report)
        {
            foreach (var kv in report.Chunks)
            {
                var ck = kv.Key;
                var data = kv.Value;
                var chunkChild = rendererTransform.Find($"PathChunk_{ck.x}_{ck.y}");
                if (chunkChild == null) continue;

                var coreCopy = UnityEngine.Object.Instantiate(data.CoreMesh);
                coreCopy.name = data.CoreMesh.name;
                var fringeCopy = UnityEngine.Object.Instantiate(data.FringeMesh);
                fringeCopy.name = data.FringeMesh.name;

                var coreAsset = SaveMeshAsset(coreCopy, $"{MeshFolder}/PathCore_{ck.x}_{ck.y}.asset");
                var fringeAsset = SaveMeshAsset(fringeCopy, $"{MeshFolder}/PathFringe_{ck.x}_{ck.y}.asset");

                var coreMf = chunkChild.Find("Core")?.GetComponent<MeshFilter>();
                var fringeMf = chunkChild.Find("Fringe")?.GetComponent<MeshFilter>();
                if (coreMf != null) coreMf.sharedMesh = coreAsset;
                if (fringeMf != null) fringeMf.sharedMesh = fringeAsset;
            }
        }

        // ------------------------------------------------------------------ 2x2 four-chunk fixture setup

        private static Dictionary<Vector2Int, byte[]> BuildFourChunkLandMasks()
        {
            var dict = new Dictionary<Vector2Int, byte[]>
            {
                [new Vector2Int(0, 0)] = new byte[ChunkMaskCodec.CellCount],
                [new Vector2Int(1, 0)] = new byte[ChunkMaskCodec.CellCount],
                [new Vector2Int(0, 1)] = new byte[ChunkMaskCodec.CellCount],
                [new Vector2Int(1, 1)] = new byte[ChunkMaskCodec.CellCount]
            };

            for (var cz = 0; cz <= 1; cz++)
            {
                for (var cx = 0; cx <= 1; cx++)
                {
                    var mask = dict[new Vector2Int(cx, cz)];
                    for (var lz = 0; lz < ChunkMaskCodec.GridSize; lz++)
                    {
                        for (var lx = 0; lx < ChunkMaskCodec.GridSize; lx++)
                        {
                            var wx = cx * 16 + lx;
                            var wz = cz * 16 + lz;
                            var center = SettlementPathTopology.CellCenterXZ(wx, wz);

                            // Organic 2x2 island centered around (6.8, 5.2), rx=13.4, rz=12.4
                            var dx = (center.x - 6.8f) / 13.4f;
                            var dz = (center.y - 5.2f) / 12.4f;
                            var ang = Mathf.Atan2(dz, dx);
                            var wave = 1.0f + 0.055f * Mathf.Sin(3f * ang + 0.5f) + 0.04f * Mathf.Cos(5f * ang - 0.6f);
                            var r = Mathf.Sqrt(dx * dx + dz * dz) / wave;

                            byte stage;
                            if (r > 1.02f) stage = (byte)CellStage.Void;
                            else if (r > 0.94f) stage = (byte)CellStage.Roots;
                            else if (r > 0.87f) stage = (byte)CellStage.Soil;
                            else
                            {
                                // Pronounced rolling ridge & gentle gully across chunk borders to stress terrain hugging
                                var hillWave = Mathf.Sin(center.x * 0.38f + 0.4f) * Mathf.Cos(center.y * 0.36f - 0.2f);
                                stage = hillWave > 0.12f ? (byte)CellStage.Vegetation : (byte)CellStage.Moss;
                            }

                            mask[ChunkMaskCodec.CellIndex(lx, lz)] = stage;
                        }
                    }
                }
            }

            return dict;
        }

        private static SettlementPathTopology.TopologyInput CreateC3DTopologyInput(
            Func<float, float, bool> isInsideLand,
            Func<float, float, bool> isBlockedByWater,
            bool includeExtraNEHut)
        {
            var cfg = new SettlementPathTopology.TopologyConfig
            {
                SampleStepMeters = 0.12f,
                AllowVisualDoorCorrection = true
            };

            // Starter paths (production Starter_Main + pond-safe Starter_Cross) + a curved hillside Y-fork starter in Chunk (0,1)
            // to explicitly stress non-orthogonal angled Y-junctions & sweeping S-curves across Z=8.0 m!
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
                },
                // Curved hillside lane climbing from Chunk (0,0) across Z = 8.0 m into Chunk (0,1) to form a Y-fork at (2.8, 10.6)
                new SettlementPathTopology.StarterPolylineInput
                {
                    Id = "Hillside_Spine",
                    HalfWidth = 0.26f,
                    SmoothCatmullRom = true,
                    ControlPoints = new List<Vector2>
                    {
                        new Vector2(0.00f, 2.30f),
                        new Vector2(0.55f, 4.60f),
                        new Vector2(1.65f, 6.80f),
                        new Vector2(2.45f, 8.80f),
                        new Vector2(2.80f, 10.60f)
                    }
                },
                // Y-fork NW arm (~125 deg angle from Spine)
                new SettlementPathTopology.StarterPolylineInput
                {
                    Id = "Hillside_YFork_NW",
                    HalfWidth = 0.24f,
                    SmoothCatmullRom = true,
                    ControlPoints = new List<Vector2>
                    {
                        new Vector2(2.80f, 10.60f),
                        new Vector2(1.45f, 11.75f),
                        new Vector2(0.15f, 12.60f)
                    }
                },
                // Y-fork NE arm curving across X = 8.0 m into Chunk (1,1) to join the northern trunk!
                new SettlementPathTopology.StarterPolylineInput
                {
                    Id = "Hillside_YFork_NE",
                    HalfWidth = 0.24f,
                    SmoothCatmullRom = true,
                    ControlPoints = new List<Vector2>
                    {
                        new Vector2(2.80f, 10.60f),
                        new Vector2(4.90f, 11.15f),
                        new Vector2(7.10f, 10.75f),
                        new Vector2(9.20f, 10.50f),
                        new Vector2(10.50f, 10.50f)
                    }
                }
            };

            // Mask road cells spanning ALL 4 chunks across BOTH x = 8.0 m (wx=15->16) AND z = 8.0 m (wz=15->16)
            var roadCells = new List<SettlementPathTopology.MaskCellInput>();
            void AddRoad(int wx, int wz, byte stage = 2) =>
                roadCells.Add(new SettlementPathTopology.MaskCellInput { WorldCellX = wx, WorldCellZ = wz, Stage = stage });

            // East-west trunk crossing X-border x = 8.0 m (wx=8..21, wz=8)
            for (var wx = 8; wx <= 21; wx++) AddRoad(wx, 8, wx == 13 ? (byte)3 : (byte)2);

            // North-south trunk at wx=18 crossing Z-border z = 8.0 m (wz=5..19):
            // creates 4-way crossroads at (18,8), T-junction at (18,7), AND near-border T-junction at (18,16) (z=8.5m!)
            for (var wz = 5; wz <= 19; wz++)
            {
                if (wz == 8) continue;
                AddRoad(18, wz);
            }

            // T-junction at (18, 7) + 2x2 block / S-curve stress in Chunk (1,0)
            AddRoad(19, 7); AddRoad(20, 7);
            AddRoad(19, 6); AddRoad(20, 6);

            // Near-border T-junction right above Z=8.0m in Chunk (1,1): (18,16) -> east (19..21, 16) -> north (21, 17..19)
            AddRoad(19, 16); AddRoad(20, 16); AddRoad(21, 16);
            AddRoad(21, 17); AddRoad(21, 18); AddRoad(21, 19);

            // Pond-adjacent SW lane wrapping west of Hearth in Chunk (0,0)
            AddRoad(6, 7); AddRoad(6, 6); AddRoad(6, 5); AddRoad(7, 5); AddRoad(7, 4); AddRoad(8, 4);

            // Huts across all 4 chunks
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
                    Id = "Hut_PondSW",
                    Center = SettlementPathTopology.CellCenterXZ(8, 3, cfg),
                    YawDegrees = 0f,
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(1.35f * 0.5f, 1.15f * 0.5f),
                    DoorLocalZ = 1.15f * 0.52f
                },
                new SettlementPathTopology.HutInput
                {
                    Id = "Hut_BorderX_N",
                    Center = SettlementPathTopology.CellCenterXZ(15, 11, cfg),
                    YawDegrees = 150f,
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(1.35f * 0.5f, 1.15f * 0.5f),
                    DoorLocalZ = 1.15f * 0.52f
                },
                new SettlementPathTopology.HutInput
                {
                    Id = "Hut_East_Reversed",
                    Center = SettlementPathTopology.CellCenterXZ(22, 8, cfg),
                    YawDegrees = 90f, // visual-only door correction demo
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(1.55f * 0.5f, 1.25f * 0.5f),
                    DoorLocalZ = 1.25f * 0.52f
                },
                new SettlementPathTopology.HutInput
                {
                    Id = "Hut_HillNW",
                    Center = new Vector2(-0.55f, 13.65f),
                    YawDegrees = 145f,
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(1.35f * 0.5f, 1.15f * 0.5f),
                    DoorLocalZ = 1.15f * 0.52f
                }
            };

            // Added only in Pass 3 to verify selective incremental single-chunk rebuild of Chunk (1,1)!
            if (includeExtraNEHut)
            {
                huts.Add(new SettlementPathTopology.HutInput
                {
                    Id = "Hut_Incremental_NE",
                    Center = SettlementPathTopology.CellCenterXZ(22, 19, cfg),
                    YawDegrees = -90f,
                    Scale = SettlementCellQuery.HutVisualUniformScale,
                    FootprintHalfExtents = new Vector2(1.55f * 0.5f, 1.25f * 0.5f),
                    DoorLocalZ = 1.25f * 0.52f
                });
            }

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
                    new SettlementPathTopology.CircleObstacle { Id = "Rock_Mid", Center = new Vector2(5.4f, -1.6f), Radius = 0.36f },
                    new SettlementPathTopology.CircleObstacle { Id = "Rock_NorthHill", Center = new Vector2(5.1f, 8.9f), Radius = 0.36f }
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
                ("CherryBlossom.prefab", -3.4f, 4.2f, 0.42f, 80f),
                ("SawtoothOak.prefab", -4.6f, -1.5f, 0.38f, 145f),
                ("Pine.prefab", 3.5f, -4.4f, 0.35f, 310f),
                ("SawtoothOak.prefab", 14.0f, -3.5f, 0.38f, 260f),
                ("PlaneTree.prefab", -2.2f, 9.8f, 0.40f, 110f),
                ("JapaneseCedar.prefab", 3.8f, 14.8f, 0.35f, 195f),
                ("CherryBlossom.prefab", 10.6f, 14.5f, 0.40f, 45f),
                ("Pine.prefab", 16.4f, 6.2f, 0.36f, 220f),
                ("SawtoothOak.prefab", 16.2f, 12.2f, 0.36f, 15f)
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
            go.transform.position = new Vector3(x, sampleHeight(x, z) + 0.30f, z);
            go.transform.localScale = new Vector3(0.09f, 0.30f, 0.09f);
            ApplyMat(go, mat, castShadows: true);
            StripColliders(go);
        }

        // ------------------------------------------------------------------ Shot rendering

        private static void RenderAllShots()
        {
            var camGo = GameObject.Find("StageC3D_Camera");
            if (camGo == null)
            {
                Debug.LogError("[AFK Island] StageC3D_Camera not found in active scene.");
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

            // 1-2: 4-chunk overview & medium iso
            CaptureOrtho(cam, "overview_4chunks", new Vector3(6.8f, 0.8f, 5.2f), 45f, 45f, 44f, 16.5f);
            CaptureOrtho(cam, "medium_4chunks", new Vector3(7.0f, 0.8f, 5.0f), 45f, 45f, 42f, 11.8f);
            // 3: Close-up on 4-way crossroads + T-junction concave Bezier fillets & flat T-back
            CaptureOrtho(cam, "detail_4way_and_t_fillets", new Vector3(11.3f, 0.8f, -0.1f), 55f, 30f, 26f, 2.9f);
            // 4: Close-up on angled Y-fork & sweeping curves in Chunk (0,1)
            CaptureOrtho(cam, "detail_yfork_and_curves", new Vector3(2.8f, 0.8f, 10.6f), 52f, 25f, 26f, 3.8f);
            // 5: Close-up across 4-chunk corner (X=8.0m & Z=8.0m) + near-border T-junction in Chunk (1,1)
            CaptureOrtho(cam, "detail_xz_chunk_borders", new Vector3(9.8f, 0.8f, 8.8f), 50f, 25f, 28f, 5.2f);
            // 6: Starter settlement + pond + hearth + SW lane
            CaptureOrtho(cam, "detail_starter_and_pond", new Vector3(0.9f, 0.8f, -0.7f), 50f, 35f, 28f, 3.4f);
            // 7: Low-angle perspective across rolling terrain & X/Z borders
            CapturePerspective(cam, "detail_low_angle_ridge", new Vector3(6.5f, 0.7f, 8.5f), new Vector3(2.2f, 2.25f, 3.0f), 38f);
            // 8: Low-angle close-up into 4-way crossroads & T-junction (verifies zero gaps/clipping/T-cracks)
            CapturePerspective(cam, "detail_low_angle_junctions", new Vector3(11.2f, 0.65f, 0.4f), new Vector3(6.2f, 2.05f, -3.4f), 34f);

            cam.transform.position = origPos;
            cam.transform.rotation = origRot;
            cam.orthographicSize = origSize;
            cam.orthographic = origOrtho;
            cam.fieldOfView = origFov;

            AssetDatabase.Refresh();
            Debug.Log($"[AFK Island] Stage C-3D shots written to {BuildShotsDir} and {DocsShotsDir}");
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
            LifecycleCheckResult lc,
            string b2Before, string b2After,
            string c2Before, string c2After,
            string c3cBefore, string c3cAfter)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(4096);
            sb.AppendLine("Stage C-3D — Standalone Path Network Renderer Verification Report");
            sb.AppendLine($"Scene: {ScenePath}");
            sb.AppendLine($"B-2 scene hash before={b2Before} after={b2After} unchanged={b2Before == b2After}");
            sb.AppendLine($"C-2 scene hash before={c2Before} after={c2After} unchanged={c2Before == c2After}");
            sb.AppendLine($"C-3C scene hash before={c3cBefore} after={c3cAfter} unchanged={c3cBefore == c3cAfter}");
            sb.AppendLine($"Deterministic rebuild: planHash={plan.TopologyHash} identical={planDet}; meshHash={mesh.MeshHash}");
            sb.AppendLine();
            sb.AppendLine("Standalone Renderer Lifecycle & Leak Checks:");
            sb.AppendLine($"  Pass1 Full Build (4 chunks): ok={lc.Pass1FullBuildOk} (rebuilt={lc.Pass1Rebuilt})");
            sb.AppendLine($"  Pass2 Idempotent No-Op: ok={lc.Pass2NoOpReuseOk} (reused={lc.Pass2Reused})");
            sb.AppendLine($"  Pass3 Incremental Single-Chunk (1,1) Update: ok={lc.Pass3IncrementalSingleChunkOk} (rebuilt={lc.Pass3Rebuilt}, reused={lc.Pass3Reused})");
            sb.AppendLine($"  Pass4 Force Full == Incremental Hash: ok={lc.Pass4ForceFullDeterministicOk} (inc={lc.Pass3IncrementalHash}, full={lc.Pass4ForceFullHash})");
            sb.AppendLine($"  Pass5 Clear() Zero Mesh Leaks: ok={lc.Pass5ClearZeroLeaksOk}");
            sb.AppendLine($"  Final State: activeMeshes={lc.FinalActiveMeshes}, liveOwnedMeshes={lc.FinalLiveOwnedMeshes}, totalCreated={lc.TotalCreated}, totalDestroyed={lc.TotalDestroyed}, sharedMaterialsOnly={lc.SharedMaterialsOnlyOk}");
            sb.AppendLine();
            sb.AppendLine($"Topology: nodes={plan.Nodes.Count}, edges={plan.Edges.Count}, junctions={plan.Junctions.Count}, components={plan.ConnectedComponents}, starterIssues={plan.StarterIssues.Count}");
            sb.AppendLine($"Chunks generated: {mesh.Chunks.Count} (Total Core: {mesh.TotalCoreVertices} verts / {mesh.TotalCoreTriangles} tris; Fringe: {mesh.TotalFringeVertices} verts / {mesh.TotalFringeTriangles} tris; JunctionPatches: {mesh.TotalJunctionPatches})");

            var keys = new List<Vector2Int>(mesh.Chunks.Keys);
            keys.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            for (var i = 0; i < keys.Count; i++)
            {
                var d = mesh.Chunks[keys[i]];
                sb.AppendLine($"  Chunk ({d.ChunkCoord.x},{d.ChunkCoord.y}): core={d.CoreVertices}v/{d.CoreTriangles}t fringe={d.FringeVertices}v/{d.FringeTriangles}t junctionPatches={d.JunctionPatches} hash={d.ChunkHash}");
            }

            sb.AppendLine();
            sb.AppendLine("Terrain & Geometry Verification:");
            sb.AppendLine(string.Format(ci, "  Core ground clearance (vertexY - SampleVisualHeight): min={0:F4} m, max={1:F4} m",
                mesh.MinCoreGroundClearance, mesh.MaxCoreGroundClearance));
            sb.AppendLine(string.Format(ci, "  Core ribbon width control: min={0:F4} m, max={1:F4} m",
                mesh.MinRibbonWidthMeters, mesh.MaxRibbonWidthMeters));
            sb.AppendLine(string.Format(ci, "  Max path terrain slope: {0:F2} deg (violations > 28 deg: {1})",
                mesh.MaxSlopeDegrees, mesh.SlopeViolations));
            sb.AppendLine(string.Format(ci, "  Upward winding check: minUpwardNormalDot={0:F4}, downwardTriangles={1}",
                mesh.MinUpwardNormalDot, mesh.DownwardTriangles));
            sb.AppendLine(string.Format(ci, "  Foldback protection: remainingFoldbacks={0}, foldbacksPrevented={1}",
                mesh.FoldbackTriangles, mesh.FoldbacksPrevented));
            sb.AppendLine(string.Format(ci, "  Junction-to-ribbon exact attachment: verifiedPairs={0}, maxDelta={1:F5} m",
                mesh.JunctionAttachPairs, mesh.MaxJunctionAttachDelta));
            sb.AppendLine(string.Format(ci, "  Chunk-border continuity (X & Z): sharedPairs={0}, maxPosDelta={1:F5} m, maxHeightDelta={2:F5} m",
                mesh.BorderSharedPointPairs, mesh.MaxBorderPositionDelta, mesh.MaxBorderHeightDelta));

            sb.AppendLine();
            sb.AppendLine("Hut Branches:");
            for (var i = 0; i < plan.HutReports.Count; i++)
            {
                var hr = plan.HutReports[i];
                sb.AppendLine(string.Format(ci,
                    "  {0}: status={1} origYaw={2:F1} effYaw={3:F1} len={4:F2}m dot={5:F2} clear={6:F2}m join={7} ({8}) violations=[{9}]",
                    hr.HutId, hr.Status, hr.OriginalYawDegrees, hr.EffectiveYawDegrees,
                    hr.RouteLength, hr.StartAlignmentDot, hr.MinHutClearance,
                    hr.JoinedEdgeId ?? "-", hr.Variant ?? "-", string.Join(",", hr.HardViolations)));
            }

            var text = sb.ToString();
            File.WriteAllText(BuildReportPath, text, Encoding.UTF8);
            File.WriteAllText(Path.Combine(DocsShotsDir, "renderer-report.txt"), text, Encoding.UTF8);
        }

        private static Mesh SaveMeshAsset(Mesh mesh, string assetPath)
        {
            if (AssetDatabase.LoadAssetAtPath<Mesh>(assetPath) != null)
            {
                AssetDatabase.DeleteAsset(assetPath);
            }

            AssetDatabase.CreateAsset(mesh, assetPath);
            EditorUtility.SetDirty(mesh);
            return mesh;
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
            if (r != 0 && ((r < 0) != (b < 0))) q--;
            return q;
        }
    }
}
#endif
