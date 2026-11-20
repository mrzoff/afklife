#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using AfkIsland.Fishing;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>Contract 0.3 Stage 5: FishingController FSM + isolation (no Save/Collection writes).</summary>
    public static class FishingControllerSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "fishing_controller_smoke_result.txt");

        [MenuItem("AFK Island/Smoke Fishing Controller 0.3")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("FishingController smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] FishingController smoke OK.");
        }

        public static void RunBatch()
        {
            Debug.Log("[AFK Island] FishingController smoke START");
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
                Debug.LogError("[AFK Island] Failed to write FishingController smoke result: " + e.Message);
            }

            Debug.Log(code == 0
                ? "[AFK Island] FishingController smoke OK."
                : "[AFK Island] FishingController smoke FAIL code=" + code);
            EditorApplication.Exit(code);
        }

        private static int RunInternal()
        {
            GameObject host = null;
            var created = new List<UnityEngine.Object>();
            try
            {
                host = new GameObject("FishingControllerSmokeHost");
                var ui = host.AddComponent<FishingUI>();
                var controller = host.AddComponent<FishingController>();
                var catalog = Resources.Load<FishCatalog>("FishCatalog");
                if (catalog == null)
                {
                    Debug.LogError("FishCatalog missing in Resources.");
                    return 2;
                }

                var save = new GameSaveData
                {
                    version = 8,
                    lastSaveUtc = 100,
                    fishing = new FishingSaveData { totalCatches = 4 },
                    collection = new CollectionSaveData
                    {
                        entries = new List<CollectionEntrySaveData>
                        {
                            new CollectionEntrySaveData
                            {
                                entryId = "fish_mackerel",
                                discovered = true,
                                discoveryCount = 2,
                                firstDiscoveredUtc = 1,
                                lastDiscoveredUtc = 2
                            }
                        }
                    },
                    weather = new WeatherSaveData
                    {
                        weatherId = WeatherSimulation.Sunny,
                        timeOfDay01 = 0.5f
                    }
                };
                SaveMigrationService.EnsureCollectionAndFishingDefaults(save);
                var beforeJson = JsonUtility.ToJson(save);

                // Minimal SaveService stub via SetAuthoritative on a live component.
                var saveGo = new GameObject("SmokeSave");
                created.Add(saveGo);
                var saveService = saveGo.AddComponent<SaveService>();
                saveService.SetAuthoritative(save);

                // Stage 5 isolation: no CollectionService → success must not mutate Save.
                controller.Configure(saveService, catalog, ui, collection: null, rng: new DeterministicRng(77));
                ui.Configure(controller);
                controller.DebugSetTiming(0.05f, 0.05f, 5f);

                // A-ish: closed → open once; second open ignored
                if (!controller.TryOpenFromOcean() || controller.State != FishingFsmState.Ready)
                {
                    Debug.LogError("OpenFromOcean failed.");
                    return 3;
                }

                if (controller.TryOpenFromOcean())
                {
                    Debug.LogError("Second open must be ignored.");
                    return 4;
                }

                // B: Cast → Wait; cast ignored in Wait; close cancels
                controller.RequestCast();
                if (controller.State != FishingFsmState.Wait)
                {
                    Debug.LogError("Cast must enter Wait.");
                    return 5;
                }

                var attempt = controller.AttemptSerial;
                controller.RequestCast();
                if (controller.AttemptSerial != attempt || controller.State != FishingFsmState.Wait)
                {
                    Debug.LogError("Cast during Wait must be ignored.");
                    return 6;
                }

                controller.RequestClose();
                if (controller.State != FishingFsmState.Closed ||
                    controller.LastOutcome != FishingAttemptOutcome.Cancelled ||
                    controller.SimulationInvokedThisAttempt)
                {
                    Debug.LogError("Close during Wait must cancel without simulation.");
                    return 7;
                }

                if (JsonUtility.ToJson(save) != beforeJson)
                {
                    Debug.LogError("Save mutated on cancel.");
                    return 8;
                }

                // C/D: Bite success → simulation once
                controller.TryOpenFromOcean();
                controller.RequestCast();
                controller.DebugForceBiteNow();
                if (controller.State != FishingFsmState.Bite)
                {
                    Debug.LogError("Force bite failed.");
                    return 9;
                }

                controller.RequestReact();
                if (controller.State != FishingFsmState.Result ||
                    !controller.SimulationInvokedThisAttempt)
                {
                    Debug.LogError("Success react must invoke simulation and enter Result.");
                    return 10;
                }

                if (controller.LastOutcome == FishingAttemptOutcome.Success &&
                    controller.LastSelectedFish == null)
                {
                    Debug.LogError("Success outcome without fish.");
                    return 11;
                }

                // G: repeated react ignored
                var outcome = controller.LastOutcome;
                var fish = controller.LastSelectedFish;
                controller.RequestReact();
                controller.RequestReact();
                if (controller.LastOutcome != outcome || controller.LastSelectedFish != fish)
                {
                    Debug.LogError("Repeated react mutated result.");
                    return 12;
                }

                if (JsonUtility.ToJson(save) != beforeJson ||
                    save.fishing.totalCatches != 4 ||
                    save.collection.entries[0].discoveryCount != 2)
                {
                    Debug.LogError("I: Save/Collection changed after success (Stage 5 isolation).");
                    return 13;
                }

                // E: Miss path — no simulation
                controller.RequestAgain();
                controller.RequestCast();
                controller.DebugForceBiteNow();
                controller.DebugForceBiteTimeout();
                if (controller.State != FishingFsmState.Result ||
                    controller.LastOutcome != FishingAttemptOutcome.Miss ||
                    controller.SimulationInvokedThisAttempt ||
                    controller.LastSelectedFish != null)
                {
                    Debug.LogError("E: miss must not select fish / invoke simulation.");
                    return 14;
                }

                // Close during Bite cancels
                controller.RequestAgain();
                controller.RequestCast();
                controller.DebugForceBiteNow();
                controller.RequestClose();
                if (controller.State != FishingFsmState.Closed ||
                    controller.LastOutcome != FishingAttemptOutcome.Cancelled ||
                    controller.SimulationInvokedThisAttempt)
                {
                    Debug.LogError("Close during Bite must cancel without simulation.");
                    return 15;
                }

                // F: empty pool
                var empty = ScriptableObject.CreateInstance<FishCatalog>();
                created.Add(empty);
                empty.Configure(Array.Empty<FishData>());
                controller.SetCatalog(empty);
                controller.SetRng(new DeterministicRng(3));
                controller.TryOpenFromOcean();
                controller.RequestCast();
                controller.DebugForceBiteNow();
                controller.RequestReact();
                if (controller.LastOutcome != FishingAttemptOutcome.EmptyPool ||
                    controller.LastSelectedFish != null ||
                    string.IsNullOrWhiteSpace(controller.LastResultMessage))
                {
                    Debug.LogError("F: empty pool safe result failed.");
                    return 16;
                }

                if (JsonUtility.ToJson(save) != beforeJson)
                {
                    Debug.LogError("I: Save mutated after empty-pool result.");
                    return 17;
                }

                // Stage 10A: Pond open uses pond pool; source resets on close
                controller.RequestClose();
                controller.SetCatalog(catalog);
                controller.SetRng(new DeterministicRng(91));
                if (!controller.TryOpenFromPond() ||
                    controller.ActiveWaterSource != FishingWaterSource.Pond ||
                    controller.State != FishingFsmState.Ready)
                {
                    Debug.LogError("10A: TryOpenFromPond failed.");
                    return 18;
                }

                if (controller.TryOpenFromOcean() || controller.TryOpenFromPond())
                {
                    Debug.LogError("10A: second open while Ready must be ignored.");
                    return 19;
                }

                controller.RequestCast();
                controller.DebugForceBiteNow();
                controller.RequestReact();
                if (controller.State != FishingFsmState.Result ||
                    !controller.SimulationInvokedThisAttempt)
                {
                    Debug.LogError("10A: pond success path must invoke simulation.");
                    return 20;
                }

                if (controller.LastOutcome == FishingAttemptOutcome.Success)
                {
                    if (controller.LastSelectedFish == null ||
                        !controller.LastSelectedFish.IsPondEligible ||
                        controller.LastSelectedFish.waterType == FishWaterType.Ocean)
                    {
                        Debug.LogError("10A: pond success selected non-pond fish.");
                        return 21;
                    }
                }

                if (JsonUtility.ToJson(save) != beforeJson)
                {
                    Debug.LogError("10A: Save mutated on pond success without CollectionService.");
                    return 22;
                }

                controller.RequestClose();
                if (controller.ActiveWaterSource != FishingWaterSource.None || controller.IsOpen)
                {
                    Debug.LogError("10A: close must clear water source.");
                    return 23;
                }

                // Pond empty pool
                controller.SetCatalog(empty);
                controller.SetRng(new DeterministicRng(4));
                controller.TryOpenFromPond();
                controller.RequestCast();
                controller.DebugForceBiteNow();
                controller.RequestReact();
                if (controller.LastOutcome != FishingAttemptOutcome.EmptyPool ||
                    controller.LastSelectedFish != null)
                {
                    Debug.LogError("10A: empty pond pool safe result failed.");
                    return 24;
                }

                controller.RequestClose();
                controller.SetCatalog(catalog);

                // A: Ocean hit-proxy helper does not throw / touch pond
                var ocean = GameObject.Find("Ocean");
                if (ocean == null)
                {
                    var tempOcean = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    tempOcean.name = "Ocean";
                    created.Add(tempOcean);
                }

                FishingOceanInput.EnsureOceanHitProxy();
                var oceanGo = GameObject.Find("Ocean");
                if (oceanGo == null || oceanGo.GetComponent<MeshCollider>() == null)
                {
                    Debug.LogError("A: Ocean hit-proxy missing MeshCollider.");
                    return 25;
                }

                // Stage 10B: Pond hit-proxy helper does not throw / touch ocean
                var pond = GameObject.Find("WaterSource");
                if (pond == null)
                {
                    var tempPond = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    tempPond.name = "WaterSource";
                    created.Add(tempPond);
                }

                if (!FishingPondInput.EnsurePondHitProxy() ||
                    !FishingPondInput.TryGetPondHitProxy(out var pondProxy))
                {
                    Debug.LogError("10B: PondHitProxy child collider missing.");
                    return 27;
                }

                var pondGo = GameObject.Find("WaterSource");
                if (pondGo == null ||
                    ReferenceEquals(pondGo, oceanGo) ||
                    pondProxy.parent != pondGo.transform)
                {
                    Debug.LogError("10B: Pond and Ocean proxies must be distinct objects.");
                    return 28;
                }

                if (pondGo.GetComponent<MeshCollider>() != null)
                {
                    Debug.LogError("10B: WaterSource root must not keep MeshCollider (child proxy only).");
                    return 29;
                }

                controller.RequestClose();
                if (ui.IsVisible || controller.IsOpen)
                {
                    Debug.LogError("H: UI should hide when closed.");
                    return 26;
                }

                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] FishingController smoke exception: " + e);
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
