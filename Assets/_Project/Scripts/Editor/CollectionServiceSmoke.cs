#if UNITY_EDITOR
using System;
using System.IO;
using AfkIsland.Collection;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>Contract 0.3 stage 3: CollectionService discovery progress.</summary>
    public static class CollectionServiceSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "collection_service_smoke_result.txt");

        [MenuItem("AFK Island/Smoke Collection Service 0.3")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("CollectionService smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] CollectionService smoke OK.");
        }

        public static void RunBatch()
        {
            Debug.Log("[AFK Island] CollectionService smoke START");
            var code = RunInternal();
            try
            {
                var dir = Path.GetDirectoryName(ResultPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(ResultPath, code == 0 ? "OK" : ("FAIL:" + code));
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] Failed to write CollectionService smoke result: " + e.Message);
            }

            Debug.Log(code == 0
                ? "[AFK Island] CollectionService smoke OK."
                : "[AFK Island] CollectionService smoke FAIL code=" + code);
            EditorApplication.Exit(code);
        }

        private static int RunInternal()
        {
            GameObject host = null;
            try
            {
                host = new GameObject("CollectionServiceSmokeHost");
                var service = host.AddComponent<CollectionService>();

                var save = CreateBareV8Save();
                SaveMigrationService.EnsureCollectionAndFishingDefaults(save);
                service.Bind(save);

                // A. First discovery
                if (!service.RegisterDiscovery("fish_mackerel"))
                {
                    Debug.LogError("A: RegisterDiscovery failed.");
                    return 2;
                }

                if (!service.TryGetEntry("fish_mackerel", out var first) ||
                    first == null ||
                    !first.discovered ||
                    first.discoveryCount != 1 ||
                    first.firstDiscoveredUtc <= 0 ||
                    first.lastDiscoveredUtc <= 0 ||
                    first.firstDiscoveredUtc != first.lastDiscoveredUtc)
                {
                    Debug.LogError("A: first discovery fields wrong.");
                    return 3;
                }

                var firstUtc = first.firstDiscoveredUtc;

                // B. Repeat discovery
                System.Threading.Thread.Sleep(1100); // ensure unix-second lastDiscovered can advance
                if (!service.RegisterDiscovery("fish_mackerel"))
                {
                    Debug.LogError("B: second RegisterDiscovery failed.");
                    return 4;
                }

                if (!service.TryGetEntry("fish_mackerel", out var second) ||
                    second.discoveryCount != 2 ||
                    second.firstDiscoveredUtc != firstUtc ||
                    second.lastDiscoveredUtc < firstUtc)
                {
                    Debug.LogError(
                        $"B: repeat discovery wrong (count={second?.discoveryCount}, " +
                        $"first={second?.firstDiscoveredUtc}, last={second?.lastDiscoveredUtc}, kept={firstUtc}).");
                    return 5;
                }

                if (second.lastDiscoveredUtc == firstUtc)
                {
                    // Same second is acceptable if clock did not tick; still require count bump.
                    Debug.LogWarning("B: lastDiscoveredUtc same second (clock); count bump verified.");
                }

                // C. Different IDs
                if (!service.RegisterDiscovery("fish_tuna"))
                {
                    Debug.LogError("C: tuna register failed.");
                    return 6;
                }

                var entries = service.GetEntries();
                if (entries.Count != 2 ||
                    service.GetDiscoveryCount("fish_mackerel") != 2 ||
                    service.GetDiscoveryCount("fish_tuna") != 1)
                {
                    Debug.LogError("C: independent entries wrong.");
                    return 7;
                }

                // Mutating GetEntries copy must not touch save.
                entries[0].discoveryCount = 999;
                if (service.GetDiscoveryCount("fish_mackerel") != 2)
                {
                    Debug.LogError("C: GetEntries leaked mutable save reference.");
                    return 8;
                }

                // D. Reads
                if (!service.IsDiscovered("fish_mackerel") ||
                    service.IsDiscovered("fish_missing") ||
                    service.GetDiscoveryCount("fish_missing") != 0 ||
                    service.TryGetEntry("fish_missing", out _))
                {
                    Debug.LogError("D: read API wrong.");
                    return 9;
                }

                // E. Invalid IDs
                if (service.RegisterDiscovery(null) ||
                    service.RegisterDiscovery("") ||
                    service.RegisterDiscovery("   "))
                {
                    Debug.LogError("E: invalid ids must be rejected.");
                    return 10;
                }

                foreach (var e in save.collection.entries)
                {
                    if (e == null || string.IsNullOrWhiteSpace(e.entryId))
                    {
                        Debug.LogError("E: empty entry written to save.");
                        return 11;
                    }
                }

                // F. Re-init on same save
                var service2 = host.AddComponent<CollectionService>();
                service2.Bind(save);
                if (service2.GetDiscoveryCount("fish_mackerel") != 2 ||
                    !service2.IsDiscovered("fish_tuna") ||
                    service2.GetEntries().Count != 2)
                {
                    Debug.LogError("F: re-init lost progress.");
                    return 12;
                }

                // G. JSON save round-trip (simulates persist + reload)
                var json = JsonUtility.ToJson(save, false);
                var loaded = JsonUtility.FromJson<GameSaveData>(json);
                loaded = SaveMigrationService.MigrateIfNeeded(loaded);
                if (loaded == null || loaded.version != 8)
                {
                    Debug.LogError("G: migrate after json failed.");
                    return 13;
                }

                var service3 = host.AddComponent<CollectionService>();
                service3.Bind(loaded);
                if (service3.GetDiscoveryCount("fish_mackerel") != 2 ||
                    service3.GetDiscoveryCount("fish_tuna") != 1 ||
                    !service3.TryGetEntry("fish_mackerel", out var reloaded) ||
                    reloaded.firstDiscoveredUtc != firstUtc)
                {
                    Debug.LogError("G: round-trip lost collection progress.");
                    return 14;
                }

                // H. Force-stop style: reload same payload twice — no duplicate rows
                service3.Bind(loaded);
                if (service3.GetEntries().Count != 2 ||
                    service3.GetDiscoveryCount("fish_mackerel") != 2)
                {
                    Debug.LogError("H: reload duplicated or lost entries.");
                    return 15;
                }

                // I. Legacy vc37-like save without collection field
                const string legacy =
                    "{\"version\":8,\"lastSaveUtc\":1700000000,\"plants\":[],\"chunks\":[" +
                    "{\"chunkX\":0,\"chunkZ\":0,\"isUnlocked\":true,\"biomeId\":\"starter_island\"," +
                    "\"randomSeed\":0,\"lastSimulatedUtc\":1700000000,\"plants\":[]}]," +
                    "\"animals\":[],\"habitats\":[]}";
                var legacySave = JsonUtility.FromJson<GameSaveData>(legacy);
                var islandChunks = legacySave.chunks?.Count ?? 0;
                legacySave = SaveMigrationService.MigrateIfNeeded(legacySave);
                if (legacySave == null ||
                    legacySave.collection == null ||
                    legacySave.collection.entries == null ||
                    legacySave.collection.entries.Count != 0 ||
                    (legacySave.chunks?.Count ?? 0) != islandChunks)
                {
                    Debug.LogError("I: legacy Ensure/collection empty or island mutated.");
                    return 16;
                }

                var legacyService = host.AddComponent<CollectionService>();
                legacyService.Bind(legacySave);
                if (legacyService.IsDiscovered("fish_mackerel") ||
                    legacyService.GetEntries().Count != 0)
                {
                    Debug.LogError("I: legacy collection should start empty.");
                    return 17;
                }

                // No save bound → refuse (no orphan progress)
                var unbound = host.AddComponent<CollectionService>();
                if (unbound.RegisterDiscovery("fish_mackerel"))
                {
                    Debug.LogError("Unbound service must not register discoveries.");
                    return 18;
                }

                // Not FishData-dependent: plain string entryId works.
                if (!service.RegisterDiscovery("nature_event_demo"))
                {
                    Debug.LogError("Generic entryId register failed.");
                    return 19;
                }

                if (SaveMigrationService.CurrentVersion != 8)
                {
                    Debug.LogError("CurrentVersion must remain 8.");
                    return 20;
                }

                // Overflow clamp: discoveryCount must not wrap negative.
                if (!service.TryGetEntry("fish_tuna", out var tunaEntry))
                {
                    Debug.LogError("Overflow setup missing tuna.");
                    return 21;
                }

                // Mutate live save (smoke host) to MaxValue then register again.
                foreach (var e in save.collection.entries)
                {
                    if (e != null && e.entryId == "fish_tuna")
                    {
                        e.discoveryCount = int.MaxValue;
                        break;
                    }
                }

                if (!service.RegisterDiscovery("fish_tuna") ||
                    service.GetDiscoveryCount("fish_tuna") != int.MaxValue)
                {
                    Debug.LogError("discoveryCount must clamp at int.MaxValue, not wrap.");
                    return 22;
                }

                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] CollectionService smoke exception: " + e);
                return 1;
            }
            finally
            {
                if (host != null)
                {
                    UnityEngine.Object.DestroyImmediate(host);
                }
            }
        }

        private static GameSaveData CreateBareV8Save()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new GameSaveData
            {
                version = SaveMigrationService.CurrentVersion,
                lastSaveUtc = now,
                plants = new System.Collections.Generic.List<PlantSaveData>(),
                chunks = new System.Collections.Generic.List<ChunkSaveData>
                {
                    new ChunkSaveData
                    {
                        chunkX = IslandChunk.HomeChunkX,
                        chunkZ = IslandChunk.HomeChunkZ,
                        isUnlocked = true,
                        biomeId = "starter_island",
                        lastSimulatedUtc = now,
                        plants = new System.Collections.Generic.List<PlantSaveData>()
                    }
                },
                animals = new System.Collections.Generic.List<AnimalSaveData>(),
                habitats = new System.Collections.Generic.List<HabitatSaveData>()
            };
        }
    }
}
#endif
