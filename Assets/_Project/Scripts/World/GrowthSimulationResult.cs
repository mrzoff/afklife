using System;
using System.Collections.Generic;
using AfkIsland.Save;

namespace AfkIsland.World
{
    [Serializable]
    public sealed class GrowthSimulationResult
    {
        public bool success;
        public string errorCode;
        public string errorMessage;

        public GameSaveData updatedSave;
        public long elapsedSeconds;
        public long appliedSeconds;
        public float expandedArea;
        public List<ZoneGrowthReport> zones = new();

        public int chunksCreated;
        public int cellsAdvanced;
        public int eventsCreated;
        public List<NatureEventSaveData> newEvents = new();

        public string ToReportText()
        {
            if (!success)
            {
                return $"Остров: симуляция не применена ({errorCode}: {errorMessage})";
            }

            var lines = new System.Text.StringBuilder();
            if (expandedArea > 0.1f || chunksCreated > 0)
            {
                lines.AppendLine(
                    $"Остров вырос (AFK): +{expandedArea:0.#} клеток земли" +
                    (chunksCreated > 0 ? $", +{chunksCreated} новых участков" : string.Empty) +
                    $", стадий: {cellsAdvanced}");
            }
            else if (appliedSeconds > 0)
            {
                lines.AppendLine(
                    "Остров пока не вырос: за это отсутствие клеток земли не добавилось " +
                    "(при текущем темпе нужно примерно 20–40 минут AFK, либо кнопка «Демо +8ч»).");
            }
            else
            {
                lines.AppendLine("Остров: симуляция мира без прошедшего времени.");
            }

            lines.AppendLine(
                $"Симулировано мира: {FormatSeconds(appliedSeconds)} (запрос {FormatSeconds(elapsedSeconds)})");
            if (zones == null || zones.Count == 0)
            {
                if (expandedArea <= 0.1f && chunksCreated == 0)
                {
                    lines.AppendLine("• Новых зон расширения нет.");
                }

                return lines.ToString();
            }

            foreach (var zone in zones)
            {
                if (zone == null)
                {
                    continue;
                }

                lines.AppendLine(
                    $"• {zone.direction} участок [{zone.chunkX},{zone.chunkZ}]: +{zone.expandedArea:0.#} клеток" +
                    (zone.newChunkCreated ? " (новый участок!)" : string.Empty));
            }

            if (newEvents != null && newEvents.Count > 0)
            {
                lines.AppendLine("События:");
                foreach (var evt in newEvents)
                {
                    if (evt == null)
                    {
                        continue;
                    }

                    lines.AppendLine($"• {evt.titleRu ?? evt.eventId}");
                }
            }

            return lines.ToString();
        }

        private static string FormatSeconds(long seconds) => AfkDurationFormat.Format(seconds);

        public static GrowthSimulationResult Fail(string code, string message)
        {
            return new GrowthSimulationResult
            {
                success = false,
                errorCode = code,
                errorMessage = message
            };
        }
    }

    [Serializable]
    public sealed class ZoneGrowthReport
    {
        public int chunkX;
        public int chunkZ;
        public string direction;
        public float expandedArea;
        public int cellsAdvanced;
        public bool newChunkCreated;
    }
}
