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
    /// <summary>Contract 0.3 Stage 4: FishingSimulation pool + weighted pick (no Save/Collection).</summary>
    public static class FishingSimulationSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "fishing_simulation_smoke_result.txt");

        [MenuItem("AFK Island/Smoke Fishing Simulation 0.3")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("FishingSimulation smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] FishingSimulation smoke OK.");
        }

        public static void RunBatch()
        {
            Debug.Log("[AFK Island] FishingSimulation smoke START");
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
                Debug.LogError("[AFK Island] Failed to write FishingSimulation smoke result: " + e.Message);
            }

            Debug.Log(code == 0
                ? "[AFK Island] FishingSimulation smoke OK."
                : "[AFK Island] FishingSimulation smoke FAIL code=" + code);
            EditorApplication.Exit(code);
        }

        private static int RunInternal()
        {
            var created = new List<UnityEngine.Object>();
            try
            {
                // --- A/B/C/D filters via ephemeral catalog ---
                var oceanAny = MakeFish(created, "t_ocean", FishWaterType.Ocean, 1f, null, FishTimeCondition.Any);
                var bothAny = MakeFish(created, "t_both", FishWaterType.Both, 1f, null, FishTimeCondition.Any);
                var pond = MakeFish(created, "t_pond", FishWaterType.Pond, 1f, null, FishTimeCondition.Any);
                pond.pondEligible = true;
                var sunnyOnly = MakeFish(
                    created, "t_sunny", FishWaterType.Ocean, 1f,
                    new[] { WeatherSimulation.Sunny }, FishTimeCondition.Any);
                var dayOnly = MakeFish(
                    created, "t_day", FishWaterType.Ocean, 1f, null, FishTimeCondition.Day);
                var zeroW = MakeFish(created, "t_zero", FishWaterType.Ocean, 0f, null, FishTimeCondition.Any);
                var negW = MakeFish(created, "t_neg", FishWaterType.Ocean, -1f, null, FishTimeCondition.Any);
                var nanW = MakeFish(created, "t_nan", FishWaterType.Ocean, float.NaN, null, FishTimeCondition.Any);
                var infW = MakeFish(created, "t_inf", FishWaterType.Ocean, float.PositiveInfinity, null, FishTimeCondition.Any);

                var filterCatalog = ScriptableObject.CreateInstance<FishCatalog>();
                created.Add(filterCatalog);
                filterCatalog.Configure(new[]
                {
                    oceanAny, bothAny, pond, sunnyOnly, dayOnly, zeroW, negW, nanW, infW
                });

                var pool = new List<FishData>();
                FishingSimulation.CollectOceanCatchPool(
                    filterCatalog, WeatherSimulation.Sunny, 0.5f, pool);

                if (!pool.Exists(f => f.fishId == "t_ocean") ||
                    !pool.Exists(f => f.fishId == "t_both") ||
                    pool.Exists(f => f.fishId == "t_pond"))
                {
                    Debug.LogError("A: Ocean/Both in, Pond out.");
                    return 2;
                }

                if (!pool.Exists(f => f.fishId == "t_sunny"))
                {
                    Debug.LogError("B: sunny-matching fish missing on sunny.");
                    return 3;
                }

                FishingSimulation.CollectOceanCatchPool(
                    filterCatalog, WeatherSimulation.Rain, 0.5f, pool);
                if (pool.Exists(f => f.fishId == "t_sunny") ||
                    !pool.Exists(f => f.fishId == "t_ocean"))
                {
                    Debug.LogError("B: weather filter wrong on rain.");
                    return 4;
                }

                FishingSimulation.CollectOceanCatchPool(
                    filterCatalog, WeatherSimulation.Sunny, 0.5f, pool);
                if (!pool.Exists(f => f.fishId == "t_day"))
                {
                    Debug.LogError("C: Day fish missing at noon.");
                    return 5;
                }

                FishingSimulation.CollectOceanCatchPool(
                    filterCatalog, WeatherSimulation.Sunny, 0.05f, pool);
                if (pool.Exists(f => f.fishId == "t_day"))
                {
                    Debug.LogError("C: Day fish must be out at night.");
                    return 6;
                }

                if (pool.Exists(f => f.fishId == "t_zero") ||
                    pool.Exists(f => f.fishId == "t_neg") ||
                    pool.Exists(f => f.fishId == "t_nan") ||
                    pool.Exists(f => f.fishId == "t_inf"))
                {
                    Debug.LogError("D: invalid weights must be excluded.");
                    return 7;
                }

                if (!FishingSimulation.IsValidCatchWeight(1f) ||
                    FishingSimulation.IsValidCatchWeight(0f) ||
                    FishingSimulation.IsValidCatchWeight(-0.1f) ||
                    FishingSimulation.IsValidCatchWeight(float.NaN) ||
                    FishingSimulation.IsValidCatchWeight(float.PositiveInfinity))
                {
                    Debug.LogError("D: IsValidCatchWeight mismatch.");
                    return 8;
                }

                // --- E: selection ---
                var singleCatalog = ScriptableObject.CreateInstance<FishCatalog>();
                created.Add(singleCatalog);
                singleCatalog.Configure(new[] { oceanAny });
                var rng = new DeterministicRng(42);
                if (!FishingSimulation.TrySelectOceanCatch(
                        singleCatalog, WeatherSimulation.Sunny, 0.5f, out var one, rng) ||
                    one != oceanAny)
                {
                    Debug.LogError("E: single candidate must always return that fish.");
                    return 9;
                }

                var emptyCatalog = ScriptableObject.CreateInstance<FishCatalog>();
                created.Add(emptyCatalog);
                emptyCatalog.Configure(Array.Empty<FishData>());
                if (FishingSimulation.TrySelectOceanCatch(
                        emptyCatalog, WeatherSimulation.Sunny, 0.5f, out var none, new DeterministicRng(1)) ||
                    none != null)
                {
                    Debug.LogError("E: empty pool must return false/null.");
                    return 10;
                }

                // Pond-only catalog → empty ocean pool
                var pondCatalog = ScriptableObject.CreateInstance<FishCatalog>();
                created.Add(pondCatalog);
                pondCatalog.Configure(new[] { pond });
                if (FishingSimulation.TrySelectOceanCatch(
                        pondCatalog, WeatherSimulation.Sunny, 0.5f, out _, new DeterministicRng(2)))
                {
                    Debug.LogError("E: pond-only catalog must not select.");
                    return 11;
                }

                // Weighted A:1 B:0.5 C:0.5 over many samples
                var a = MakeFish(created, "w_a", FishWaterType.Ocean, 1f, null, FishTimeCondition.Any);
                var b = MakeFish(created, "w_b", FishWaterType.Ocean, 0.5f, null, FishTimeCondition.Any);
                var c = MakeFish(created, "w_c", FishWaterType.Ocean, 0.5f, null, FishTimeCondition.Any);
                var weightCatalog = ScriptableObject.CreateInstance<FishCatalog>();
                created.Add(weightCatalog);
                weightCatalog.Configure(new[] { a, b, c });

                const int samples = 4000;
                var counts = new Dictionary<string, int>
                {
                    ["w_a"] = 0,
                    ["w_b"] = 0,
                    ["w_c"] = 0
                };
                var weightRng = new DeterministicRng(12345);
                for (var i = 0; i < samples; i++)
                {
                    if (!FishingSimulation.TrySelectOceanCatch(
                            weightCatalog, WeatherSimulation.Sunny, 0.5f, out var pick, weightRng) ||
                        pick == null)
                    {
                        Debug.LogError("E: weighted pick failed mid-loop.");
                        return 12;
                    }

                    counts[pick.fishId]++;
                }

                // Expected ~50/25/25. Allow ±8pp absolute.
                var pa = counts["w_a"] / (double)samples;
                var pb = counts["w_b"] / (double)samples;
                var pc = counts["w_c"] / (double)samples;
                if (Math.Abs(pa - 0.50) > 0.08 ||
                    Math.Abs(pb - 0.25) > 0.08 ||
                    Math.Abs(pc - 0.25) > 0.08)
                {
                    Debug.LogError(
                        $"E: weight proportions off A={pa:F3} B={pb:F3} C={pc:F3} (n={samples}).");
                    return 13;
                }

                if (float.IsNaN(0.5f) == false &&
                    FishingSimulation.TrySelectOceanCatch(
                        weightCatalog, WeatherSimulation.Sunny, float.NaN, out _, new DeterministicRng(9)))
                {
                    Debug.LogError("E: NaN timeOfDay must fail safely.");
                    return 14;
                }

                if (FishingSimulation.TrySelectOceanCatch(
                        null, WeatherSimulation.Sunny, 0.5f, out _, new DeterministicRng(9)))
                {
                    Debug.LogError("E: null catalog must fail.");
                    return 15;
                }

                // --- F: isolation ---
                var save = new GameSaveData
                {
                    version = 8,
                    lastSaveUtc = 1,
                    fishing = new FishingSaveData { totalCatches = 7 },
                    collection = new CollectionSaveData
                    {
                        entries = new List<CollectionEntrySaveData>
                        {
                            new CollectionEntrySaveData
                            {
                                entryId = "fish_mackerel",
                                discovered = true,
                                discoveryCount = 3,
                                firstDiscoveredUtc = 10,
                                lastDiscoveredUtc = 20
                            }
                        }
                    }
                };
                var beforeJson = JsonUtility.ToJson(save);
                var catalogCountBefore = weightCatalog.Fish.Count;

                FishingSimulation.TrySelectOceanCatch(
                    weightCatalog, WeatherSimulation.Sunny, 0.5f, out _, new DeterministicRng(99));

                if (JsonUtility.ToJson(save) != beforeJson ||
                    save.fishing.totalCatches != 7 ||
                    save.collection.entries.Count != 1 ||
                    save.collection.entries[0].discoveryCount != 3 ||
                    weightCatalog.Fish.Count != catalogCountBefore)
                {
                    Debug.LogError("F: Save/Collection/Catalog mutated by FishingSimulation.");
                    return 16;
                }

                // Resource catalog sanity (optional presence)
                var res = Resources.Load<FishCatalog>("FishCatalog");
                if (res != null)
                {
                    FishingSimulation.CollectOceanCatchPool(
                        res, WeatherSimulation.Sunny, 0.5f, pool);
                    if (pool.Exists(f => f != null && f.waterType == FishWaterType.Pond))
                    {
                        Debug.LogError("F: resource ocean pool contained Pond.");
                        return 17;
                    }

                    if (pool.Count == 0)
                    {
                        Debug.LogWarning(
                            "Resource FishCatalog ocean pool empty for sunny noon (ok if all filtered).");
                    }
                }

                // --- G: Stage 10 Pond eligibility matrix + selection ---
                var oceanFlag = MakeFish(created, "p_ocean_flag", FishWaterType.Ocean, 1f, null, FishTimeCondition.Any);
                oceanFlag.pondEligible = true;
                var pondBare = MakeFish(created, "p_pond_bare", FishWaterType.Pond, 1f, null, FishTimeCondition.Any);
                pondBare.pondEligible = false;
                var bothNo = MakeFish(created, "p_both_no", FishWaterType.Both, 1f, null, FishTimeCondition.Any);
                bothNo.pondEligible = false;
                var bothYes = MakeFish(created, "p_both_yes", FishWaterType.Both, 1f, null, FishTimeCondition.Any);
                bothYes.pondEligible = true;
                var pondZero = MakeFish(created, "p_pond_zero", FishWaterType.Pond, 0f, null, FishTimeCondition.Any);

                if (oceanFlag.IsPondEligible ||
                    !pondBare.IsPondEligible ||
                    bothNo.IsPondEligible ||
                    !bothYes.IsPondEligible)
                {
                    Debug.LogError("G: FishData.IsPondEligible matrix mismatch.");
                    return 18;
                }

                var pondMatrixCatalog = ScriptableObject.CreateInstance<FishCatalog>();
                created.Add(pondMatrixCatalog);
                pondMatrixCatalog.Configure(new[]
                {
                    oceanFlag, pondBare, bothNo, bothYes, pondZero, oceanAny
                });

                FishingSimulation.CollectPondCatchPool(
                    pondMatrixCatalog, WeatherSimulation.Sunny, 0.5f, pool);
                if (!pool.Exists(f => f.fishId == "p_pond_bare") ||
                    !pool.Exists(f => f.fishId == "p_both_yes") ||
                    pool.Exists(f => f.fishId == "p_both_no") ||
                    pool.Exists(f => f.fishId == "p_ocean_flag") ||
                    pool.Exists(f => f.fishId == "t_ocean") ||
                    pool.Exists(f => f.fishId == "p_pond_zero"))
                {
                    Debug.LogError("G: Pond catch pool filter wrong.");
                    return 19;
                }

                FishingSimulation.CollectOceanCatchPool(
                    pondMatrixCatalog, WeatherSimulation.Sunny, 0.5f, pool);
                if (pool.Exists(f => f.fishId == "p_pond_bare") ||
                    !pool.Exists(f => f.fishId == "p_both_no") ||
                    !pool.Exists(f => f.fishId == "p_both_yes") ||
                    !pool.Exists(f => f.fishId == "p_ocean_flag"))
                {
                    Debug.LogError("G: Ocean pool must keep Both/Ocean and exclude Pond-only.");
                    return 20;
                }

                if (!FishingSimulation.TrySelectPondCatch(
                        pondMatrixCatalog, WeatherSimulation.Sunny, 0.5f, out var pondPick,
                        new DeterministicRng(55)) ||
                    pondPick == null ||
                    !pondPick.IsPondEligible)
                {
                    Debug.LogError("G: TrySelectPondCatch failed on non-empty pool.");
                    return 21;
                }

                var pondOnlyEmptyOcean = ScriptableObject.CreateInstance<FishCatalog>();
                created.Add(pondOnlyEmptyOcean);
                pondOnlyEmptyOcean.Configure(new[] { pondBare });
                if (FishingSimulation.TrySelectOceanCatch(
                        pondOnlyEmptyOcean, WeatherSimulation.Sunny, 0.5f, out _,
                        new DeterministicRng(56)))
                {
                    Debug.LogError("G: pond-only catalog must not ocean-select.");
                    return 22;
                }

                if (!FishingSimulation.TrySelectPondCatch(
                        pondOnlyEmptyOcean, WeatherSimulation.Sunny, 0.5f, out var carpLike,
                        new DeterministicRng(57)) ||
                    carpLike != pondBare)
                {
                    Debug.LogError("G: pond-only catalog must pond-select that fish.");
                    return 23;
                }

                if (res != null)
                {
                    FishingSimulation.CollectPondCatchPool(
                        res, WeatherSimulation.Sunny, 0.5f, pool);
                    if (!pool.Exists(f => f != null && f.fishId == "fish_carp"))
                    {
                        Debug.LogError("G: resource pond pool missing fish_carp.");
                        return 24;
                    }

                    if (pool.Exists(f => f != null && f.waterType == FishWaterType.Ocean))
                    {
                        Debug.LogError("G: resource pond pool contained Ocean-only.");
                        return 25;
                    }
                }

                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] FishingSimulation smoke exception: " + e);
                return 1;
            }
            finally
            {
                foreach (var o in created)
                {
                    if (o != null)
                    {
                        UnityEngine.Object.DestroyImmediate(o);
                    }
                }
            }
        }

        private static FishData MakeFish(
            List<UnityEngine.Object> created,
            string id,
            FishWaterType water,
            float weight,
            string[] weather,
            FishTimeCondition time)
        {
            var fish = ScriptableObject.CreateInstance<FishData>();
            created.Add(fish);
            fish.fishId = id;
            fish.displayName = id;
            fish.waterType = water;
            fish.catchWeight = weight;
            fish.weatherConditions = weather ?? Array.Empty<string>();
            fish.timeConditions = time;
            return fish;
        }
    }
}
#endif
