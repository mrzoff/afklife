#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using AfkIsland.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Isolated Stage C-2 path-network review. Copies the approved B-2 composition into a new
    /// scene, adds two test huts, derives door anchors from prefab children and creates a
    /// deterministic nearest-network branch graph. Production systems are not referenced.
    /// </summary>
    public static class CozyDioramaStageC2Builder
    {
        public const string SourceScene = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageB2.unity";
        public const string ScenePath = "Assets/_Project/Scenes/Prototypes/CozyDioramaStageC2.unity";
        private const string SettlementPath = "Assets/_Project/Resources/AfkIsland/Settlement/";
        private const string MeshFolder = "Assets/_Project/Art/CozyDiorama/StageC2";
        private const float PathHalfWidth = 0.19f;
        private const float PathY = 0.035f;
        private const int CurveSteps = 16;
        private const int WidthSegments = 3;

        private sealed class HutAnchor
        {
            public string Id;
            public Transform Root;
            public Vector3 Door;
        }

        private struct Segment
        {
            public Vector3 A;
            public Vector3 B;
            public Segment(Vector3 a, Vector3 b) { A = a; B = b; }
        }

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
                var scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
                var settlement = GameObject.Find("Settlement");
                if (settlement == null) throw new InvalidOperationException("Settlement root not found in B-2 scene.");

                // Save a copy first; all subsequent changes belong only to the C-2 review scene.
                EditorSceneManager.SaveScene(scene, ScenePath);
                scene = SceneManager.GetActiveScene();
                settlement = GameObject.Find("Settlement");

                var oldRibbon = settlement.transform.Find("Path_Ribbon");
                if (oldRibbon != null) UnityEngine.Object.DestroyImmediate(oldRibbon.gameObject);

                AddTestHut(settlement.transform, "Hut_Test_03", "Hut_A", new Vector3(-0.25f, 0f, -3.15f), -18f);
                AddTestHut(settlement.transform, "Hut_Test_04", "Hut_B", new Vector3(-2.85f, 0f, -2.35f), 22f);

                var huts = CollectHuts(settlement.transform);
                if (huts.Count < 2) throw new InvalidOperationException("At least two huts with Door children are required.");

                // The Stage B-2 ribbon's original centreline is retained as the shared starter spine.
                var spine = new List<Vector3>
                {
                    new Vector3(-0.7f, PathY, -1.0f),
                    new Vector3(0.15f, PathY, -1.55f),
                    new Vector3(1.0f, PathY, -1.85f),
                    new Vector3(1.8f, PathY, -1.55f)
                };

                var network = new List<Segment>();
                for (var i = 0; i < spine.Count - 1; i++)
                {
                    network.Add(new Segment(spine[i], spine[i + 1]));
                    BuildRibbon(settlement.transform, "Path_Network_Spine_" + i, new List<Vector3> { spine[i], spine[i + 1] }, "Spine");
                }

                // Stable identity order makes the same scene rebuild into the same topology.
                huts.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                for (var i = 0; i < huts.Count; i++)
                {
                    var hut = huts[i];
                    var door = hut.Door;
                    door.y = PathY;

                    var nearest = FindNearestPoint(network, door);
                    var branch = BuildCurvedBranch(door, nearest, hut.Id);
                    BuildRibbon(settlement.transform, "Path_Branch_" + hut.Id, branch, "Branch");
                    for (var p = 0; p < branch.Count - 1; p++)
                        network.Add(new Segment(branch[p], branch[p + 1]));
                }

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("[AFK Island] Stage C-2 review scene built: " + ScenePath +
                          " | huts=" + huts.Count + " | branches=" + huts.Count);
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

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var cameraObject = GameObject.Find("StageB2_Camera");
            if (cameraObject == null)
            {
                Debug.LogError("[AFK Island] StageB2_Camera not found.");
                return;
            }

            var camera = cameraObject.GetComponent<Camera>();
            if (camera == null) throw new InvalidOperationException("StageB2_Camera has no Camera component.");
            var originalSize = camera.orthographicSize;
            var originalTarget = camera.targetTexture;
            var output = Path.Combine(Application.dataPath, "..", "Builds/cozy-diorama/stage-c2-shots");
            Directory.CreateDirectory(output);

            try
            {
                RenderShot(camera, Path.Combine(output, "close_7_5.png"), 7.5f);
                RenderShot(camera, Path.Combine(output, "medium_12_8.png"), 12.8f);
                RenderShot(camera, Path.Combine(output, "overview_17.png"), 17f);
            }
            finally
            {
                camera.orthographicSize = originalSize;
                camera.targetTexture = originalTarget;
            }

            Debug.Log("[AFK Island] Stage C-2 shots written to " + output + ". Copy approved images into Docs/CozyDiorama/StageC2-Shots manually.");
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

        private static void AddTestHut(Transform parent, string instanceName, string prefabName, Vector3 position, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SettlementPath + prefabName + ".prefab");
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

        private static List<HutAnchor> CollectHuts(Transform settlement)
        {
            var result = new List<HutAnchor>();
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

                result.Add(new HutAnchor { Id = root.name, Root = root, Door = door.position });
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

        private static Vector3 FindNearestPoint(List<Segment> network, Vector3 point)
        {
            var best = Vector3.zero;
            var bestDistance = float.PositiveInfinity;
            for (var i = 0; i < network.Count; i++)
            {
                var a = network[i].A;
                var b = network[i].B;
                var ab = b - a;
                var denominator = Vector3.Dot(ab, ab);
                var t = denominator > 0.00001f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / denominator) : 0f;
                var candidate = Vector3.Lerp(a, b, t);
                var distance = (point - candidate).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best;
        }

        private static List<Vector3> BuildCurvedBranch(Vector3 fromDoor, Vector3 toNetwork, string stableId)
        {
            var start = new Vector3(fromDoor.x, PathY, fromDoor.z);
            var end = new Vector3(toNetwork.x, PathY, toNetwork.z);
            var direction = (end - start).normalized;
            var perpendicular = new Vector3(-direction.z, 0f, direction.x);
            var hash = StableHash(stableId);
            var bend = ((hash % 2) == 0 ? 1f : -1f) * Mathf.Min(0.22f, Vector3.Distance(start, end) * 0.07f);
            var controlA = Vector3.Lerp(start, end, 0.33f) + perpendicular * bend;
            var controlB = Vector3.Lerp(start, end, 0.67f) + perpendicular * bend;
            var points = new List<Vector3>(CurveSteps + 1);
            for (var i = 0; i <= CurveSteps; i++)
            {
                var t = i / (float)CurveSteps;
                var u = 1f - t;
                var p = u * u * u * start + 3f * u * u * t * controlA +
                        3f * u * t * t * controlB + t * t * t * end;
                points.Add(p);
            }
            return points;
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

        private static void BuildRibbon(Transform parent, string objectName, List<Vector3> points, string kind)
        {
            if (points == null || points.Count < 2) return;
            const float edgeNoise = 0.025f;
            var vertices = new Vector3[points.Count * (WidthSegments + 1)];
            var uvs = new Vector2[vertices.Length];
            var triangles = new List<int>();
            for (var i = 0; i < points.Count; i++)
            {
                var previous = points[Mathf.Max(0, i - 1)];
                var next = points[Mathf.Min(points.Count - 1, i + 1)];
                var tangent = (next - previous).normalized;
                var side = new Vector3(-tangent.z, 0f, tangent.x);
                if (side.sqrMagnitude < 0.0001f) side = Vector3.right;
                for (var w = 0; w <= WidthSegments; w++)
                {
                    var across = w / (float)WidthSegments * 2f - 1f;
                    var noise = Mathf.Sin(i * 0.71f + StableHash(objectName) * 0.001f) * edgeNoise;
                    var vertex = points[i] + side * (across * (PathHalfWidth + noise * Mathf.Abs(across)));
                    vertex.y = PathY + (1f - Mathf.Abs(across)) * 0.008f;
                    var index = i * (WidthSegments + 1) + w;
                    vertices[index] = vertex;
                    uvs[index] = new Vector2(w / (float)WidthSegments, i / (float)(points.Count - 1));
                    if (i == points.Count - 1 || w == WidthSegments) continue;
                    var a = index;
                    var b = index + WidthSegments + 1;
                    triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
                    triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
                }
            }

            var mesh = new Mesh { name = objectName + "_Mesh" };
            mesh.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            EnsureFolder("Assets/_Project/Art");
            EnsureFolder("Assets/_Project/Art/CozyDiorama");
            EnsureFolder(MeshFolder);
            var meshPath = MeshFolder + "/" + objectName + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existing != null) AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            var go = new GameObject(objectName);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = CreatePathMaterial(kind);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }

        private static Material CreatePathMaterial(string kind)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var material = new Material(shader) { name = "C2_Path_" + kind };
            material.color = kind == "Spine"
                ? new Color(0.57f, 0.43f, 0.27f, 1f)
                : new Color(0.64f, 0.49f, 0.31f, 1f);
            return material;
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
