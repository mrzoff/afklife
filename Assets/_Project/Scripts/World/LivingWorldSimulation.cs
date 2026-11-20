using AfkIsland.Save;

namespace AfkIsland.World
{
    /// <summary>
    /// Coordinates weather → ecosystem → fauna populations → nature feedback for one AFK window.
    /// Pure data; efficient hourly buckets.
    /// </summary>
    public static class LivingWorldSimulation
    {
        /// <summary>
        /// Full living pass including weather. Prefer <see cref="SimulateAfterWeather"/> when
        /// plant offline growth must use the weather multiplier first.
        /// </summary>
        public static LivingWorldResult Simulate(GameSaveData save, long appliedSeconds, DeterministicRng rng)
        {
            if (save == null || appliedSeconds <= 0 || rng == null)
            {
                return new LivingWorldResult();
            }

            SaveMigrationService.EnsureEcosystemWeatherAnimals(save);
            var weatherStats = WeatherSimulation.Advance(save, appliedSeconds, rng);
            return SimulateAfterWeather(save, appliedSeconds, rng, weatherStats);
        }

        public static LivingWorldResult SimulateAfterWeather(
            GameSaveData save,
            long appliedSeconds,
            DeterministicRng rng,
            WeatherSimulation.WeatherWindowStats weatherStats)
        {
            var result = new LivingWorldResult();
            if (save == null || appliedSeconds <= 0 || rng == null)
            {
                return result;
            }

            SaveMigrationService.EnsureEcosystemWeatherAnimals(save);
            weatherStats ??= new WeatherSimulation.WeatherWindowStats();

            result.rainHoursRounded = (int)(weatherStats.rainHours + 0.5f);
            result.windEvents = weatherStats.windEvents;
            result.fogHours = weatherStats.fogHours;
            result.weatherId = weatherStats.finalWeatherId;
            result.plantGrowthMultiplier = weatherStats.plantGrowthMultiplier;

            var raining = weatherStats.rainHours > 0.5f || WeatherSimulation.IsRaining(save.weather);
            EcosystemSimulation.Recalculate(save, raining);

            result.fauna = FaunaSimulation.Simulate(save, appliedSeconds, rng, weatherStats);
            result.habitatsCreated = result.fauna?.habitatsCreated ?? 0;

            FaunaNatureFeedback.Apply(save, result, rng);
            EcosystemSimulation.Recalculate(save, raining);

            var line = result.ToReportLine();
            if (!string.IsNullOrEmpty(line))
            {
                result.highlights.Add(line);
            }

            return result;
        }
    }
}
