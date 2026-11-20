using System;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.Fishing
{
    public enum FishRarity : byte
    {
        Common = 0,
        Uncommon = 1,
        Rare = 2,
        Legendary = 3
    }

    public enum FishWaterType : byte
    {
        Ocean = 0,
        Pond = 1,
        Both = 2
    }

    [Flags]
    public enum FishTimeCondition : byte
    {
        Any = 0,
        Day = 1 << 0,
        Night = 1 << 1,
        Dawn = 1 << 2,
        Dusk = 1 << 3
    }

    /// <summary>
    /// Species catalog entry for Contract 0.3. Traits live here; save stores discovery progress only.
    /// </summary>
    [CreateAssetMenu(fileName = "FishData", menuName = "AFK Island/Fish")]
    public sealed class FishData : ScriptableObject
    {
        public string fishId = "fish_mackerel";
        public string displayName = "Mackerel";
        [TextArea(2, 4)] public string descriptionRu = "";
        public FishRarity rarity = FishRarity.Common;
        public FishWaterType waterType = FishWaterType.Ocean;

        [Tooltip("Empty = any weather. Must use WeatherSimulation ids: sunny / rain / fog / wind.")]
        public string[] weatherConditions;

        [Tooltip("Any (0) = all dayparts. Otherwise require matching flag.")]
        public FishTimeCondition timeConditions = FishTimeCondition.Any;

        [Tooltip("P1 pond hook. P0 ocean fishing must not rely on pond flow.")]
        public bool pondEligible;

        [Tooltip("Relative weight inside an eligible catch pool. <= 0 excludes from catch pools.")]
        public float catchWeight = 1f;

        public Sprite icon;
        public GameObject visualPrefab;
        public Color fallbackColor = new(0.35f, 0.55f, 0.85f);

        public bool IsOceanEligible =>
            waterType == FishWaterType.Ocean || waterType == FishWaterType.Both;

        /// <summary>
        /// Stage 10: Pond-only always eligible; Both only when <see cref="pondEligible"/>.
        /// Ocean-only never pond-eligible regardless of the flag.
        /// </summary>
        public bool IsPondEligible =>
            waterType == FishWaterType.Pond ||
            (waterType == FishWaterType.Both && pondEligible);

        public bool HasPositiveCatchWeight => catchWeight > 0f;

        public bool MatchesWeather(string weatherId)
        {
            if (weatherConditions == null || weatherConditions.Length == 0)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(weatherId))
            {
                return false;
            }

            foreach (var allowed in weatherConditions)
            {
                if (!string.IsNullOrWhiteSpace(allowed) &&
                    string.Equals(allowed, weatherId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Canon weather ids from <see cref="WeatherSimulation"/>.</summary>
        public static bool IsKnownWeatherId(string weatherId)
        {
            if (string.IsNullOrWhiteSpace(weatherId))
            {
                return false;
            }

            return string.Equals(weatherId, WeatherSimulation.Sunny, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(weatherId, WeatherSimulation.Rain, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(weatherId, WeatherSimulation.Fog, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(weatherId, WeatherSimulation.Wind, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Empty list = any. Non-empty must only contain known weather ids.</summary>
        public bool HasValidWeatherConditions(out string invalidId)
        {
            invalidId = null;
            if (weatherConditions == null || weatherConditions.Length == 0)
            {
                return true;
            }

            foreach (var id in weatherConditions)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (!IsKnownWeatherId(id))
                {
                    invalidId = id;
                    return false;
                }
            }

            return true;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!HasValidWeatherConditions(out var bad))
            {
                Debug.LogWarning(
                    $"[AFK Island] FishData '{name}' ({fishId}) has unknown weatherConditions '{bad}'. " +
                    $"Use {WeatherSimulation.Sunny}/{WeatherSimulation.Rain}/{WeatherSimulation.Fog}/{WeatherSimulation.Wind}.",
                    this);
            }

            if (catchWeight < 0f)
            {
                catchWeight = 0f;
            }
        }
#endif

        public bool MatchesTimeOfDay(float timeOfDay01)
        {
            if (timeConditions == FishTimeCondition.Any)
            {
                return true;
            }

            var part = ResolveTimePart(timeOfDay01);
            return (timeConditions & part) != 0;
        }

        public static FishTimeCondition ResolveTimePart(float timeOfDay01)
        {
            var t = timeOfDay01 - Mathf.Floor(timeOfDay01);
            // 0=midnight … 0.5=noon. Dawn ~05–07, day ~07–18, dusk ~18–20, night otherwise.
            if (t >= 0.21f && t < 0.29f)
            {
                return FishTimeCondition.Dawn;
            }

            if (t >= 0.29f && t < 0.75f)
            {
                return FishTimeCondition.Day;
            }

            if (t >= 0.75f && t < 0.83f)
            {
                return FishTimeCondition.Dusk;
            }

            return FishTimeCondition.Night;
        }
    }
}
