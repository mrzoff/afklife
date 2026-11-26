#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Isolated Stage C-2B settlement path-network review. Copies the approved B-2 composition
    /// (huts and grove stay where they are) into a new scene, adds the authored starter hearth and
    /// starter path network (IslandPrototypeBuilder coordinates), two test huts, and a deterministic
    /// network of door approaches that join the nearest existing network point. Prototype only:
    /// production settlement, resident navigation and save data are not referenced.
    /// </summary>
    public static class CozyDioramaStageC2Builder
    {
        public const string SourceScene = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageB2.unity";
        public const string ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC2.unity";
        private const string SettlementPrefabs = "Assets/_Project/Resources/AfkIsland/Settlement/";
        private const string MeshFolder = "Assets/_Project/Art/CozyDiorama/StageC2";
        private const string ReportDir = "Builds/cozy-diorama";
        private const string ShotsDir = "Builds/cozy-diorama/stage-c2-shots";
        private const string DocsShotsDir = "Docs/CozyDiorama/StageC2-Shots";

        // Starter coordinates from IslandPrototypeBuilder.CreateStarterSettlement (x/z only).
        // Path_Main: centre (0, 0.2), size 1.1 x 4.2  -> spine along z from -1.9 to 2.3.
        // Path_Cross: centre (0.9, -0.6), size 2.4 x 0.7 -> along x from -0.3 to 2.1.
        // Hearth: (0.15, -1.5), 0.9 x 0.9 stone ring with fire.
        private static readonly Vector3 HearthPos = new Vector3(0.15f, 0f, -1.5f);
        private const float HearthRadius = 0.60f;

        private const float MainHalfWidth = 0.30f;
        private const float CrossHalfWidth = 0.25f;
        private const float BranchHalfWidth = 0.22f;
        private const float FringeExtra = 0.07f;
        private const float Step = 0.05f;

        private sealed class HutInfo
        {
            public string Id;
            public Transform Root;
            public Vector3 Center;
            public Vector3 DoorWorld;
            public Vector3 Forward;
            public float Scale;
            public Vector2 Min;
            public Vector2 Max;
            public float Radius;
        }

        private sealed class Obstacle
        {
            public string Name;
            public Vector3 Pos;
            public float Radius;
        }

        private sealed class Poly
        {
            public string Id;
            public List<Vector3> Pts = new List<Vector3>();
            public float HalfWidth;
            public float Y;
            public bool TaperStart;
            public bool TaperEnd;
            public bool DoorStart;
            public bool JunctionEnd;
        }

        private sealed class BranchInfo
        {
            public string HutId;
            public Vector3 Anchor;
            public Vector3 Junction;
            public string JunctionOn;
            public string Variant;
            public int CandidateRank;
            public float Length;
            public float MinHutClear;
            public float StartDot;
            public List<string> Violations = new List<string>();
        }

        private sealed class Plan
        {
            public List<Poly> Polys = new List<Poly>();
            public List<BranchInfo> Branches = new List<BranchInfo>();
        }

        private struct Cand
        {
            public Vector3 Point;
            public Poly Poly;
            public int Index;
            public float Dist;
        }

        // ------------------------------------------------------------------ menu

        [MenuItem("AFK Island/Cozy Diorama/Stage C-2/Build Path Network Review Scene")]
        public static void BuildMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!File.Exists(SourceScene))
            {
                Debug.LogError("[AFK Island] Stage B-2 source scene missing: " + SourceScene);
                return;
            }

            try
            {
                var sourceHashBefore = FileHash(SourceScene);
                var scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
                if (GameObject.Find("Settlement") == null)
                    throw new InvalidOperationException("Settlement root not found in B-2 scene.");

                // Save as a new scene first; everything after this belongs only to the C-2 scene.
                EditorSceneManager.SaveScene(scene, ScenePath);
                scene = SceneManager.GetActiveScene();
                var settlement = GameObject.Find("Settlement").transform;

                RemoveIfPresent(settlement, "Path_Ribbon");
                RemoveIfPresent(settlement, "Path_Network");
                RemoveIfPresent(settlement, "Hearth");
                RemoveIfPresent(settlement, "Hut_Test_03");
                RemoveIfPresent(settlement, "Hut_Test_04");

                AddHearth(settlement);
                // Test expansion huts: 03 branches from an existing branch, 04 branches from Starter_Cross.
                // Finding of the first run: a hut at (2.9, 0.2) with yaw 100 (door facing away from the network) has no
                // clean branch (best route 5.26 m with hut/obstacle overlaps) -> production must orient doors towards the network.
                AddTestHut(settlement, "Hut_Test_03", "Hut_A", new Vector3(-0.9f, 0f, -3.2f), 10f);
                AddTestHut(settlement, "Hut_Test_04", "Hut_B", new Vector3(2.3f, 0f, -2.5f), 10f);

                var huts = CollectHuts(settlement);
                if (huts.Count < 2) throw new InvalidOperationException("At least two huts with Door children are required.");
                huts.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                var obstacles = CollectObstacles(settlement);

                var plan = ComputePlan(huts, obstacles);
                var planAgain = ComputePlan(huts, obstacles);
                var planHash = PlanHash(plan);
                var planHashAgain = PlanHash(planAgain);

                var group = new GameObject("Path_Network");
                group.transform.SetParent(settlement, false);
                var meshHash = BuildSceneObjects(group.transform, plan, huts);
                var meshHashAgain = MeshHash(plan);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                var sourceHashAfter = FileHash(SourceScene);
                var report = BuildReport(plan, huts, planHash == planHashAgain, planHash, meshHash == meshHashAgain,
                    meshHash, sourceHashBefore, sourceHashAfter);
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "..", ReportDir));
                File.WriteAllText(Path.Combine(Application.dataPath, "..", ReportDir, "stage-c2-network-report.txt"), report);

                Directory.CreateDirectory(Path.Combine(Application.dataPath, "..", DocsShotsDir));
                File.WriteAllText(Path.Combine(Application.dataPath, "..", DocsShotsDir, "network-report.txt"), report);

                // Reload the saved scene and render shots on later editor ticks: rendering in the same frame as the
                // build shows ghosts of renderers removed in this session (GPU Resident Drawer cache).
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                EditorApplication.delayCall += () => EditorApplication.delayCall += RenderAllShots;

                Debug.Log("[AFK Island] Stage C-2 review scene built: " + ScenePath + "\n" + report);
            }
            catch (Exception ex)
            {
                Debug.LogError("[AFK Island] Stage C-2 build failed: " + ex);
                throw;
            }
        }

        [MenuItem("AFK Island/Cozy Diorama/Stage C-2/Render Review Shots")]
        public static void RenderReviewShotsMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!File.Exists(ScenePath))
            {
                Debug.LogError("[AFK Island] Build the Stage C-2 review scene first.");
                return;
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorApplication.delayCall += () => EditorApplication.delayCall += RenderAllShots;
        }

        // ------------------------------------------------------------------ plan (pure geometry)

        private static Plan ComputePlan(List<HutInfo> huts, List<Obstacle> obstacles)
        {
            var plan = new Plan();

            var mainCtrl = new List<Vector3>
            {
                new Vector3(0.15f, 0f, -1.50f), new Vector3(0.00f, 0f, -0.60f), new Vector3(0.00f, 0f, 0.20f),
                new Vector3(0.00f, 0f, 1.30f), new Vector3(0.00f, 0f, 2.30f)
            };
            plan.Polys.Add(new Poly
            {
                Id = "Starter_Main", Pts = Resample(CatmullRom(mainCtrl, 10), Step),
                HalfWidth = MainHalfWidth, Y = 0.020f, TaperEnd = true
            });

            var crossCtrl = new List<Vector3>
            {
                new Vector3(-0.3f, 0f, -0.6f), new Vector3(0.9f, 0f, -0.6f), new Vector3(2.1f, 0f, -0.6f)
            };
            plan.Polys.Add(new Poly
            {
                Id = "Starter_Cross", Pts = Resample(CatmullRom(crossCtrl, 12), Step),
                HalfWidth = CrossHalfWidth, Y = 0.023f, TaperStart = true, TaperEnd = true
            });

            var junctions = new List<Vector3>();
            for (var h = 0; h < huts.Count; h++)
            {
                var hut = huts[h];
                var anchor = Flat(hut.DoorWorld) + hut.Forward * 0.06f;
                var candidates = FindCandidates(plan, anchor, junctions);

                List<Vector3> bestRoute = null;
                List<string> bestViolations = null;
                float bestClear = 0f;
                var bestVariant = "none";
                var bestRank = -1;
                Cand bestCand = default;
                var bestCount = int.MaxValue;
                var found = false;

                for (var c = 0; c < candidates.Count && !found; c++)
                {
                    var cand = candidates[c];
                    var tangent = TangentAt(cand.Poly.Pts, cand.Index);
                    var n = new Vector3(-tangent.z, 0f, tangent.x);
                    if (Vector3.Dot(n, anchor - cand.Point) < 0f) n = -n;

                    for (var variant = 0; variant < 6; variant++)
                    {
                        var route = BuildRoute(variant, anchor, hut, cand.Point, n);
                        float clear;
                        int score;
                        var violations = Validate(route, hut, huts, obstacles, plan, BranchHalfWidth, out clear, out score);
                        if (score < bestCount)
                        {
                            bestCount = score;
                            bestRoute = route;
                            bestViolations = violations;
                            bestClear = clear;
                            bestVariant = VariantName(variant);
                            bestRank = c;
                            bestCand = cand;
                        }
                        if (violations.Count == 0) { found = true; break; }
                    }
                }

                if (bestRoute == null)
                    throw new InvalidOperationException("No route candidates for " + hut.Id);

                junctions.Add(bestCand.Point);
                var branch = new Poly
                {
                    Id = "Branch_" + hut.Id, Pts = bestRoute, HalfWidth = BranchHalfWidth,
                    Y = 0.026f + 0.002f * h, DoorStart = true, JunctionEnd = true
                };
                plan.Polys.Add(branch);

                var first = bestRoute.Count > 1 ? (bestRoute[Math.Min(6, bestRoute.Count - 1)] - bestRoute[0]) : Vector3.forward;
                first.y = 0f;
                plan.Branches.Add(new BranchInfo
                {
                    HutId = hut.Id, Anchor = anchor, Junction = bestCand.Point, JunctionOn = bestCand.Poly.Id,
                    Variant = bestVariant, CandidateRank = bestRank, Length = ArcLength(bestRoute),
                    MinHutClear = bestClear, StartDot = Vector3.Dot(first.normalized, hut.Forward),
                    Violations = bestViolations
                });
            }

            return plan;
        }

        private static string VariantName(int variant)
        {
            switch (variant)
            {
                case 0: return "bezier-soft";
                case 1: return "bezier-long";
                case 2: return "wrap-left";
                case 3: return "wrap-right";
                case 4: return "wrap-left-wide";
                default: return "wrap-right-wide";
            }
        }

        private static List<Cand> FindCandidates(Plan plan, Vector3 anchor, List<Vector3> junctions)
        {
            var result = new List<Cand>();
            for (var p = 0; p < plan.Polys.Count; p++)
            {
                var poly = plan.Polys[p];
                var used = new List<int>();
                for (var pick = 0; pick < 3; pick++)
                {
                    var best = -1;
                    var bestD = float.PositiveInfinity;
                    for (var i = 6; i < poly.Pts.Count - 6; i++)
                    {
                        var pt = poly.Pts[i];
                        if (D2(pt, HearthPos) < HearthRadius + 0.05f) continue;
                        var tooClose = false;
                        for (var j = 0; j < junctions.Count; j++)
                            if (D2(pt, junctions[j]) < 0.55f) { tooClose = true; break; }
                        if (tooClose) continue;
                        for (var u = 0; u < used.Count; u++)
                            if (Mathf.Abs(i - used[u]) < 16) { tooClose = true; break; }
                        if (tooClose) continue;
                        var d = D2(pt, anchor);
                        if (d < bestD) { bestD = d; best = i; }
                    }
                    if (best < 0) break;
                    used.Add(best);
                    result.Add(new Cand { Point = poly.Pts[best], Poly = poly, Index = best, Dist = bestD });
                }
            }

            result.Sort((a, b) =>
            {
                var c = a.Dist.CompareTo(b.Dist);
                return c != 0 ? c : string.CompareOrdinal(a.Poly.Id, b.Poly.Id);
            });
            if (result.Count > 8) result.RemoveRange(8, result.Count - 8);
            return result;
        }

        private static List<Vector3> BuildRoute(int variant, Vector3 anchor, HutInfo hut, Vector3 q, Vector3 n)
        {
            var end = q - n * 0.08f;
            List<Vector3> raw;
            if (variant < 2)
            {
                var dist = D2(anchor, q);
                var k = variant == 0 ? 0.40f : 0.70f;
                var dA = Mathf.Clamp(k * dist, 0.45f, 1.3f);
                var dQ = Mathf.Clamp(0.45f * dist, 0.30f, 1.0f);
                var p0 = anchor;
                var p1 = anchor + hut.Forward * dA;
                var p2 = q + n * dQ;
                var p3 = q;
                raw = new List<Vector3>();
                const int steps = 28;
                for (var i = 0; i <= steps; i++)
                {
                    var t = i / (float)steps;
                    var u = 1f - t;
                    raw.Add(u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3);
                }
                raw.Add(end);
            }
            else
            {
                var toQ = q - hut.Center;
                toQ.y = 0f;
                var dir = toQ.normalized;
                var perp = new Vector3(-dir.z, 0f, dir.x) * (variant % 2 == 0 ? 1f : -1f);
                var pad = variant >= 4 ? 0.95f : 0.45f;
                var wrap = hut.Center + perp * (hut.Radius + pad) + dir * 0.15f;
                var ctrl = new List<Vector3>
                {
                    anchor, anchor + hut.Forward * 0.45f, wrap, q + n * 0.35f, end
                };
                raw = CatmullRom(ctrl, 10);
            }

            for (var i = 0; i < raw.Count; i++) raw[i] = Flat(raw[i]);
            return Resample(raw, Step);
        }

        private static List<string> Validate(List<Vector3> pts, HutInfo own, List<HutInfo> huts, List<Obstacle> obstacles,
            Plan plan, float halfWidth, out float minHutClear, out int score)
        {
            var violations = new List<string>();
            minHutClear = float.MaxValue;
            var total = ArcLength(pts);
            var s = 0f;
            int hutHits = 0, obsHits = 0, islandHits = 0, overlapHits = 0;

            for (var i = 0; i < pts.Count; i++)
            {
                if (i > 0) s += D2(pts[i], pts[i - 1]);
                var p = pts[i];

                for (var h = 0; h < huts.Count; h++)
                {
                    var hut = huts[h];
                    if (hut == own && s < 0.5f) continue;
                    var clear = BoxDistance(hut, p);
                    if (clear < minHutClear) minHutClear = clear;
                    if (clear < halfWidth * 0.6f + 0.04f) hutHits++;
                }

                for (var o = 0; o < obstacles.Count; o++)
                    if (D2(p, obstacles[o].Pos) < obstacles[o].Radius + halfWidth * 0.5f) obsHits++;

                if (!InsideIsland(p, 0.45f)) islandHits++;

                // The last 0.8 m legitimately merges into the trunk (junction flare), so it is not an overlap.
                if (s > 0.15f && total - s > 0.8f)
                {
                    var hit = false;
                    for (var k = 0; k < plan.Polys.Count && !hit; k++)
                    {
                        var poly = plan.Polys[k];
                        for (var j = 0; j < poly.Pts.Count; j += 2)
                            if (D2(p, poly.Pts[j]) < halfWidth + poly.HalfWidth * 0.8f) { hit = true; break; }
                    }
                    if (hit) overlapHits++;
                }
            }

            if (hutHits > 0) violations.Add("hut-footprint samples=" + hutHits);
            if (obsHits > 0) violations.Add("obstacle samples=" + obsHits);
            if (islandHits > 0) violations.Add("off-island samples=" + islandHits);
            if (overlapHits > 0) violations.Add("parallel-overlap samples=" + overlapHits);
            if (minHutClear == float.MaxValue) minHutClear = 0f;
            score = hutHits * 10 + obsHits * 5 + islandHits * 10 + overlapHits;
            return violations;
        }

        private static float BoxDistance(HutInfo hut, Vector3 world)
        {
            var l = hut.Root.InverseTransformPoint(world) * hut.Scale;
            var dx = Mathf.Max(hut.Min.x - l.x, 0f, l.x - hut.Max.x);
            var dz = Mathf.Max(hut.Min.y - l.z, 0f, l.z - hut.Max.y);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Matches CozyDioramaStageB2Builder.BuildIslandMesh: x = cos(a)*5.9*wave, z = sin(a)*4.75*wave.
        private static bool InsideIsland(Vector3 p, float margin)
        {
            var nx = p.x / 5.9f;
            var nz = p.z / 4.75f;
            var a = Mathf.Atan2(nz, nx);
            var wave = 1f + 0.075f * Mathf.Sin(3f * a + 0.4f) + 0.045f * Mathf.Sin(7f * a - 0.8f);
            var r = Mathf.Sqrt(nx * nx + nz * nz);
            return r <= wave - margin / 4.75f;
        }

        // ------------------------------------------------------------------ scene objects

        private static string BuildSceneObjects(Transform group, Plan plan, List<HutInfo> huts)
        {
            EnsureFolder("Assets/_Project/Art");
            EnsureFolder("Assets/_Project/Art/CozyDiorama");
            EnsureFolder(MeshFolder);

            var core = BuildMeshObject(group, "Path_Core", MeshFolder + "/PathNetwork_Core.asset", plan, 0f, 0f,
                CozyDioramaPrototypeSceneBuilder.TempMaterial("Cozy_C2_PathEarth", CozyDioramaPrototypeSceneBuilder.Hex("#A58A5C")));
            BuildMeshObject(group, "Path_Fringe", MeshFolder + "/PathNetwork_Fringe.asset", plan, FringeExtra, -0.004f,
                CozyDioramaPrototypeSceneBuilder.TempMaterial("Cozy_C2_PathFringe", CozyDioramaPrototypeSceneBuilder.Hex("#8D8C5B")));

            for (var i = 0; i < plan.Branches.Count; i++)
            {
                var b = plan.Branches[i];
                var door = new GameObject("DoorAnchor_" + b.HutId);
                door.transform.SetParent(group, false);
                door.transform.position = b.Anchor;
                var junction = new GameObject("Junction_" + b.HutId);
                junction.transform.SetParent(group, false);
                junction.transform.position = b.Junction;
            }

            return core;
        }

        private static string BuildMeshObject(Transform parent, string objectName, string assetPath, Plan plan,
            float extraHalf, float yOffset, Material material)
        {
            var data = BuildMeshData(plan, extraHalf, yOffset);
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (mesh == null)
            {
                mesh = new Mesh { name = Path.GetFileNameWithoutExtension(assetPath) };
                AssetDatabase.CreateAsset(mesh, assetPath);
            }
            mesh.Clear();
            mesh.indexFormat = data.Vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(data.Vertices);
            mesh.SetUVs(0, data.Uvs);
            mesh.SetTriangles(data.Triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);

            var go = new GameObject(objectName);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            return HashVertices(data.Vertices);
        }

        private sealed class MeshData
        {
            public List<Vector3> Vertices = new List<Vector3>();
            public List<Vector2> Uvs = new List<Vector2>();
            public List<int> Triangles = new List<int>();
        }

        private static MeshData BuildMeshData(Plan plan, float extraHalf, float yOffset)
        {
            var data = new MeshData();
            for (var i = 0; i < plan.Polys.Count; i++)
                AppendRibbon(plan.Polys[i], extraHalf, yOffset, data);
            return data;
        }

        private static string MeshHash(Plan plan)
        {
            return HashVertices(BuildMeshData(plan, 0f, 0f).Vertices);
        }

        private static void AppendRibbon(Poly poly, float extraHalf, float yOffset, MeshData data)
        {
            var pts = Decimate(poly.Pts, 2);
            if (pts.Count < 2) return;
            const int cols = 5;
            var total = ArcLength(pts);
            var phase = StableHash(poly.Id) * 0.001f;
            var baseIndex = data.Vertices.Count;
            var s = 0f;

            for (var i = 0; i < pts.Count; i++)
            {
                if (i > 0) s += D2(pts[i], pts[i - 1]);
                var prev = pts[Mathf.Max(0, i - 1)];
                var next = pts[Mathf.Min(pts.Count - 1, i + 1)];
                var tangent = Flat(next - prev);
                tangent = tangent.sqrMagnitude < 1e-8f ? Vector3.forward : tangent.normalized;
                var side = Vector3.Cross(Vector3.up, tangent).normalized;
                var w = HalfWidthAt(poly, s, total, phase) + extraHalf;

                for (var c = 0; c < cols; c++)
                {
                    var across = c / (cols - 1f) * 2f - 1f;
                    var pos = pts[i] + side * (across * w);
                    pos.y = poly.Y + yOffset + (1f - across * across) * 0.006f;
                    data.Vertices.Add(pos);
                    data.Uvs.Add(new Vector2((across + 1f) * 0.5f, s));
                }

                if (i == pts.Count - 1) continue;
                for (var c = 0; c < cols - 1; c++)
                {
                    var a = baseIndex + i * cols + c;
                    var b = a + cols;
                    data.Triangles.Add(a); data.Triangles.Add(b); data.Triangles.Add(a + 1);
                    data.Triangles.Add(a + 1); data.Triangles.Add(b); data.Triangles.Add(b + 1);
                }
            }
        }

        private static float HalfWidthAt(Poly p, float s, float total, float phase)
        {
            var w = p.HalfWidth + 0.016f * Mathf.Sin(s * 3.1f + phase) + 0.008f * Mathf.Sin(s * 7.3f + phase * 1.7f);
            var rem = total - s;
            if (p.DoorStart && s < 0.4f) w *= Mathf.Lerp(1.15f, 1f, s / 0.4f);
            if (p.TaperStart && s < 0.35f) w *= Mathf.Lerp(0.45f, 1f, s / 0.35f);
            if (p.TaperEnd && rem < 0.35f) w *= Mathf.Lerp(0.45f, 1f, rem / 0.35f);
            if (p.JunctionEnd && rem < 0.35f) w *= Mathf.Lerp(1.25f, 1f, rem / 0.35f);
            return Mathf.Max(0.05f, w);
        }

        // ------------------------------------------------------------------ scene setup helpers

        private static void RemoveIfPresent(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
        }

        private static void AddHearth(Transform parent)
        {
            var root = new GameObject("Hearth");
            root.transform.SetParent(parent, false);
            CreateCube(root.transform, "Hearth_Stones", new Vector3(HearthPos.x, 0.07f, HearthPos.z),
                new Vector3(0.9f, 0.18f, 0.9f), "Cozy_C2_HearthStone", new Color(0.35f, 0.32f, 0.30f));
            CreateCube(root.transform, "Hearth_Fire", new Vector3(HearthPos.x, 0.20f, HearthPos.z),
                new Vector3(0.35f, 0.25f, 0.35f), "Cozy_C2_HearthFire", new Color(0.95f, 0.45f, 0.15f));
        }

        private static void CreateCube(Transform parent, string name, Vector3 position, Vector3 scale, string materialName, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.transform.localScale = scale;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = CozyDioramaPrototypeSceneBuilder.TempMaterial(materialName, color);
        }

        private static void AddTestHut(Transform parent, string instanceName, string prefabName, Vector3 position, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SettlementPrefabs + prefabName + ".prefab");
            if (prefab == null) throw new InvalidOperationException("Missing hut prefab: " + prefabName);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = instanceName;
            instance.transform.SetParent(parent, false);
            instance.transform.localPosition = position;
            instance.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            instance.transform.localScale = Vector3.one * SettlementCellQuery.HutVisualUniformScale;

            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return;
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            instance.transform.position -= Vector3.up * bounds.min.y;
        }

        private static List<HutInfo> CollectHuts(Transform settlement)
        {
            var result = new List<HutInfo>();
            for (var i = 0; i < settlement.childCount; i++)
            {
                var root = settlement.GetChild(i);
                if (!root.name.StartsWith("Hut_", StringComparison.Ordinal)) continue;
                var door = FindDeepChild(root, "Door");
                if (door == null)
                {
                    Debug.LogWarning("[AFK Island] Skipping hut without Door child: " + root.name);
                    continue;
                }

                var hut = new HutInfo
                {
                    Id = root.name,
                    Root = root,
                    Center = Flat(root.position),
                    DoorWorld = door.position,
                    Forward = Flat(door.forward).normalized,
                    Scale = root.lossyScale.x
                };
                MeasureFootprint(hut);
                result.Add(hut);
            }
            return result;
        }

        private static void MeasureFootprint(HutInfo hut)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            var renderers = hut.Root.GetComponentsInChildren<Renderer>(true);
            for (var r = 0; r < renderers.Length; r++)
            {
                var lb = renderers[r].localBounds;
                for (var ix = -1; ix <= 1; ix += 2)
                for (var iy = -1; iy <= 1; iy += 2)
                for (var iz = -1; iz <= 1; iz += 2)
                {
                    var corner = lb.center + Vector3.Scale(lb.extents, new Vector3(ix, iy, iz));
                    var world = renderers[r].transform.TransformPoint(corner);
                    var local = hut.Root.InverseTransformPoint(world) * hut.Scale;
                    min = Vector2.Min(min, new Vector2(local.x, local.z));
                    max = Vector2.Max(max, new Vector2(local.x, local.z));
                }
            }
            hut.Min = min;
            hut.Max = max;
            hut.Radius = Mathf.Max(max.x - min.x, max.y - min.y) * 0.5f + 0.1f;
        }

        private static List<Obstacle> CollectObstacles(Transform settlement)
        {
            var result = new List<Obstacle>();
            result.Add(new Obstacle { Name = "Hearth", Pos = HearthPos, Radius = HearthRadius });
            for (var i = 0; i < settlement.childCount; i++)
            {
                var child = settlement.GetChild(i);
                if (child.name.StartsWith("Rock_", StringComparison.Ordinal))
                    result.Add(new Obstacle { Name = child.name, Pos = Flat(child.position), Radius = 0.30f });
            }

            var vegetation = GameObject.Find("Vegetation_V2");
            if (vegetation != null)
            {
                for (var i = 0; i < vegetation.transform.childCount; i++)
                {
                    var child = vegetation.transform.GetChild(i);
                    var isTree = child.name.EndsWith("_V2", StringComparison.Ordinal);
                    result.Add(new Obstacle
                    {
                        Name = child.name, Pos = Flat(child.position), Radius = isTree ? 0.28f : 0.22f
                    });
                }
            }
            return result;
        }

        private static Transform FindDeepChild(Transform root, string name)
        {
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindDeepChild(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        // ------------------------------------------------------------------ report

        private static string BuildReport(Plan plan, List<HutInfo> huts, bool planSame, string planHash, bool meshSame,
            string meshHash, string sourceBefore, string sourceAfter)
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine("Stage C-2B settlement path network - network report");
            sb.AppendLine("Scene: " + ScenePath);
            sb.AppendLine("B-2 source scene hash before=" + sourceBefore + " after=" + sourceAfter +
                          " unchanged=" + (sourceBefore == sourceAfter));
            sb.AppendLine("Deterministic rebuild: plan hash " + planHash + " identical=" + planSame +
                          "; mesh hash " + meshHash + " identical=" + meshSame);
            sb.AppendLine("Polylines: " + plan.Polys.Count + " (starter 2, branches " + plan.Branches.Count + ")");
            sb.AppendLine();

            for (var i = 0; i < huts.Count; i++)
            {
                var h = huts[i];
                var b = plan.Branches[i];
                sb.AppendLine("Hut " + h.Id + ": pos=" + F(h.Root.position) + " yaw=" + h.Root.eulerAngles.y.ToString("F1") +
                              " scale=" + h.Scale.ToString("F3"));
                sb.AppendLine("  door(world)=" + F(h.DoorWorld) + " forward=" + F(h.Forward) +
                              " footprint(m) x[" + h.Min.x.ToString("F2") + "," + h.Max.x.ToString("F2") + "] z[" +
                              h.Min.y.ToString("F2") + "," + h.Max.y.ToString("F2") + "]");
                sb.AppendLine("  anchor=" + F(b.Anchor) + " (door + 0.06 m along forward) first-segment/forward dot=" +
                              b.StartDot.ToString("F2"));
                sb.AppendLine("  joins " + b.JunctionOn + " at " + F(b.Junction) + " candidateRank=" + b.CandidateRank +
                              " variant=" + b.Variant + " length=" + b.Length.ToString("F2") + " m");
                sb.AppendLine("  min clearance to any hut footprint along route=" + b.MinHutClear.ToString("F2") + " m");
                sb.AppendLine("  violations: " + (b.Violations.Count == 0 ? "none" : string.Join("; ", b.Violations)));
            }

            sb.AppendLine();
            sb.AppendLine("Junction coverage (branch end vs trunk half-width):");
            for (var i = 0; i < plan.Branches.Count; i++)
            {
                var b = plan.Branches[i];
                var branch = plan.Polys[2 + i];
                var last = branch.Pts[branch.Pts.Count - 1];
                var gap = D2(last, b.Junction);
                sb.AppendLine("  " + b.HutId + ": end is " + gap.ToString("F3") + " m from trunk centreline (trunk half-width >= " +
                              CrossHalfWidth.ToString("F2") + " m) -> overlap=" + (gap < CrossHalfWidth));
            }
            return sb.ToString();
        }

        private static string F(Vector3 v) => "(" + v.x.ToString("F3") + ", " + v.y.ToString("F3") + ", " + v.z.ToString("F3") + ")";

        // ------------------------------------------------------------------ rendering

        private static void RenderAllShots()
        {
            var cameraObject = GameObject.Find("StageB2_Camera");
            if (cameraObject == null)
            {
                Debug.LogError("[AFK Island] StageB2_Camera not found.");
                return;
            }

            var camera = cameraObject.GetComponent<Camera>();
            var originalSize = camera.orthographicSize;
            var originalPos = camera.transform.position;
            var originalTarget = camera.targetTexture;
            var rotation = camera.transform.rotation;

            var output = Path.Combine(Application.dataPath, "..", ShotsDir);
            var docs = Path.Combine(Application.dataPath, "..", DocsShotsDir);
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(docs);

            var group = GameObject.Find("Path_Network");
            var shots = new List<KeyValuePair<string, KeyValuePair<Vector3, float>>>();
            // Same framing as Stage C so the comparison is direct.
            shots.Add(Shot("close_7_5", originalPos, 7.5f));
            shots.Add(Shot("medium_12_8", originalPos, 12.8f));
            shots.Add(Shot("overview_17", originalPos, 17f));
            if (group != null)
            {
                for (var i = 0; i < group.transform.childCount; i++)
                {
                    var child = group.transform.GetChild(i);
                    if (!child.name.StartsWith("DoorAnchor_", StringComparison.Ordinal)) continue;
                    var id = child.name.Substring("DoorAnchor_".Length);
                    var junction = group.transform.Find("Junction_" + id);
                    var a = child.position;
                    var q = junction != null ? junction.position : a;
                    var mid = (a + q) * 0.5f;
                    var size = Mathf.Clamp(D2(a, q) * 0.55f + 1.3f, 2.2f, 4.5f);
                    shots.Add(Shot("detail_" + id.ToLowerInvariant(), CameraPosFor(rotation, mid), size));
                }
            }
            shots.Add(Shot("detail_junction_cross", CameraPosFor(rotation, new Vector3(0f, 0f, -0.6f)), 3.2f));

            try
            {
                // Warm-up: the first frames can render with unfinished shader variants.
                var warmup = Path.Combine(output, "_warmup.png");
                for (var w = 0; w < 3; w++)
                {
                    camera.transform.position = originalPos;
                    RenderShot(camera, warmup, 12.8f);
                }
                File.Delete(warmup);

                for (var i = 0; i < shots.Count; i++)
                {
                    camera.transform.position = shots[i].Value.Key;
                    var file = shots[i].Key + ".png";
                    RenderShot(camera, Path.Combine(output, file), shots[i].Value.Value);
                    File.Copy(Path.Combine(output, file), Path.Combine(docs, file), true);
                }
            }
            finally
            {
                camera.orthographicSize = originalSize;
                camera.transform.position = originalPos;
                camera.targetTexture = originalTarget;
            }

            Debug.Log("[AFK Island] Stage C-2 shots written to " + output + " and " + docs);
        }

        private static KeyValuePair<string, KeyValuePair<Vector3, float>> Shot(string name, Vector3 cameraPos, float size)
        {
            return new KeyValuePair<string, KeyValuePair<Vector3, float>>(
                name, new KeyValuePair<Vector3, float>(cameraPos, size));
        }

        private static Vector3 CameraPosFor(Quaternion rotation, Vector3 target)
        {
            return new Vector3(target.x, 0.3f, target.z) + rotation * new Vector3(0f, 0f, -28f);
        }

        private static void RenderShot(Camera camera, string file, float size)
        {
            const int width = 1920;
            const int height = 1080;
            camera.orthographicSize = size;
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(file, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        // ------------------------------------------------------------------ math / utility

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        private static float D2(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float ArcLength(List<Vector3> pts)
        {
            var total = 0f;
            for (var i = 1; i < pts.Count; i++) total += D2(pts[i], pts[i - 1]);
            return total;
        }

        private static Vector3 TangentAt(List<Vector3> pts, int i)
        {
            var a = pts[Mathf.Max(0, i - 1)];
            var b = pts[Mathf.Min(pts.Count - 1, i + 1)];
            var t = Flat(b - a);
            return t.sqrMagnitude < 1e-8f ? Vector3.forward : t.normalized;
        }

        private static List<Vector3> CatmullRom(List<Vector3> c, int perSegment)
        {
            var result = new List<Vector3>();
            for (var i = 0; i < c.Count - 1; i++)
            {
                var p0 = c[Mathf.Max(0, i - 1)];
                var p1 = c[i];
                var p2 = c[i + 1];
                var p3 = c[Mathf.Min(c.Count - 1, i + 2)];
                for (var k = 0; k < perSegment; k++)
                {
                    var t = k / (float)perSegment;
                    var t2 = t * t;
                    var t3 = t2 * t;
                    result.Add(0.5f * ((2f * p1) + (-p0 + p2) * t +
                                       (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                                       (-p0 + 3f * p1 - 3f * p2 + p3) * t3));
                }
            }
            result.Add(c[c.Count - 1]);
            return result;
        }

        private static List<Vector3> Resample(List<Vector3> pts, float step)
        {
            var result = new List<Vector3> { Flat(pts[0]) };
            var carry = 0f;
            for (var i = 1; i < pts.Count; i++)
            {
                var a = Flat(pts[i - 1]);
                var b = Flat(pts[i]);
                var seg = Vector3.Distance(a, b);
                if (seg < 1e-6f) continue;
                var dist = step - carry;
                while (dist <= seg)
                {
                    result.Add(Vector3.Lerp(a, b, dist / seg));
                    dist += step;
                }
                carry = seg - (dist - step);
            }

            var last = Flat(pts[pts.Count - 1]);
            if (Vector3.Distance(result[result.Count - 1], last) > 1e-3f) result.Add(last);
            return result;
        }

        private static List<Vector3> Decimate(List<Vector3> pts, int every)
        {
            var result = new List<Vector3>();
            for (var i = 0; i < pts.Count; i += every) result.Add(pts[i]);
            if (result[result.Count - 1] != pts[pts.Count - 1]) result.Add(pts[pts.Count - 1]);
            return result;
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                var hash = 17;
                for (var i = 0; i < value.Length; i++) hash = hash * 31 + value[i];
                return hash & 0x7fffffff;
            }
        }

        private static string PlanHash(Plan plan)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < plan.Polys.Count; i++)
            {
                sb.Append(plan.Polys[i].Id).Append(':');
                for (var j = 0; j < plan.Polys[i].Pts.Count; j++)
                {
                    var p = plan.Polys[i].Pts[j];
                    sb.Append(p.x.ToString("F4")).Append(',').Append(p.z.ToString("F4")).Append(';');
                }
            }
            return Sha(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private static string HashVertices(List<Vector3> vertices)
        {
            var bytes = new byte[vertices.Count * 12];
            for (var i = 0; i < vertices.Count; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(vertices[i].x), 0, bytes, i * 12, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(vertices[i].y), 0, bytes, i * 12 + 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(vertices[i].z), 0, bytes, i * 12 + 8, 4);
            }
            return Sha(bytes);
        }

        private static string FileHash(string assetPath)
        {
            return Sha(File.ReadAllBytes(Path.Combine(Application.dataPath, "..", assetPath)));
        }

        private static string Sha(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 16);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace("\\", "/");
            var name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            if (!string.IsNullOrEmpty(parent) && AssetDatabase.IsValidFolder(parent))
                AssetDatabase.CreateFolder(parent, name);
        }
    }
}
#endif
