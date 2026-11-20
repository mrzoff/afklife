using System;

namespace AfkIsland.World
{
    /// <summary>Shared human-readable AFK durations (days when >= 24h).</summary>
    public static class AfkDurationFormat
    {
        public static string Format(long seconds)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (ts.TotalDays >= 1)
            {
                return $"{(int)ts.TotalDays} д {ts.Hours} ч";
            }

            if (ts.TotalHours >= 1)
            {
                return $"{(int)ts.TotalHours} ч {ts.Minutes} мин";
            }

            if (ts.TotalMinutes >= 1)
            {
                return $"{(int)ts.TotalMinutes} мин {ts.Seconds} сек";
            }

            return $"{ts.Seconds} сек";
        }

        /// <summary>
        /// One-line acceptance summary:
        /// Отсутствовал: 8 д · Обработано: 7 д · Осталось: 1 д
        /// </summary>
        public static string FormatCatchUpTriple(long absenceSeconds, long appliedSeconds, long remainingSeconds)
        {
            return
                $"Отсутствовал: {Format(absenceSeconds)} · " +
                $"Обработано: {Format(appliedSeconds)} · " +
                $"Осталось: {Format(Math.Max(0, remainingSeconds))}";
        }
    }
}
