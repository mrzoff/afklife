using System.Collections.Generic;
using AfkIsland.Save;

namespace AfkIsland.World
{
    public sealed class LivingWorldResult
    {
        public FaunaSimulationResult fauna = new();
        public int habitatsCreated;
        public int cellsAdvancedByFauna;
        public int rainHoursRounded;
        public int windEvents;
        public float fogHours;
        public float plantGrowthMultiplier = 1f;
        public string weatherId = WeatherSimulation.Sunny;
        public List<string> highlights = new();

        public string ToReportLine()
        {
            var parts = new List<string>();
            if (fauna != null && fauna.TotalSpawned > 0)
            {
                parts.Add(fauna.ToReportLine());
            }

            if (habitatsCreated > 0)
            {
                parts.Add($"+{habitatsCreated} мест обитания");
            }

            if (cellsAdvancedByFauna > 0)
            {
                parts.Add($"животные продвинули {cellsAdvancedByFauna} клеток природы");
            }

            if (rainHoursRounded > 0)
            {
                parts.Add($"дождь ~{rainHoursRounded} ч");
            }

            if (fogHours > 0.1f)
            {
                parts.Add($"туман ~{fogHours:0.#} ч");
            }

            if (windEvents > 0)
            {
                parts.Add($"ветер ×{windEvents}");
            }

            if (plantGrowthMultiplier > 1.001f)
            {
                parts.Add($"рост растений ×{plantGrowthMultiplier:0.00}");
            }

            // Saturation copy is already inside fauna.ToReportLine when animals spawned.
            if (fauna != null && fauna.TotalSpawned <= 0)
            {
                var sat = fauna.SaturationLine();
                if (!string.IsNullOrEmpty(sat))
                {
                    parts.Add(sat);
                }
            }

            return parts.Count == 0 ? string.Empty : string.Join("; ", parts);
        }
    }
}
