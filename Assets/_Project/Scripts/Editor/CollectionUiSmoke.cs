#if UNITY_EDITOR
using System;
using System.IO;
using AfkIsland.Collection;
using AfkIsland.Fishing;
using AfkIsland.Save;
using AfkIsland.UI;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>Stage 8A/8B: CollectionUI list builder + open/close + entry visibility smoke.</summary>
    public static class CollectionUiSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "collection_ui_smoke_result.txt");

        [MenuItem("AFK Island/Smoke Collection UI Stage 8B")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Collection UI Stage 8B smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Collection UI Stage 8B smoke OK.");
        }

        public static void RunBatch()
        {
            Debug.Log("[AFK Island] Collection UI Stage 8B smoke START");
            SetupFishCatalogMenu.SetupQuiet();
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
                Debug.LogError("[AFK Island] Failed to write collection UI smoke result: " + e.Message);
            }

            EditorApplication.Exit(code);
        }

        private static int RunInternal()
        {
            GameObject host = null;
            FishCatalog emptyCatalog = null;
            FishCatalog dupCatalog = null;
            FishData dupA = null;
            FishData dupB = null;
            try
            {
                SetupFishCatalogMenu.SetupQuiet();
                var catalog = Resources.Load<FishCatalog>("FishCatalog");
                if (catalog == null || catalog.Fish == null || catalog.Fish.Count == 0)
                {
                    Debug.LogError("FishCatalog missing or empty.");
                    return 2;
                }

                host = new GameObject("CollectionUiSmokeHost");
                var collection = host.AddComponent<CollectionService>();
                var ui = host.AddComponent<CollectionUI>();
                var fishing = host.AddComponent<FishingController>();
                var fishingUi = host.AddComponent<FishingUI>();
                var save = new GameSaveData
                {
                    version = SaveMigrationService.CurrentVersion,
                    collection = new CollectionSaveData(),
                    fishing = new FishingSaveData()
                };
                SaveMigrationService.EnsureCollectionAndFishingDefaults(save);
                collection.Bind(save);

                // Stage 5-style fishing wiring (no Collection write path needed for cancel).
                fishingUi.Configure(fishing);
                fishing.Configure(null, catalog, fishingUi, null);
                fishing.DebugSetTiming(10f, 10f, 10f);
                ui.Configure(collection, catalog);
                ui.WireGameplay(fishing);

                // A: Menu — entry hidden
                ui.SetPlayEntryVisible(false);
                if (ui.IsOpen)
                {
                    Debug.LogError("Must start closed.");
                    return 20;
                }

                // B: Play — entry allowed (button built; visibility gated)
                ui.SetPlayEntryVisible(true);

                // Open from Ready fishing → RequestClose then collection
                if (!fishing.TryOpenFromOcean())
                {
                    Debug.LogError("TryOpenFromOcean failed.");
                    return 21;
                }

                fishing.RequestCast();
                if (fishing.State != FishingFsmState.Wait)
                {
                    Debug.LogError("Expected Wait after cast.");
                    return 22;
                }

                ui.Open();
                if (!ui.IsOpen)
                {
                    Debug.LogError("Collection must open.");
                    return 23;
                }

                if (fishing.IsOpen || fishing.State != FishingFsmState.Closed)
                {
                    Debug.LogError("Fishing must close when collection opens.");
                    return 24;
                }

                if (fishing.LastOutcome != FishingAttemptOutcome.Cancelled)
                {
                    Debug.LogError("Wait cancel must set Cancelled outcome.");
                    return 25;
                }

                if (fishing.CatchProgressAppliedThisAttempt)
                {
                    Debug.LogError("Wait cancel must not apply catch progress.");
                    return 29;
                }

                if (save.fishing.totalCatches != 0 || collection.GetDiscoveryCount("fish_mackerel") != 0)
                {
                    Debug.LogError("Cancel must not mutate save progress.");
                    return 26;
                }

                ui.Close();
                if (ui.IsOpen)
                {
                    Debug.LogError("Close failed.");
                    return 13;
                }

                // Empty catalog
                emptyCatalog = ScriptableObject.CreateInstance<FishCatalog>();
                emptyCatalog.Configure(Array.Empty<FishData>());
                if (CollectionFishListBuilder.Build(emptyCatalog, collection).Count != 0)
                {
                    Debug.LogError("Empty catalog must yield 0 rows.");
                    return 14;
                }

                var rows = CollectionFishListBuilder.Build(catalog, collection);
                if (rows.Count == 0)
                {
                    Debug.LogError("Expected catalog rows.");
                    return 3;
                }

                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Discovered || rows[i].Title != "???")
                    {
                        Debug.LogError("Empty collection must show silhouettes only.");
                        return 4;
                    }
                }

                var firstId = rows[0].FishId;
                if (!collection.RegisterDiscovery(firstId))
                {
                    Debug.LogError("RegisterDiscovery failed.");
                    return 5;
                }

                // E: reopen re-reads counts
                ui.Open();
                var after = CollectionFishListBuilder.Build(catalog, collection);
                var discoveredCount = 0;
                var found = false;
                for (var i = 0; i < after.Count; i++)
                {
                    if (after[i].Discovered)
                    {
                        discoveredCount++;
                    }

                    if (after[i].FishId == firstId)
                    {
                        found = true;
                        if (!after[i].Discovered || after[i].CountLabel != "×1")
                        {
                            Debug.LogError("Discovered row wrong after reopen.");
                            return 6;
                        }
                    }
                }

                if (!found || discoveredCount != 1)
                {
                    Debug.LogError($"Expected 1 discovered, got {discoveredCount}");
                    return 7;
                }

                collection.RegisterDiscovery(firstId);
                ui.Close();
                ui.Open();
                var again = CollectionFishListBuilder.Build(catalog, collection);
                for (var i = 0; i < again.Count; i++)
                {
                    if (again[i].FishId == firstId && again[i].CountLabel != "×2")
                    {
                        Debug.LogError("Expected ×2 after second discovery + reopen.");
                        return 8;
                    }
                }

                // F: leave play — entry hidden, panel closed
                ui.SetPlayEntryVisible(false);
                if (ui.IsOpen)
                {
                    Debug.LogError("Leaving play must close collection.");
                    return 27;
                }

                // Duplicate / blank fishId
                dupA = ScriptableObject.CreateInstance<FishData>();
                dupA.fishId = "fish_dup_test";
                dupA.displayName = "First";
                dupB = ScriptableObject.CreateInstance<FishData>();
                dupB.fishId = "fish_dup_test";
                dupB.displayName = "Second";
                var blank = ScriptableObject.CreateInstance<FishData>();
                blank.fishId = "   ";
                dupCatalog = ScriptableObject.CreateInstance<FishCatalog>();
                dupCatalog.Configure(new[] { dupA, null, blank, dupB });
                var dupRows = CollectionFishListBuilder.Build(dupCatalog, null);
                if (dupRows.Count != 1 || dupRows[0].FishId != "fish_dup_test")
                {
                    Debug.LogError("Duplicate/empty fishId handling failed.");
                    return 9;
                }

                if (save.version != SaveMigrationService.CurrentVersion)
                {
                    Debug.LogError("Save version must stay CurrentVersion.");
                    return 28;
                }

                Debug.Log(
                    $"[AFK Island] Collection UI Stage 8B smoke PASS rows={rows.Count} " +
                    $"opened={firstId}");
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] Collection UI Stage 8B smoke exception: " + e);
                return 99;
            }
            finally
            {
                if (host != null)
                {
                    UnityEngine.Object.DestroyImmediate(host);
                }

                if (emptyCatalog != null)
                {
                    UnityEngine.Object.DestroyImmediate(emptyCatalog);
                }

                if (dupCatalog != null)
                {
                    UnityEngine.Object.DestroyImmediate(dupCatalog);
                }

                if (dupA != null)
                {
                    UnityEngine.Object.DestroyImmediate(dupA);
                }

                if (dupB != null)
                {
                    UnityEngine.Object.DestroyImmediate(dupB);
                }
            }
        }
    }
}
#endif
