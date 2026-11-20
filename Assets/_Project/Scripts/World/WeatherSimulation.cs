using System;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    public static class WeatherSimulation
    {
        public const string Sunny = "sunny";
        public const string Rain = "rain";
        public const string Fog = "fog";
        public const string Wind = "wind";

        public sealed class WeatherWindowStats
        {
            public float rainHours;
            public float fogHours;
            public int windEvents;
            public string finalWeatherId = Sunny;
            public float plantGrowthMultiplier = 1f;
        }

        /// <summary>
        /// Hourly weather buckets. Accumulates report stats; never penalizes long AFK (&lt;1.0).
        /// </summary>
        public static WeatherWindowStats Advance(GameSaveData save, long appliedSeconds, DeterministicRng rng)
        {
            var stats = new WeatherWindowStats();
            if (save == null || appliedSeconds <= 0)
            {
                return stats;
            }

            SaveMigrationService.EnsureEcosystemWeatherAnimals(save);
            var weather = save.weather;
            weather.rainHours = 0f;
            weather.fogHours = 0f;
            weather.windEvents = 0;

            // CIF-A2: advance by exact elapsed time — no free hour for short AFK.
            var hoursExact = appliedSeconds / 3600.0;
            var wholeHours = (int)hoursExact;
            weather.timeOfDay01 = Mathf.Repeat(
                weather.timeOfDay01 + (float)(hoursExact / 24.0), 1f);

            // Collapse very long AFK into at most 168 hourly weather rolls + remainder rain.
            var rolls = Mathf.Min(Mathf.Max(0, wholeHours), 168);
            var id = string.IsNullOrWhiteSpace(weather.weatherId) ? Sunny : weather.weatherId;

            for (var h = 0; h < rolls; h++)
            {
                id = NextWeatherId(id, rng);
                weather.weatherId = id;
                switch (id)
                {
                    case Rain:
                        weather.rainHours += 1f;
                        break;
                    case Fog:
                        weather.fogHours += 1f;
                        break;
                    case Wind:
                        weather.windEvents += 1;
                        break;
                }
            }

            // Sub-hour remainder: accumulate gentle rain share only (no full weather roll).
            var frac = hoursExact - wholeHours;
            if (frac > 0.001 && string.Equals(id, Rain, System.StringComparison.OrdinalIgnoreCase))
            {
                weather.rainHours += (float)frac;
            }

            if (wholeHours > rolls)
            {
                // Extra time beyond 7d: mostly gentle rain accumulation, no punishment.
                var extra = wholeHours - rolls;
                weather.rainHours += extra * 0.35f;
            }

            stats.rainHours = weather.rainHours;
            stats.fogHours = weather.fogHours;
            stats.windEvents = weather.windEvents;
            stats.finalWeatherId = weather.weatherId;
            // Rain speeds plants; fog/wind/sun stay at 1.0 — never below 1.
            var denom = (float)Math.Max(hoursExact, 1e-6);
            stats.plantGrowthMultiplier = 1f + Mathf.Clamp01(weather.rainHours / denom) * 0.15f;

            weather.lastAdvanceUtc = save.worldGrowth != null
                ? save.worldGrowth.lastSimulationUtc
                : save.lastSaveUtc;
            return stats;
        }

        private static string NextWeatherId(string current, DeterministicRng rng)
        {
            var roll = rng.NextDouble();
            if (string.Equals(current, Rain, System.StringComparison.OrdinalIgnoreCase))
            {
                if (roll < 0.5) return Sunny;
                if (roll < 0.7) return Fog;
                if (roll < 0.85) return Wind;
                return Rain;
            }

            if (string.Equals(current, Fog, System.StringComparison.OrdinalIgnoreCase))
            {
                if (roll < 0.55) return Sunny;
                if (roll < 0.75) return Rain;
                return Fog;
            }

            if (string.Equals(current, Wind, System.StringComparison.OrdinalIgnoreCase))
            {
                if (roll < 0.6) return Sunny;
                if (roll < 0.8) return Rain;
                return Wind;
            }

            // sunny
            if (roll < 0.22) return Rain;
            if (roll < 0.32) return Fog;
            if (roll < 0.42) return Wind;
            return Sunny;
        }

        public static bool IsRaining(WeatherSaveData weather)
        {
            return weather != null &&
                   string.Equals(weather.weatherId, Rain, System.StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFoggy(WeatherSaveData weather)
        {
            return weather != null &&
                   string.Equals(weather.weatherId, Fog, System.StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsWindy(WeatherSaveData weather)
        {
            return weather != null &&
                   string.Equals(weather.weatherId, Wind, System.StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsDaytime(WeatherSaveData weather)
        {
            if (weather == null)
            {
                return true;
            }

            return weather.timeOfDay01 >= 0.25f && weather.timeOfDay01 < 0.83f;
        }
    }
}
