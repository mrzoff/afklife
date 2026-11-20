#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using AfkIsland.Fishing;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>Contract 0.3 stage 2: FishCatalog load + integrity + ocean pool rules.</summary>
    public static class FishCatalogSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "fish_catalog_smoke_result.txt");

        [MenuItem("AFK Island/Smoke Fish Catalog 0.3")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("Fish catalog smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] Fish catalog smoke OK.");
        }

        public static void RunBatch()
        {
            Debug.Log("[AFK Island] Fish catalog smoke START");
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
                Debug.LogError("[AFK Island] Failed to write fish catalog smoke result: " + e.Message);
            }

            Debug.Log(code == 0
                ? "[AFK Island] Fish catalog smoke OK."
                : "[AFK Island] Fish catalog smoke FAIL code=" + code);
            EditorApplication.Exit(code);
        }

        private static int RunInternal()
        {
            try
            {
                var catalog = Resources.Load<FishCatalog>("FishCatalog");
                if (catalog == null)
                {
                    Debug.LogError("Resources.Load FishCatalog returned null.");
                    return 2;
                }

                if (!catalog.TryValidateIntegrity(out var integrityError))
                {
                    Debug.LogError("Catalog integrity failed: " + integrityError);
                    return 16;
                }

                if (!catalog.TryGetById("fish_mackerel", out var mackerel) || mackerel == null)
                {
                    Debug.LogError("Missing fish_mackerel in catalog.");
                    return 3;
                }

                if (mackerel.rarity != FishRarity.Common || !mackerel.IsOceanEligible)
                {
                    Debug.LogError("fish_mackerel traits unexpected.");
                    return 4;
                }

                if (!catalog.TryGetById("fish_carp", out var carp) || carp == null || !carp.pondEligible)
                {
                    Debug.LogError("fish_carp (pondEligible P1 stub) missing.");
                    return 5;
                }

                if (carp.IsOceanEligible || !carp.IsPondEligible)
                {
                    Debug.LogError("Pond-only carp must be pond-eligible and not ocean-eligible.");
                    return 6;
                }

                var pondList = new List<FishData>();
                catalog.CollectPondEligible(pondList);
                if (!pondList.Exists(f => f != null && f.fishId == "fish_carp") ||
                    pondList.Exists(f => f != null && f.waterType == FishWaterType.Ocean))
                {
                    Debug.LogError("CollectPondEligible must include carp and exclude Ocean-only.");
                    return 26;
                }

                var ocean = new List<FishData>();
                catalog.CollectOceanEligible(ocean);
                if (ocean.Count < 4)
                {
                    Debug.LogError($"Expected >=4 ocean fish, got {ocean.Count}.");
                    return 7;
                }

                foreach (var f in ocean)
                {
                    if (f == null || !f.IsOceanEligible || !f.HasPositiveCatchWeight)
                    {
                        Debug.LogError("Ocean list contains ineligible or zero-weight entry.");
                        return 8;
                    }
                }

                if (ocean.Exists(f => f.fishId == "fish_carp"))
                {
                    Debug.LogError("Ocean list must not include fish_carp.");
                    return 9;
                }

                // Unique fishId across resource catalog list.
                var seenIds = new HashSet<string>();
                foreach (var f in catalog.Fish)
                {
                    if (f == null || string.IsNullOrWhiteSpace(f.fishId))
                    {
                        continue;
                    }

                    if (!seenIds.Add(f.fishId))
                    {
                        Debug.LogError("Duplicate fishId in resource catalog: " + f.fishId);
                        return 17;
                    }

                    if (!f.HasValidWeatherConditions(out var bad))
                    {
                        Debug.LogError($"Bad weatherConditions on {f.fishId}: {bad}");
                        return 18;
                    }
                }

                // Condition helpers — weather ids must match WeatherSimulation canon.
                if (!mackerel.MatchesWeather(WeatherSimulation.Sunny) || !mackerel.MatchesTimeOfDay(0.5f))
                {
                    Debug.LogError("Open-condition mackerel should match sunny noon.");
                    return 10;
                }

                if (!catalog.TryGetById("fish_tuna", out var tuna) || tuna == null)
                {
                    Debug.LogError("Missing fish_tuna.");
                    return 11;
                }

                if (tuna.MatchesWeather(WeatherSimulation.Rain))
                {
                    Debug.LogError("Tuna should not match rain.");
                    return 12;
                }

                if (!tuna.MatchesWeather(WeatherSimulation.Sunny) || !tuna.MatchesTimeOfDay(0.5f))
                {
                    Debug.LogError("Tuna should match sunny day.");
                    return 13;
                }

                if (tuna.MatchesTimeOfDay(0.05f))
                {
                    Debug.LogError("Tuna should not match deep night.");
                    return 14;
                }

                if (!catalog.TryGetById("fish_bass", out var bass) || bass == null)
                {
                    Debug.LogError("Missing fish_bass.");
                    return 19;
                }

                if (!bass.MatchesWeather(WeatherSimulation.Rain) ||
                    !bass.MatchesWeather(WeatherSimulation.Wind) ||
                    bass.MatchesWeather(WeatherSimulation.Sunny) ||
                    bass.MatchesWeather(WeatherSimulation.Fog))
                {
                    Debug.LogError("Bass weather filter must be rain|wind only.");
                    return 20;
                }

                if (catalog.TryGetById("no_such_fish", out _))
                {
                    Debug.LogError("Unknown id should miss.");
                    return 15;
                }

                // catchWeight <= 0 excluded from pool (ephemeral catalog).
                var zero = ScriptableObject.CreateInstance<FishData>();
                zero.fishId = "fish_zero_weight";
                zero.waterType = FishWaterType.Ocean;
                zero.catchWeight = 0f;
                var positive = ScriptableObject.CreateInstance<FishData>();
                positive.fishId = "fish_positive_weight";
                positive.waterType = FishWaterType.Ocean;
                positive.catchWeight = 1f;
                var tempPool = ScriptableObject.CreateInstance<FishCatalog>();
                tempPool.Configure(new[] { zero, positive });
                var filtered = new List<FishData>();
                tempPool.CollectOceanEligible(filtered);
                if (filtered.Count != 1 || filtered[0].fishId != "fish_positive_weight")
                {
                    Debug.LogError("catchWeight<=0 must be excluded from ocean pool.");
                    return 21;
                }

                // Duplicate fishId: integrity fails; pool keeps first only.
                var dupA = ScriptableObject.CreateInstance<FishData>();
                dupA.fishId = "fish_dup";
                dupA.waterType = FishWaterType.Ocean;
                dupA.catchWeight = 1f;
                var dupB = ScriptableObject.CreateInstance<FishData>();
                dupB.fishId = "fish_dup";
                dupB.waterType = FishWaterType.Ocean;
                dupB.catchWeight = 1f;
                var tempDup = ScriptableObject.CreateInstance<FishCatalog>();
                tempDup.Configure(new[] { dupA, dupB });
                if (tempDup.TryValidateIntegrity(out _))
                {
                    Debug.LogError("Duplicate fishId must fail TryValidateIntegrity.");
                    return 22;
                }

                var dupOcean = new List<FishData>();
                tempDup.CollectOceanEligible(dupOcean);
                if (dupOcean.Count != 1 || dupOcean[0] != dupA)
                {
                    Debug.LogError("Ocean pool must keep only the first duplicate fishId.");
                    return 23;
                }

                // Unknown weather id fails integrity.
                var badWeatherFish = ScriptableObject.CreateInstance<FishData>();
                badWeatherFish.fishId = "fish_bad_weather";
                badWeatherFish.waterType = FishWaterType.Ocean;
                badWeatherFish.catchWeight = 1f;
                badWeatherFish.weatherConditions = new[] { "stormy" };
                var tempWeather = ScriptableObject.CreateInstance<FishCatalog>();
                tempWeather.Configure(new[] { badWeatherFish });
                if (tempWeather.TryValidateIntegrity(out _))
                {
                    Debug.LogError("Unknown weatherConditions must fail integrity.");
                    return 24;
                }

                if (!FishData.IsKnownWeatherId(WeatherSimulation.Sunny) ||
                    !FishData.IsKnownWeatherId(WeatherSimulation.Rain) ||
                    !FishData.IsKnownWeatherId(WeatherSimulation.Fog) ||
                    !FishData.IsKnownWeatherId(WeatherSimulation.Wind) ||
                    FishData.IsKnownWeatherId("stormy"))
                {
                    Debug.LogError("IsKnownWeatherId mismatch vs WeatherSimulation canon.");
                    return 25;
                }

                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] Fish catalog smoke exception: " + e);
                return 1;
            }
        }
    }
}
#endif
