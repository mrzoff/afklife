#if UNITY_EDITOR
using System;
using System.IO;
using AfkIsland.Save;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>
    /// Contract 0.3 stage 1: Save v8 additive collection/fishing + Ensure + Clone.
    /// </summary>
    public static class CollectionFishingSaveSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "collection_fishing_save_smoke_result.txt");

        [MenuItem("AFK Island/Smoke Collection Fishing Save 0.3")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Collection/Fishing save smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Collection/Fishing save smoke OK.");
        }

        public static void RunBatch()
        {
            Debug.Log("[AFK Island] Collection/Fishing save smoke START");
            var code = RunInternal();
            try
            {
                var dir = Path.GetDirectoryName(ResultPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(
                    ResultPath,
                    code == 0 ? "OK" : ("FAIL:" + code));
            }
            catch (Exception writeEx)
            {
                Debug.LogError("[AFK Island] Failed to write smoke result: " + writeEx.Message);
            }

            Debug.Log(code == 0
                ? "[AFK Island] Collection/Fishing save smoke OK."
                : "[AFK Island] Collection/Fishing save smoke FAIL code=" + code);
            EditorApplication.Exit(code);
        }

        private static int RunInternal()
        {
            try
            {
                var version = SaveMigrationService.CurrentVersion;
                if (version != 8)
                {
                    Debug.LogError($"Expected CurrentVersion=8, got {version}");
                    return 2;
                }

                // Explicit null path (not via JsonUtility).
                var bare = new GameSaveData
                {
                    version = 8,
                    lastSaveUtc = 1,
                    collection = null,
                    fishing = null
                };
                SaveMigrationService.EnsureCollectionAndFishingDefaults(bare);
                if (bare.collection?.entries == null || bare.fishing == null || bare.fishing.totalCatches != 0)
                {
                    Debug.LogError("Ensure failed on explicit null collection/fishing.");
                    return 4;
                }

                // Old vc37-style v8 JSON: no collection / fishing fields.
                const string legacyV8Json =
                    "{\"version\":8,\"lastSaveUtc\":1700000000,\"plants\":[],\"chunks\":[],\"animals\":[],\"habitats\":[]}";

                var loaded = JsonUtility.FromJson<GameSaveData>(legacyV8Json);
                if (loaded == null)
                {
                    Debug.LogError("Failed to deserialize legacy v8 JSON.");
                    return 3;
                }

                // JsonUtility may leave omitted nested objects null or as empty defaults —
                // contract only requires Migrate/Ensure to yield usable empty structures on v8.
                var migrated = SaveMigrationService.MigrateIfNeeded(loaded);
                if (migrated == null || migrated.version != 8)
                {
                    Debug.LogError("MigrateIfNeeded failed or bumped version.");
                    return 5;
                }

                if (migrated.collection == null || migrated.collection.entries == null)
                {
                    Debug.LogError("Ensure did not create collection.entries.");
                    return 6;
                }

                if (migrated.fishing == null)
                {
                    Debug.LogError("Ensure did not create fishing.");
                    return 7;
                }

                if (migrated.fishing.totalCatches != 0 || migrated.collection.entries.Count != 0)
                {
                    Debug.LogError(
                        $"Fresh Ensure should leave empty collection and totalCatches=0 " +
                        $"(got entries={migrated.collection.entries.Count}, catches={migrated.fishing.totalCatches}).");
                    return 8;
                }

                // Populate progress, then Ensure again — must not wipe.
                migrated.collection.entries.Add(new CollectionEntrySaveData
                {
                    entryId = "fish_mackerel",
                    discovered = true,
                    discoveryCount = 3,
                    firstDiscoveredUtc = 100,
                    lastDiscoveredUtc = 200
                });
                migrated.fishing.totalCatches = 3;

                SaveMigrationService.EnsureCollectionAndFishingDefaults(migrated);
                SaveMigrationService.EnsureCollectionAndFishingDefaults(migrated);

                if (migrated.collection.entries.Count != 1 ||
                    migrated.collection.entries[0].discoveryCount != 3 ||
                    migrated.fishing.totalCatches != 3)
                {
                    Debug.LogError("Repeat Ensure wiped collection or totalCatches.");
                    return 9;
                }

                var clone = SaveClone.DeepClone(migrated);
                if (clone == null ||
                    clone.collection == null ||
                    clone.fishing == null ||
                    clone.collection.entries == null ||
                    clone.collection.entries.Count != 1 ||
                    clone.collection.entries[0].entryId != "fish_mackerel" ||
                    clone.collection.entries[0].discoveryCount != 3 ||
                    clone.collection.entries[0].firstDiscoveredUtc != 100 ||
                    clone.collection.entries[0].lastDiscoveredUtc != 200 ||
                    clone.fishing.totalCatches != 3)
                {
                    Debug.LogError("SaveClone lost collection/fishing data.");
                    return 10;
                }

                // Independence: mutating clone must not mutate source.
                clone.collection.entries[0].discoveryCount = 99;
                clone.fishing.totalCatches = 99;
                if (migrated.collection.entries[0].discoveryCount != 3 ||
                    migrated.fishing.totalCatches != 3)
                {
                    Debug.LogError("SaveClone shared references with source.");
                    return 11;
                }

                // Round-trip JSON with new fields.
                var json = JsonUtility.ToJson(migrated, false);
                var roundTrip = JsonUtility.FromJson<GameSaveData>(json);
                SaveMigrationService.EnsureCollectionAndFishingDefaults(roundTrip);
                if (roundTrip.collection.entries.Count != 1 ||
                    roundTrip.fishing.totalCatches != 3 ||
                    roundTrip.collection.entries[0].entryId != "fish_mackerel")
                {
                    Debug.LogError("JSON round-trip lost collection/fishing.");
                    return 12;
                }

                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] Collection/Fishing save smoke exception: " + e);
                return 1;
            }
        }
    }
}
#endif
