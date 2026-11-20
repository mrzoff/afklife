#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AfkIsland.Fishing;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Grass above the ocean plane must be the nearest hit once LandHitCollider is applied.
    /// Open water stays ocean. A raised pond box above the grass stays the nearest hit.
    /// </summary>
    public static class LandHitColliderSmoke
    {
        [MenuItem("AFK Island/Smoke Land Hit Collider")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Land hit collider smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Land hit collider smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            var created = new List<GameObject>();
            var log = new StringBuilder();
            try
            {
                var ocean = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ocean.name = "Ocean";
                ocean.transform.position = new Vector3(0f, -0.35f, 0f);
                ocean.transform.localScale = new Vector3(3f, 1f, 3f);
                created.Add(ocean);
                FishingOceanInput.EnsureOceanHitProxy();

                var land = new GameObject("GrownLand_0_0");
                created.Add(land);
                var mesh = new Mesh { name = "SmokeLand" };
                mesh.vertices = new[]
                {
                    new Vector3(0f, 0.6f, 0f),
                    new Vector3(4f, 0.6f, 0f),
                    new Vector3(4f, 0.6f, 4f),
                    new Vector3(0f, 0.6f, 4f)
                };
                mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                land.AddComponent<MeshFilter>().sharedMesh = mesh;

                var grassRay = new Ray(new Vector3(2f, 8f, 2f), Vector3.down);
                Physics.SyncTransforms();
                if (!TryNearest(grassRay, out var before) || !FishingOceanInput.IsOceanCollider(before))
                {
                    return Fail(2, "Grass ray must hit the ocean before the land collider exists.", log);
                }

                log.AppendLine("before_collider_hits_ocean=PASS");
                LandHitCollider.Apply(land, mesh);
                Physics.SyncTransforms();
                if (!TryNearest(grassRay, out var after) || after == null || after.gameObject != land)
                {
                    return Fail(3, "Grass ray must stop on GrownLand after LandHitCollider.Apply.", log);
                }

                if (FishingOceanInput.IsOceanCollider(after))
                {
                    return Fail(4, "Grown land must not count as ocean.", log);
                }

                log.AppendLine("grass_blocks_ocean=PASS");

                var waterRay = new Ray(new Vector3(12f, 8f, 12f), Vector3.down);
                if (!TryNearest(waterRay, out var water) || !FishingOceanInput.IsOceanCollider(water))
                {
                    return Fail(5, "Open water ray must still hit Ocean.", log);
                }

                log.AppendLine("open_water_hits_ocean=PASS");

                var pond = new GameObject("PondHitProxy");
                pond.transform.position = new Vector3(2f, 2.2f, 2f);
                var box = pond.AddComponent<BoxCollider>();
                box.size = new Vector3(1.2f, 0.4f, 1.2f);
                created.Add(pond);
                Physics.SyncTransforms();
                if (!TryNearest(grassRay, out var pondHit) || pondHit == null || pondHit.gameObject != pond)
                {
                    return Fail(6, "Raised pond box must stay nearer than the land mesh.", log);
                }

                log.AppendLine("pond_stays_nearest=PASS");
                LandHitCollider.Apply(land, null);
                Physics.SyncTransforms();
                var cleared = land.GetComponent<MeshCollider>();
                if (cleared != null && cleared.enabled)
                {
                    return Fail(7, "Apply(null) must disable the land collider.", log);
                }

                log.AppendLine("clear_disables=PASS");
                var path = Path.Combine("Builds", "Share", "land_hit_collider_smoke_result.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Builds/Share");
                File.WriteAllText(path, log.ToString());
                Debug.Log("[AFK Island] Land hit collider smoke OK.\n" + log);
                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogError(ex);
                return 1;
            }
            finally
            {
                for (var i = created.Count - 1; i >= 0; i--)
                {
                    if (created[i] != null)
                    {
                        UnityEngine.Object.DestroyImmediate(created[i]);
                    }
                }
            }
        }

        private static bool TryNearest(Ray ray, out Collider collider)
        {
            collider = null;
            if (!Physics.Raycast(ray, out var hit, 500f, ~0, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            collider = hit.collider;
            return collider != null;
        }

        private static int Fail(int code, string message, StringBuilder log)
        {
            log.AppendLine("FAIL " + code + ": " + message);
            Debug.LogError("[AFK Island] Land hit collider smoke: " + message);
            var path = Path.Combine("Builds", "Share", "land_hit_collider_smoke_result.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Builds/Share");
            File.WriteAllText(path, log.ToString());
            return code;
        }
    }
}
#endif
