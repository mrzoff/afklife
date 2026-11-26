using AfkIsland.Fishing;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Builds the authored home island: rocky footprint + compact starter settlement (Stage A).
    /// Keep yard height ~1.55 and WaterSource pose so AFK land / legacy plots stay aligned.
    /// </summary>
    public sealed class IslandPrototypeBuilder : MonoBehaviour
    {
        [SerializeField] private bool buildOnAwake = true;
        private static readonly Vector3 AuthoredCedarPosition = new(-3.1f, 1.85f, -1.4f);

        private void Awake()
        {
            if (buildOnAwake)
            {
                if (transform.childCount == 0)
                {
                    Build();
                }
                else
                {
                    EnsureSettlementLayout();
                }
            }

            // Upgrade already-authored scenes as well as newly built islands.
            UpgradeOrganicIslandPlatforms(transform.Find("Island"));
            StripDecorativeWaterColliders();
            AlignAuthoredWaterSource();
            // Stage 10B/11E: after WaterSource exists and root colliders are stripped, attach child hit proxy.
            FishingPondInput.EnsurePondHitProxy();
            // Authored scenes may still have SettlementYard BoxCollider from older builds.
            var yard = transform.Find("Island/SettlementYard");
            if (yard != null)
            {
                StripCollider(yard.gameObject);
            }

            UpgradeAuthoredNatureVisuals();
            // C-3I: starter hut/pad/road live in the save mask; keep scene props off that footprint.
            LiftAuthoredPropsOffStarterSettlement();
            LiftAuthoredPropsOutOfPond();
            UpgradeAuthoredSettlementVisuals();
            var water = GetComponent<WaterSurfaceApplicator>() ?? gameObject.AddComponent<WaterSurfaceApplicator>();
            water.Apply();
        }

        /// <summary>
        /// C-3I: the authored starter settlement (see <see cref="StarterSettlementSeed"/>) is written into the
        /// home chunk settlement mask, so scene props never know about it. Push any prop whose position falls
        /// inside the starter footprint radially outward. Scene file is not modified; this runs at Awake only.
        /// </summary>
        private static void LiftAuthoredPropsOffStarterSettlement()
        {
            var props = GameObject.Find("NaturePackProps");
            if (props == null)
            {
                return;
            }

            var centers = StarterSettlementSeed.GetFootprintCentersXZ();
            var radius = StarterSettlementSeed.PropClearanceRadius;
            for (var i = 0; i < props.transform.childCount; i++)
            {
                var child = props.transform.GetChild(i);
                var position = child.position;
                var nearest = -1;
                var nearestSq = radius * radius;
                for (var c = 0; c < centers.Count; c++)
                {
                    var dx = position.x - centers[c].x;
                    var dz = position.z - centers[c].y;
                    var sq = dx * dx + dz * dz;
                    if (sq < nearestSq)
                    {
                        nearestSq = sq;
                        nearest = c;
                    }
                }

                if (nearest < 0)
                {
                    continue;
                }

                var away = new Vector2(position.x - centers[nearest].x, position.z - centers[nearest].y);
                if (away.sqrMagnitude < 0.0001f)
                {
                    // Prop sits exactly on a cell centre: step west, away from the pond.
                    away = Vector2.left;
                }

                away.Normalize();
                // Step out from the nearest centre, then re-check all centres; a few small steps suffice.
                var distance = radius + 0.05f;
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    var x = centers[nearest].x + away.x * distance;
                    var z = centers[nearest].y + away.y * distance;
                    var clear = true;
                    for (var c = 0; c < centers.Count; c++)
                    {
                        var dx = x - centers[c].x;
                        var dz = z - centers[c].y;
                        if (dx * dx + dz * dz < radius * radius)
                        {
                            clear = false;
                            break;
                        }
                    }

                    if (clear)
                    {
                        position.x = x;
                        position.z = z;
                        child.position = position;
                        break;
                    }

                    distance += 0.25f;
                }
            }
        }

        /// <summary>
        /// NaturePackProps are scene instances. Chunk decor and colonization never see them.
        /// The authored pine at (2.4, -1.5) stands in the pond; push any such trunk onto the bank.
        /// </summary>
        private static void LiftAuthoredPropsOutOfPond()
        {
            var props = GameObject.Find("NaturePackProps");
            if (props == null)
            {
                return;
            }

            for (var i = 0; i < props.transform.childCount; i++)
            {
                var child = props.transform.GetChild(i);
                var scale = Mathf.Max(Mathf.Abs(child.lossyScale.x), Mathf.Abs(child.lossyScale.z));
                var radius = WaterSourceLayout.TrunkRadius(scale);
                var pushed = WaterSourceLayout.PushTrunkOutOfWater(child.position.x, child.position.z, radius);
                if (Mathf.Abs(pushed.x - child.position.x) < 0.001f &&
                    Mathf.Abs(pushed.y - child.position.z) < 0.001f)
                {
                    continue;
                }

                var position = child.position;
                position.x = pushed.x;
                position.z = pushed.y;
                child.position = position;
            }
        }

        /// <summary>
        /// Stage 9.1 — swap authored primitive tree/stones for Polyeler decor without changing poses/names.
        /// </summary>
        private void UpgradeAuthoredNatureVisuals()
        {
            var settlement = transform.Find("Island/Settlement");
            if (settlement == null)
            {
                return;
            }

            var tree = settlement.Find("SettlementTree");
            if (tree != null)
            {
                // Keep the authored cedar clear of Hut_A while staying on the rounded yard.
                tree.localPosition = AuthoredCedarPosition;
            }

            if (tree != null && tree.Find("Canopy") != null)
            {
                var prefab = Resources.Load<GameObject>("AfkIsland/Decor/JapaneseCedar");
                if (prefab != null)
                {
                    var pos = tree.localPosition;
                    var rot = tree.localRotation;
                    for (var i = tree.childCount - 1; i >= 0; i--)
                    {
                        DestroyChild(tree.GetChild(i).gameObject);
                    }

                    var visual = Instantiate(prefab, tree);
                    visual.name = "Visual";
                    visual.transform.localPosition = Vector3.zero;
                    visual.transform.localRotation = Quaternion.identity;
                    visual.transform.localScale = Vector3.one * 0.55f;
                    StripColliderDeep(visual);
                    VegetationWindApplicator.ApplyToInstance(visual, isRock: false, styleName: "JapaneseCedar");
                    tree.localPosition = pos;
                    tree.localRotation = rot;
                }
            }
            else if (tree != null)
            {
                var existing = tree.Find("Visual");
                if (existing != null)
                {
                    VegetationWindApplicator.ApplyToInstance(
                        existing.gameObject, isRock: false, styleName: "JapaneseCedar");
                }
            }

            for (var i = 0; i < settlement.childCount; i++)
            {
                var child = settlement.GetChild(i);
                if (child == null || !child.name.StartsWith("Stone_"))
                {
                    continue;
                }

                // Primitive sphere stones have MeshFilter on the root; Polyeler rocks are LOD roots.
                var rootMf = child.GetComponent<MeshFilter>();
                if (rootMf == null)
                {
                    continue;
                }

                var rock = Resources.Load<GameObject>(
                    i % 2 == 0 ? "AfkIsland/Decor/Rock_A" : "AfkIsland/Decor/Rock_B");
                if (rock == null)
                {
                    continue;
                }

                var pos = child.localPosition;
                var rot = child.localRotation;
                var scale = child.localScale;
                for (var c = child.childCount - 1; c >= 0; c--)
                {
                    DestroyChild(child.GetChild(c).gameObject);
                }

                DestroyComponent(child.GetComponent<MeshRenderer>());
                DestroyComponent(child.GetComponent<MeshFilter>());
                DestroyComponent(child.GetComponent<Collider>());

                var visual = Instantiate(rock, child);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one;
                StripColliderDeep(visual);
                child.localPosition = pos;
                child.localRotation = rot;
                child.localScale = scale * 0.85f;
            }
        }

        /// <summary>
        /// Stage 9.2 — swap authored primitive Hut_A/Hut_B for Settlement kit prefabs (names/poses kept for SoftDim).
        /// </summary>
        private void UpgradeAuthoredSettlementVisuals()
        {
            var settlement = transform.Find("Island/Settlement");
            if (settlement == null)
            {
                return;
            }

            TryReplaceAuthoredHut(settlement.Find("Hut_A"), "AfkIsland/Settlement/Hut_A");
            TryReplaceAuthoredHut(settlement.Find("Hut_B"), "AfkIsland/Settlement/Hut_B");
        }

        private static void TryReplaceAuthoredHut(Transform hut, string resourcePath)
        {
            if (hut == null || hut.Find("Body") == null || hut.Find("Roof") == null)
            {
                return;
            }

            // Already upgraded (gable roofs use Roof_L / Roof_R).
            if (hut.Find("Roof_L") != null)
            {
                return;
            }

            var prefab = Resources.Load<GameObject>(resourcePath);
            if (prefab == null)
            {
                return;
            }

            var pos = hut.localPosition;
            var rot = hut.localRotation;
            var scale = hut.localScale;
            for (var i = hut.childCount - 1; i >= 0; i--)
            {
                DestroyChild(hut.GetChild(i).gameObject);
            }

            var visual = Object.Instantiate(prefab, hut);
            visual.name = "Visual";
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
            StripColliderDeep(visual);
            hut.localPosition = pos;
            hut.localRotation = rot;
            hut.localScale = scale;
        }

        private static void DestroyChild(GameObject go)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(go);
            }
            else
            {
                Object.DestroyImmediate(go);
            }
        }

        private static void DestroyComponent(Component c)
        {
            if (c == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(c);
            }
            else
            {
                Object.DestroyImmediate(c);
            }
        }

        private static void StripColliderDeep(GameObject root)
        {
            var cols = root.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < cols.Length; i++)
            {
                DestroyComponent(cols[i]);
            }
        }

        private void StripDecorativeWaterColliders()
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.name != "WaterSource")
                {
                    continue;
                }

                // Root visual only — leave child PondHitProxy MeshCollider intact.
                StripCollider(t.gameObject);
            }
        }

        [ContextMenu("Rebuild Island Prototype")]
        public void Build()
        {
            ClearChildren();
            CreateOcean();
            var island = CreateIslandBase();
            CreateStarterSettlement(island.transform);
            var pond = CreateWaterSource(island.transform);
            FishingPondInput.EnsurePondHitProxy(pond);
            CreateAmbientLightHints();
            Debug.Log("[AFK Island] Settlement island built.");
        }

        /// <summary>
        /// Upgrade an already-authored scene (Ocean + old cube island) without wiping Ocean.
        /// </summary>
        public void EnsureSettlementLayout()
        {
            if (transform.Find("Ocean") == null)
            {
                CreateOcean();
            }

            var island = transform.Find("Island");
            // PlantPlots is the authored starter garden. Replacing the island drops those
            // colliders and leaves PlantingSystem pointing at destroyed plots.
            if (island != null &&
                (island.Find("Settlement") != null || island.Find("PlantPlots") != null))
            {
                return;
            }

            if (island != null)
            {
                // Rename before destroy so GameObject.Find("WaterSource") cannot bind a dying object.
                // DestroyImmediate in play — deferred Destroy left two "Island" roots for a frame and
                // Find/upgrade could race (missing settlement visual / camera looking at void trees).
                RetireNamedChildren(island, "WaterSource");
                if (Application.isPlaying)
                {
                    DestroyImmediate(island.gameObject);
                }
                else
                {
                    DestroyImmediate(island.gameObject);
                }
            }

            island = CreateIslandBase().transform;
            CreateStarterSettlement(island);
            var pond = CreateWaterSource(island);
            FishingPondInput.EnsurePondHitProxy(pond);
            Debug.Log("[AFK Island] Upgraded authored island to starter settlement.");
        }

        private void ClearChildren()
        {
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                if (Application.isPlaying)
                {
                    Destroy(child);
                }
                else
                {
                    DestroyImmediate(child);
                }
            }
        }

        private GameObject CreateOcean()
        {
            var ocean = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ocean.name = "Ocean";
            ocean.transform.SetParent(transform, false);
            ocean.transform.localScale = new Vector3(20f, 1f, 20f);
            ocean.transform.position = new Vector3(0f, -0.35f, 0f);
            ApplyColor(ocean, new Color(0.12f, 0.42f, 0.62f, 1f));
            return ocean;
        }

        private GameObject CreateIslandBase()
        {
            var root = new GameObject("Island");
            root.transform.SetParent(transform, false);

            CreateScaledCube(root.transform, "BaseRock", new Vector3(0f, 0.2f, 0f), new Vector3(18f, 1.2f, 14f),
                new Color(0.45f, 0.40f, 0.34f));
            CreateScaledCube(root.transform, "MidPlateau", new Vector3(0f, 0.9f, 0f), new Vector3(12f, 1.0f, 10f),
                new Color(0.36f, 0.48f, 0.28f));
            // Settlement yard (was CentralMeadow) — same height so plots/fauna stay valid.
            CreateScaledCube(root.transform, "SettlementYard", new Vector3(0f, 1.55f, 0f), new Vector3(7f, 0.6f, 6f),
                new Color(0.42f, 0.52f, 0.32f));
            CreateScaledCube(root.transform, "ShoreSand_N", new Vector3(0f, 0.35f, 6.5f), new Vector3(10f, 0.35f, 2.5f),
                new Color(0.76f, 0.70f, 0.48f));
            CreateScaledCube(root.transform, "ShoreSand_S", new Vector3(0f, 0.35f, -6.2f), new Vector3(9f, 0.35f, 2.2f),
                new Color(0.76f, 0.70f, 0.48f));

            UpgradeOrganicIslandPlatforms(root.transform);
            return root;
        }

        /// <summary>
        /// Replaces the hard rectangular starter slabs with rounded, irregular platform meshes.
        /// Top surfaces remain flat so existing settlement, plots, pond and gameplay coordinates stay valid.
        /// </summary>
        private static void UpgradeOrganicIslandPlatforms(Transform island)
        {
            if (island == null)
            {
                return;
            }

            var names = new[] { "BaseRock", "MidPlateau", "SettlementYard", "ShoreSand_N", "ShoreSand_S" };
            for (var n = 0; n < names.Length; n++)
            {
                var platform = island.Find(names[n]);
                if (platform == null)
                {
                    continue;
                }

                var filter = platform.GetComponent<MeshFilter>();
                if (filter == null ||
                    (filter.sharedMesh != null && filter.sharedMesh.name.StartsWith("AFK_Organic_")))
                {
                    continue;
                }

                var mesh = BuildOrganicPlatformMesh(names[n]);
                filter.sharedMesh = mesh;

                var oldBox = platform.GetComponent<BoxCollider>();
                if (oldBox != null)
                {
                    if (Application.isPlaying) Destroy(oldBox);
                    else DestroyImmediate(oldBox);
                }

                var oldMeshCollider = platform.GetComponent<MeshCollider>();
                if (names[n] == "SettlementYard")
                {
                    if (oldMeshCollider != null)
                    {
                        if (Application.isPlaying) Destroy(oldMeshCollider);
                        else DestroyImmediate(oldMeshCollider);
                    }

                    continue;
                }

                var meshCollider = oldMeshCollider != null
                    ? oldMeshCollider
                    : platform.gameObject.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = null;
                meshCollider.sharedMesh = mesh;
            }
        }

        private static Mesh BuildOrganicPlatformMesh(string platformName)
        {
            const int segments = 64;
            var vertices = new Vector3[2 + segments * 3];
            var triangles = new int[segments * 18];
            var seed = 17;
            for (var i = 0; i < platformName.Length; i++)
            {
                seed = unchecked(seed * 31 + platformName[i]);
            }

            var phase = (seed & 1023) * 0.0061f;
            var amplitude = platformName == "BaseRock" ? 0.12f :
                platformName == "MidPlateau" ? 0.095f :
                platformName == "SettlementYard" ? 0.10f : 0.11f;
            // The yard uses a soft superellipse so authored fence posts remain on its flat top.
            // Other layers stay closer to an oval island silhouette.
            var exponent = platformName == "SettlementYard" ? 3f : 2f;
            var coordinatePower = 2f / exponent;

            vertices[0] = new Vector3(0f, 0.5f, 0f);
            var topStart = 1;
            var middleStart = topStart + segments;
            var bottomStart = middleStart + segments;
            var bottomCenter = bottomStart + segments;
            vertices[bottomCenter] = new Vector3(0f, -0.5f, 0f);

            for (var i = 0; i < segments; i++)
            {
                var angle = (Mathf.PI * 2f * i) / segments;
                var shape =
                    1f +
                    Mathf.Sin(angle * 3f + phase) * amplitude * 0.48f +
                    Mathf.Sin(angle * 5f - phase * 1.7f) * amplitude * 0.32f +
                    Mathf.Sin(angle * 9f + phase * 0.7f) * amplitude * 0.20f;

                var cosine = Mathf.Cos(angle);
                var sine = Mathf.Sin(angle);
                var x = Mathf.Sign(cosine) * Mathf.Pow(Mathf.Abs(cosine), coordinatePower) * 0.5f * shape;
                var z = Mathf.Sign(sine) * Mathf.Pow(Mathf.Abs(sine), coordinatePower) * 0.5f * shape;
                vertices[topStart + i] = new Vector3(x, 0.5f, z);
                vertices[middleStart + i] = new Vector3(x * 0.96f, 0.02f, z * 0.96f);
                vertices[bottomStart + i] = new Vector3(x * 0.82f, -0.5f, z * 0.82f);
            }

            var t = 0;
            for (var i = 0; i < segments; i++)
            {
                var next = (i + 1) % segments;
                var top = topStart + i;
                var topNext = topStart + next;
                var middle = middleStart + i;
                var middleNext = middleStart + next;
                var bottom = bottomStart + i;
                var bottomNext = bottomStart + next;

                // Top fan faces upward.
                triangles[t++] = 0;
                triangles[t++] = topNext;
                triangles[t++] = top;

                // Two sloped side bands.
                triangles[t++] = top;
                triangles[t++] = topNext;
                triangles[t++] = middle;
                triangles[t++] = topNext;
                triangles[t++] = middleNext;
                triangles[t++] = middle;

                triangles[t++] = middle;
                triangles[t++] = middleNext;
                triangles[t++] = bottom;
                triangles[t++] = middleNext;
                triangles[t++] = bottomNext;
                triangles[t++] = bottom;

                // Bottom fan faces downward.
                triangles[t++] = bottomCenter;
                triangles[t++] = bottom;
                triangles[t++] = bottomNext;
            }

            var mesh = new Mesh
            {
                name = "AFK_Organic_" + platformName,
                hideFlags = HideFlags.DontSave
            };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void CreateStarterSettlement(Transform island)
        {
            var root = new GameObject("Settlement");
            root.transform.SetParent(island, false);
            root.transform.localPosition = Vector3.zero;

            // Path through the yard (local Y sits on SettlementYard top ~1.85).
            CreateScaledCube(root.transform, "Path_Main", new Vector3(0f, 1.88f, 0.2f), new Vector3(1.1f, 0.06f, 4.2f),
                new Color(0.55f, 0.48f, 0.36f));
            CreateScaledCube(root.transform, "Path_Cross", new Vector3(0.9f, 1.88f, -0.6f), new Vector3(2.4f, 0.05f, 0.7f),
                new Color(0.55f, 0.48f, 0.36f));

            CreateHut(root.transform, "Hut_A", new Vector3(-1.6f, 1.85f, 0.9f), 12f);
            CreateHut(root.transform, "Hut_B", new Vector3(1.8f, 1.85f, -0.2f), -25f);

            // Stone ring / hearth.
            CreateScaledCube(root.transform, "Hearth", new Vector3(0.15f, 1.92f, -1.5f), new Vector3(0.9f, 0.18f, 0.9f),
                new Color(0.35f, 0.32f, 0.3f));
            CreateScaledCube(root.transform, "HearthFire", new Vector3(0.15f, 2.05f, -1.5f), new Vector3(0.35f, 0.25f, 0.35f),
                new Color(0.95f, 0.45f, 0.15f));

            Vector3[] stones =
            {
                new Vector3(-2.6f, 1.95f, -1.2f),
                new Vector3(2.5f, 1.95f, 1.1f),
                new Vector3(-2.2f, 1.95f, 1.8f),
                new Vector3(2.8f, 1.95f, -1.6f)
            };
            for (var i = 0; i < stones.Length; i++)
            {
                var rock = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                rock.name = $"Stone_{i:00}";
                rock.transform.SetParent(root.transform, false);
                rock.transform.localPosition = stones[i];
                rock.transform.localScale = Vector3.one * (0.45f + (i % 2) * 0.15f);
                rock.transform.localRotation = Quaternion.Euler(10f * i, 40f * i, 5f * i);
                ApplyColor(rock, new Color(0.48f, 0.45f, 0.42f));
                StripCollider(rock);
            }

            // Short fence fragments (not a full wall).
            Vector3[] fencePosts =
            {
                new Vector3(-3.0f, 2.05f, 0.2f),
                new Vector3(-3.0f, 2.05f, 1.1f),
                new Vector3(-3.0f, 2.05f, 2.0f),
                new Vector3(3.1f, 2.05f, -0.8f),
                new Vector3(3.1f, 2.05f, 0.1f)
            };
            for (var i = 0; i < fencePosts.Length; i++)
            {
                CreateScaledCube(root.transform, $"FencePost_{i:00}", fencePosts[i],
                    new Vector3(0.12f, 0.7f, 0.12f), new Color(0.38f, 0.26f, 0.14f));
                if (i + 1 < fencePosts.Length &&
                    Mathf.Abs(fencePosts[i].x - fencePosts[i + 1].x) < 0.2f)
                {
                    var mid = (fencePosts[i] + fencePosts[i + 1]) * 0.5f;
                    mid.y = 2.15f;
                    CreateScaledCube(root.transform, $"FenceRail_{i:00}", mid,
                        new Vector3(0.08f, 0.08f, 0.85f), new Color(0.42f, 0.3f, 0.16f));
                }
            }

            // One shade tree at the edge of the yard (Stage 9.1 Polyeler; primitive fallback).
            var tree = new GameObject("SettlementTree");
            tree.transform.SetParent(root.transform, false);
            tree.transform.localPosition = AuthoredCedarPosition;
            var cedar = Resources.Load<GameObject>("AfkIsland/Decor/JapaneseCedar");
            if (cedar != null)
            {
                var visual = Object.Instantiate(cedar, tree.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one * 0.55f;
                StripColliderDeep(visual);
                VegetationWindApplicator.ApplyToInstance(visual, isRock: false, styleName: "JapaneseCedar");
            }
            else
            {
                var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                trunk.name = "Trunk";
                trunk.transform.SetParent(tree.transform, false);
                trunk.transform.localPosition = new Vector3(0f, 0.55f, 0f);
                trunk.transform.localScale = new Vector3(0.22f, 0.55f, 0.22f);
                ApplyColor(trunk, new Color(0.40f, 0.26f, 0.16f));
                StripCollider(trunk);
                var canopy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                canopy.name = "Canopy";
                canopy.transform.SetParent(tree.transform, false);
                canopy.transform.localPosition = new Vector3(0f, 1.25f, 0f);
                canopy.transform.localScale = new Vector3(1.2f, 1.0f, 1.2f);
                ApplyColor(canopy, new Color(0.22f, 0.5f, 0.24f));
                StripCollider(canopy);
            }
        }

        private static void CreateHut(Transform parent, string name, Vector3 pos, float yaw)
        {
            var hut = new GameObject(name);
            hut.transform.SetParent(parent, false);
            hut.transform.localPosition = pos;
            hut.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            var resource = name == "Hut_B"
                ? "AfkIsland/Settlement/Hut_B"
                : "AfkIsland/Settlement/Hut_A";
            var prefab = Resources.Load<GameObject>(resource);
            if (prefab != null)
            {
                var visual = Object.Instantiate(prefab, hut.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one;
                StripColliderDeep(visual);
                return;
            }

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(hut.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            body.transform.localScale = new Vector3(1.35f, 1.1f, 1.15f);
            ApplyColor(body, new Color(0.55f, 0.38f, 0.22f));
            StripCollider(body);

            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
            roof.name = "Roof";
            roof.transform.SetParent(hut.transform, false);
            roof.transform.localPosition = new Vector3(0f, 1.25f, 0f);
            roof.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
            roof.transform.localScale = new Vector3(1.55f, 0.35f, 1.55f);
            ApplyColor(roof, new Color(0.35f, 0.22f, 0.14f));
            StripCollider(roof);

            var door = GameObject.CreatePrimitive(PrimitiveType.Cube);
            door.name = "Door";
            door.transform.SetParent(hut.transform, false);
            door.transform.localPosition = new Vector3(0f, 0.4f, 0.58f);
            door.transform.localScale = new Vector3(0.4f, 0.7f, 0.08f);
            ApplyColor(door, new Color(0.28f, 0.18f, 0.1f));
            StripCollider(door);
        }

        private static void RetireNamedChildren(Transform root, string name)
        {
            if (root == null)
            {
                return;
            }

            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t != null && t.name == name)
                {
                    t.name = name + "_Retired";
                }
            }
        }

        private GameObject CreateWaterSource(Transform island)
        {
            var pond = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pond.name = "WaterSource";
            pond.transform.SetParent(island, false);
            pond.transform.localPosition = WaterSourceLayout.LocalPosition;
            pond.transform.localScale = WaterSourceLayout.LocalScale;
            WaterSourceLayout.ClearCenterCache();
            ApplyColor(pond, new Color(0.25f, 0.55f, 0.72f));
            StripCollider(pond);
            return pond;
        }

        /// <summary>Raise authored WaterSource onto yard top if an old scene pose is still present.</summary>
        private static void AlignAuthoredWaterSource()
        {
            var pond = GameObject.Find("WaterSource");
            if (pond == null)
            {
                return;
            }

            var island = pond.transform.parent;
            if (island == null)
            {
                return;
            }

            pond.transform.localPosition = WaterSourceLayout.LocalPosition;
            pond.transform.localScale = WaterSourceLayout.LocalScale;
            WaterSourceLayout.ClearCenterCache();
        }

        private static void StripCollider(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(col);
            }
            else
            {
                Object.DestroyImmediate(col);
            }
        }

        private void CreateAmbientLightHints()
        {
            if (FindFirstObjectByType<Light>() == null)
            {
                var lightGo = new GameObject("Sun");
                lightGo.transform.SetParent(transform, false);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = new Color(1f, 0.96f, 0.90f);
                light.intensity = 1.15f;
                lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }
        }

        private static void CreateScaledCube(Transform parent, string name, Vector3 pos, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            ApplyColor(go, color);
            // Stage 11E: SettlementYard must not steal nearest-hit pond taps (pond sits on yard).
            if (name.StartsWith("Path_") ||
                name.StartsWith("Hearth") ||
                name == "SettlementYard")
            {
                StripCollider(go);
            }
        }

        private static void ApplyColor(GameObject go, Color color)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }
            else
            {
                mat.color = color;
            }

            renderer.sharedMaterial = mat;
        }
    }
}
