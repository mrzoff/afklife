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
    /// <summary>Stage 8A: CollectionFishListBuilder + CollectionUI.Configure/Open/Close smoke.</summary>
    public static class CollectionUiSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "collection_ui_smoke_result.txt");

        [MenuItem("AFK Island/Smoke Collection UI Stage 8A")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Collection UI Stage 8A smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Collection UI Stage 8A smoke OK.");
        }

        public static void RunBatch()
        {
            Debug.Log("[AFK Island] Collection UI Stage 8A smoke START");
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
                var save = new GameSaveData
                {
                    version = SaveMigrationService.CurrentVersion,
                    collection = new CollectionSaveData(),
                    fishing = new FishingSaveData()
                };
                SaveMigrationService.EnsureCollectionAndFishingDefaults(save);
                collection.Bind(save);

                ui.Configure(collection, catalog);
                if (ui.IsOpen)
                {
                    Debug.LogError("UI must be closed after Configure.");
                    return 11;
                }

                ui.Open();
                if (!ui.IsOpen)
                {
                    Debug.LogError("UI must be open after Open().");
                    return 12;
                }

                ui.Close();
                if (ui.IsOpen)
                {
                    Debug.LogError("UI must be closed after Close().");
                    return 13;
                }

                // Empty catalog → 0 rows, no throw
                emptyCatalog = ScriptableObject.CreateInstance<FishCatalog>();
                emptyCatalog.Configure(Array.Empty<FishData>());
                var emptyRows = CollectionFishListBuilder.Build(emptyCatalog, collection);
                if (emptyRows.Count != 0)
                {
                    Debug.LogError("Empty catalog must yield 0 rows.");
                    return 14;
                }

                // Empty collection → all silhouettes
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

                // Open again re-reads CollectionService
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
                        if (!after[i].Discovered ||
                            after[i].Title == "???" ||
                            after[i].CountLabel != "×1" ||
                            string.IsNullOrEmpty(after[i].RarityLabel) ||
                            after[i].RarityLabel == "—")
                        {
                            Debug.LogError(
                                $"Discovered row wrong: title={after[i].Title} " +
                                $"rarity={after[i].RarityLabel} count={after[i].CountLabel}");
                            return 6;
                        }
                    }
                }

                if (!found || discoveredCount != 1)
                {
                    Debug.LogError($"Expected 1 discovered, got {discoveredCount}, found={found}");
                    return 7;
                }

                collection.RegisterDiscovery(firstId);
                var again = CollectionFishListBuilder.Build(catalog, collection);
                for (var i = 0; i < again.Count; i++)
                {
                    if (again[i].FishId == firstId && again[i].CountLabel != "×2")
                    {
                        Debug.LogError("Expected ×2 after second discovery.");
                        return 8;
                    }
                }

                // Duplicate fishId: first wins, single row
                dupA = ScriptableObject.CreateInstance<FishData>();
                dupA.fishId = "fish_dup_test";
                dupA.displayName = "First";
                dupA.fallbackColor = Color.red;
                dupB = ScriptableObject.CreateInstance<FishData>();
                dupB.fishId = "fish_dup_test";
                dupB.displayName = "Second";
                dupB.fallbackColor = Color.blue;
                var blank = ScriptableObject.CreateInstance<FishData>();
                blank.fishId = "   ";
                blank.displayName = "Blank";
                dupCatalog = ScriptableObject.CreateInstance<FishCatalog>();
                dupCatalog.Configure(new[] { dupA, null, blank, dupB });
                var dupRows = CollectionFishListBuilder.Build(dupCatalog, null);
                if (dupRows.Count != 1 || dupRows[0].Title != "???" || dupRows[0].FishId != "fish_dup_test")
                {
                    Debug.LogError(
                        $"Duplicate/empty fishId handling failed: count={dupRows.Count}");
                    return 9;
                }

                // Null collection → all undiscovered
                var nullSafe = CollectionFishListBuilder.Build(catalog, null);
                if (nullSafe.Count != rows.Count || nullSafe[0].Discovered)
                {
                    Debug.LogError("Null collection should yield undiscovered rows.");
                    return 10;
                }

                ui.Close();
                Debug.Log(
                    $"[AFK Island] Collection UI Stage 8A smoke PASS rows={rows.Count} " +
                    $"opened={firstId}");
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] Collection UI Stage 8A smoke exception: " + e);
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
