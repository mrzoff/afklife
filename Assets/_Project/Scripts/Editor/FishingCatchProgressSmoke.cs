#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using AfkIsland.Collection;
using AfkIsland.Fishing;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>Contract 0.3 Stage 6: atomic Success → discovery + totalCatches.</summary>
    public static class FishingCatchProgressSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "fishing_catch_progress_smoke_result.txt");

        [MenuItem("AFK Island/Smoke Fishing Catch Progress 0.3")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("FishingCatchProgress smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] FishingCatchProgress smoke OK.");
        }

        public static void RunBatch()
        {
            Debug.Log("[AFK Island] FishingCatchProgress smoke START");
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
                Debug.LogError("[AFK Island] Failed to write catch-progress smoke result: " + e.Message);
            }

            Debug.Log(code == 0
                ? "[AFK Island] FishingCatchProgress smoke OK."
                : "[AFK Island] FishingCatchProgress smoke FAIL code=" + code);
            EditorApplication.Exit(code);
        }

        private static int RunInternal()
        {
            GameObject host = null;
            var created = new List<UnityEngine.Object>();
            try
            {
                host = new GameObject("CatchProgressSmokeHost");
                var ui = host.AddComponent<FishingUI>();
                var controller = host.AddComponent<FishingController>();
                var collection = host.AddComponent<CollectionService>();

                var catalog = Resources.Load<FishCatalog>("FishCatalog");
                if (catalog == null)
                {
                    Debug.LogError("FishCatalog missing.");
                    return 2;
                }

                var save = new GameSaveData
                {
                    version = 8,
                    lastSaveUtc = 50,
                    fishing = new FishingSaveData { totalCatches = 0 },
                    collection = new CollectionSaveData
                    {
                        entries = new List<CollectionEntrySaveData>()
                    },
                    weather = new WeatherSaveData
                    {
                        weatherId = WeatherSimulation.Sunny,
                        timeOfDay01 = 0.5f
                    }
                };
                SaveMigrationService.EnsureCollectionAndFishingDefaults(save);

                var saveGo = new GameObject("CatchProgressSave");
                created.Add(saveGo);
                var saveService = saveGo.AddComponent<SaveService>();
                saveService.SetAuthoritative(save);
                collection.Configure(saveService);

                controller.Configure(
                    saveService,
                    catalog,
                    ui,
                    collection,
                    new DeterministicRng(2026));
                ui.Configure(controller);
                controller.DebugSetTiming(0.05f, 0.05f, 5f);

                // --- Success apply once ---
                controller.TryOpenFromOcean();
                controller.RequestCast();
                controller.DebugForceBiteNow();
                controller.RequestReact();

                if (controller.LastOutcome != FishingAttemptOutcome.Success ||
                    controller.LastSelectedFish == null ||
                    !controller.CatchProgressAppliedThisAttempt)
                {
                    Debug.LogError(
                        $"Success apply failed (outcome={controller.LastOutcome}, " +
                        $"fish={controller.LastSelectedFish?.fishId}, applied={controller.CatchProgressAppliedThisAttempt}).");
                    return 3;
                }

                var fishId = controller.LastSelectedFish.fishId;
                if (save.fishing.totalCatches != 1 ||
                    collection.GetDiscoveryCount(fishId) != 1 ||
                    !collection.IsDiscovered(fishId))
                {
                    Debug.LogError(
                        $"Progress mismatch catches={save.fishing.totalCatches} " +
                        $"discovery={collection.GetDiscoveryCount(fishId)}.");
                    return 4;
                }

                // --- Repeated Result callbacks must not double-apply ---
                controller.RequestReact();
                controller.RequestReact();
                if (save.fishing.totalCatches != 1 || collection.GetDiscoveryCount(fishId) != 1)
                {
                    Debug.LogError("Repeated react doubled progress.");
                    return 5;
                }

                // Direct second TryApply without new attempt gate would bump — controller gate is required.
                // Simulate controller gate: flag already true; calling Apply path again via react is no-op (tested).

                // --- Second successful attempt increments both again ---
                controller.RequestAgain();
                controller.RequestCast();
                controller.DebugForceBiteNow();
                controller.RequestReact();
                if (!controller.CatchProgressAppliedThisAttempt ||
                    save.fishing.totalCatches != 2)
                {
                    Debug.LogError(
                        $"Second catch not applied (catches={save.fishing.totalCatches}, " +
                        $"applied={controller.CatchProgressAppliedThisAttempt}).");
                    return 6;
                }

                var secondId = controller.LastSelectedFish.fishId;
                var expectedDiscovery = string.Equals(secondId, fishId, StringComparison.Ordinal) ? 2 : 1;
                if (collection.GetDiscoveryCount(secondId) != expectedDiscovery)
                {
                    Debug.LogError(
                        $"Second discovery wrong for {secondId}: {collection.GetDiscoveryCount(secondId)}.");
                    return 7;
                }

                // --- Miss does not apply ---
                var catchesBeforeMiss = save.fishing.totalCatches;
                var entriesBeforeMiss = collection.GetEntries().Count;
                controller.RequestAgain();
                controller.RequestCast();
                controller.DebugForceBiteNow();
                controller.DebugForceBiteTimeout();
                if (controller.LastOutcome != FishingAttemptOutcome.Miss ||
                    controller.CatchProgressAppliedThisAttempt ||
                    save.fishing.totalCatches != catchesBeforeMiss ||
                    collection.GetEntries().Count != entriesBeforeMiss)
                {
                    Debug.LogError("Miss must not change progress.");
                    return 8;
                }

                // --- Empty pool does not apply ---
                var empty = ScriptableObject.CreateInstance<FishCatalog>();
                created.Add(empty);
                empty.Configure(Array.Empty<FishData>());
                controller.SetCatalog(empty);
                controller.RequestAgain();
                controller.RequestCast();
                controller.DebugForceBiteNow();
                controller.RequestReact();
                if (controller.LastOutcome != FishingAttemptOutcome.EmptyPool ||
                    controller.CatchProgressAppliedThisAttempt ||
                    save.fishing.totalCatches != catchesBeforeMiss)
                {
                    Debug.LogError("EmptyPool must not change progress.");
                    return 9;
                }

                // --- Atomic helper: discovery fail ⇒ catches unchanged ---
                var bare = new GameSaveData { version = 8, fishing = new FishingSaveData { totalCatches = 9 } };
                SaveMigrationService.EnsureCollectionAndFishingDefaults(bare);
                var bareGo = new GameObject("BareSave");
                created.Add(bareGo);
                var bareSave = bareGo.AddComponent<SaveService>();
                bareSave.SetAuthoritative(bare);
                var bareCollection = bareGo.AddComponent<CollectionService>();
                bareCollection.Configure(bareSave);
                if (FishingCatchProgress.TryApply(bareSave, bareCollection, "   ") ||
                    bare.fishing.totalCatches != 9)
                {
                    Debug.LogError("Invalid fishId must not bump totalCatches.");
                    return 10;
                }

                if (!FishingCatchProgress.TryApply(bareSave, bareCollection, "fish_mackerel") ||
                    bare.fishing.totalCatches != 10 ||
                    bareCollection.GetDiscoveryCount("fish_mackerel") != 1)
                {
                    Debug.LogError("Direct TryApply failed.");
                    return 11;
                }

                // Second direct apply (no controller gate) is allowed by helper — documents caller duty.
                if (!FishingCatchProgress.TryApply(bareSave, bareCollection, "fish_mackerel") ||
                    bare.fishing.totalCatches != 11 ||
                    bareCollection.GetDiscoveryCount("fish_mackerel") != 2)
                {
                    Debug.LogError("Direct TryApply second call should advance (caller gates idempotency).");
                    return 12;
                }

                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] FishingCatchProgress smoke exception: " + e);
                return 1;
            }
            finally
            {
                if (host != null)
                {
                    UnityEngine.Object.DestroyImmediate(host);
                }

                foreach (var o in created)
                {
                    if (o != null)
                    {
                        UnityEngine.Object.DestroyImmediate(o);
                    }
                }
            }
        }
    }
}
#endif
