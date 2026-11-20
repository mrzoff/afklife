#if UNITY_EDITOR
using System;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Stage 11E-1: WorldAtmosphereVfx must never move GameSystems / Plants / Animals.
    /// </summary>
    public static class WorldAtmosphereRootSmoke
    {
        [MenuItem("AFK Island/Smoke World Atmosphere Root (11E-1)")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("World Atmosphere Root smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] World Atmosphere Root smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            GameObject systems = null;
            GameObject plants = null;
            WorldAtmosphereVfx isolated = null;

            try
            {
                CleanupLeftovers();

                systems = new GameObject("GameSystems");
                plants = new GameObject("Plants");
                plants.transform.SetParent(systems.transform, false);
                plants.transform.position = new Vector3(3f, 1.5f, -2f);

                // Legacy mistake: VFX on GameSystems. EnsureIsolatedInstance must migrate away.
                systems.AddComponent<WorldAtmosphereVfx>();
                isolated = WorldAtmosphereVfx.EnsureIsolatedInstance();
                if (isolated == null)
                {
                    Debug.LogError("EnsureIsolatedInstance returned null");
                    return 2;
                }

                if (isolated.gameObject.name != WorldAtmosphereVfx.IsolatedRootName)
                {
                    Debug.LogError("Isolated host name mismatch: " + isolated.gameObject.name);
                    return 3;
                }

                if (isolated.transform.parent != null)
                {
                    Debug.LogError("Isolated host must be a scene root");
                    return 4;
                }

                if (isolated.gameObject == systems)
                {
                    Debug.LogError("Atmosphere must not live on GameSystems");
                    return 5;
                }

                if (systems.GetComponent<WorldAtmosphereVfx>() != null)
                {
                    Debug.LogError("Misplaced WorldAtmosphereVfx still on GameSystems");
                    return 9;
                }

                var plantsWorldBefore = plants.transform.position;
                var systemsBefore = systems.transform.position;
                var follow = new GameObject("SmokeFollow").transform;
                follow.position = new Vector3(20f, 8f, 12f);
                isolated.Configure(follow);
                isolated.TickFollowAndState(forceState: true);

                if (systems.transform.position != systemsBefore)
                {
                    Debug.LogError("GameSystems moved after atmosphere follow — Plants would drift");
                    return 6;
                }

                if (plants.transform.position != plantsWorldBefore)
                {
                    Debug.LogError("Plants moved after atmosphere follow");
                    return 7;
                }

                var expected = new Vector3(follow.position.x, follow.position.y + 6f * 0.35f, follow.position.z);
                if (Vector3.Distance(isolated.transform.position, expected) > 0.01f)
                {
                    Debug.LogError(
                        "VFX root did not follow camera. got=" + isolated.transform.position + " expected=" + expected);
                    return 8;
                }

                UnityEngine.Object.DestroyImmediate(follow.gameObject);
                Debug.Log("[AFK Island] World Atmosphere Root smoke OK.");
                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return 1;
            }
            finally
            {
                if (systems != null)
                {
                    UnityEngine.Object.DestroyImmediate(systems);
                }

                CleanupLeftovers();
            }
        }

        private static void CleanupLeftovers()
        {
            var root = GameObject.Find(WorldAtmosphereVfx.IsolatedRootName);
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            var leftovers = UnityEngine.Object.FindObjectsByType<WorldAtmosphereVfx>();
            for (var i = 0; i < leftovers.Length; i++)
            {
                if (leftovers[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(leftovers[i].gameObject);
                }
            }

            var systems = GameObject.Find("GameSystems");
            if (systems != null)
            {
                UnityEngine.Object.DestroyImmediate(systems);
            }

            var follow = GameObject.Find("SmokeFollow");
            if (follow != null)
            {
                UnityEngine.Object.DestroyImmediate(follow);
            }
        }
    }
}
#endif
