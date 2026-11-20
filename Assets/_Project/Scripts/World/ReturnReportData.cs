using System;
using System.Collections.Generic;
using AfkIsland.Save;

namespace AfkIsland.World
{
    [Serializable]
    public sealed class ReturnReportData
    {
        public long absenceSeconds;
        public long worldAppliedSeconds;
        public long catchUpRemainingSeconds;
        public float expandedArea;
        public int chunksCreated;
        public int cellsAdvanced;
        public int unlockedChunksNow;
        public int landCellsNow;
        public int plantsGrownStages;
        public int plantsMatured;
        public int plantsPresent;
        public int unknownPlantsSkipped;
        public int plantsSpawnedByNature;
        public string natureColonizationLine;
        public string settlementSummaryLine;
        public int settlementCellsAdvanced;
        public int settlementPathsAdded;
        public int settlementStonesAdded;
        public int settlementHutsAdded;
        public int settlementPathsNow;
        public int settlementStonesNow;
        public int settlementHutsNow;
        public string plantSummaryLine;
        public List<NatureEventSaveData> events = new();
        public List<ZoneGrowthReport> zones = new();
        public bool hasMeaningfulGrowth;

        public int animalsSpawned;
        public int butterfliesSpawned;
        public int birdsSpawned;
        public int frogsSpawned;
        public string faunaSummaryLine;
        public bool faunaHitSessionCap;
        public bool faunaHitIslandCap;
        public string faunaSaturationLine;
        public float humidity;
        public float fertility;
        public float biodiversity;
        public string weatherId;
        public float timeOfDay01;
        public float plantGrowthMultiplier = 1f;
        public string weatherSummaryLine;

        public int habitatsCreated;
        public int cellsAdvancedByFauna;
        public int rainHours;
        public int windEvents;
        public float fogHours;
        public string livingSummaryLine;
        public string afkBandId;
        public string afkBandTitleRu;
        public string afkBandHintRu;

        public static ReturnReportData FromSession(
            long absenceSeconds,
            GrowthSimulationResult world,
            GameSaveData saveAfter,
            int plantsGrownStages,
            int plantsMatured,
            int plantsPresent,
            string plantSummaryLine,
            FaunaSimulationResult fauna = null,
            LivingWorldResult living = null,
            int unknownPlantsSkipped = 0,
            int plantsSpawnedByNature = 0,
            string natureColonizationLine = null,
            SettlementSimulationResult settlement = null)
        {
            ResolveAfkBand(absenceSeconds, out var bandId, out var bandTitle, out var bandHint);
            var applied = world != null ? world.appliedSeconds : 0;
            var faunaResult = fauna ?? living?.fauna;
            SettlementSimulation.CountStages(
                saveAfter, out _, out var pathsNow, out var stonesNow, out var hutsNow);
            var report = new ReturnReportData
            {
                absenceSeconds = absenceSeconds,
                worldAppliedSeconds = applied,
                catchUpRemainingSeconds = Math.Max(0, absenceSeconds - applied),
                expandedArea = world != null ? world.expandedArea : 0f,
                chunksCreated = world != null ? world.chunksCreated : 0,
                cellsAdvanced = world != null ? world.cellsAdvanced : 0,
                plantsGrownStages = plantsGrownStages,
                plantsMatured = plantsMatured,
                plantsPresent = plantsPresent,
                unknownPlantsSkipped = unknownPlantsSkipped,
                plantsSpawnedByNature = plantsSpawnedByNature,
                natureColonizationLine = natureColonizationLine ?? string.Empty,
                settlementSummaryLine = settlement != null ? settlement.ToReportLine() : string.Empty,
                settlementCellsAdvanced = settlement?.cellsAdvanced ?? 0,
                settlementPathsAdded = settlement?.pathsAdded ?? 0,
                settlementStonesAdded = settlement?.stonesAdded ?? 0,
                settlementHutsAdded = settlement?.hutsAdded ?? 0,
                settlementPathsNow = pathsNow,
                settlementStonesNow = stonesNow,
                settlementHutsNow = hutsNow,
                plantSummaryLine = plantSummaryLine ?? string.Empty,
                events = world?.newEvents != null
                    ? new List<NatureEventSaveData>(world.newEvents)
                    : new List<NatureEventSaveData>(),
                zones = world?.zones != null
                    ? new List<ZoneGrowthReport>(world.zones)
                    : new List<ZoneGrowthReport>(),
                animalsSpawned = faunaResult?.TotalSpawned ?? 0,
                butterfliesSpawned = faunaResult?.butterflies ?? 0,
                birdsSpawned = faunaResult?.birds ?? 0,
                frogsSpawned = faunaResult?.frogs ?? 0,
                faunaSummaryLine = faunaResult?.ToReportLine() ?? string.Empty,
                faunaHitSessionCap = faunaResult?.hitSessionCap ?? false,
                faunaHitIslandCap = faunaResult?.hitIslandCap ?? false,
                faunaSaturationLine = faunaResult?.SaturationLine() ?? string.Empty,
                humidity = saveAfter?.ecosystem != null ? saveAfter.ecosystem.humidity : 0f,
                fertility = saveAfter?.ecosystem != null ? saveAfter.ecosystem.fertility : 0f,
                biodiversity = saveAfter?.ecosystem != null ? saveAfter.ecosystem.biodiversity : 0f,
                weatherId = saveAfter?.weather != null ? saveAfter.weather.weatherId : "sunny",
                timeOfDay01 = saveAfter?.weather != null ? saveAfter.weather.timeOfDay01 : 0.5f,
                plantGrowthMultiplier = living != null ? living.plantGrowthMultiplier : 1f,
                habitatsCreated = living?.habitatsCreated ?? faunaResult?.habitatsCreated ?? 0,
                cellsAdvancedByFauna = living?.cellsAdvancedByFauna ?? 0,
                rainHours = living?.rainHoursRounded
                            ?? (saveAfter?.weather != null ? (int)(saveAfter.weather.rainHours + 0.5f) : 0),
                windEvents = living?.windEvents
                             ?? (saveAfter?.weather != null ? saveAfter.weather.windEvents : 0),
                fogHours = living?.fogHours
                           ?? (saveAfter?.weather != null ? saveAfter.weather.fogHours : 0f),
                livingSummaryLine = living?.ToReportLine() ?? string.Empty,
                afkBandId = bandId,
                afkBandTitleRu = bandTitle,
                afkBandHintRu = bandHint
            };

            report.weatherSummaryLine = BuildWeatherSummary(report);

            if (saveAfter?.chunks != null)
            {
                foreach (var c in saveAfter.chunks)
                {
                    if (c != null && c.isUnlocked)
                    {
                        report.unlockedChunksNow++;
                    }
                }
            }

            report.landCellsNow = CountLand(saveAfter);
            report.hasMeaningfulGrowth =
                report.expandedArea > 0.1f ||
                report.chunksCreated > 0 ||
                report.cellsAdvanced > 0 ||
                report.plantsGrownStages > 0 ||
                report.plantsMatured > 0 ||
                report.plantsSpawnedByNature > 0 ||
                report.settlementCellsAdvanced > 0 ||
                report.animalsSpawned > 0 ||
                report.habitatsCreated > 0 ||
                report.cellsAdvancedByFauna > 0 ||
                report.catchUpRemainingSeconds > 0 ||
                (report.events != null && report.events.Count > 0);

            return report;
        }

        private static string BuildWeatherSummary(ReturnReportData r)
        {
            var parts = new List<string>();
            if (r.rainHours > 0)
            {
                parts.Add($"дождь ~{r.rainHours} ч");
            }

            if (r.fogHours > 0.1f)
            {
                parts.Add($"туман ~{r.fogHours:0.#} ч");
            }

            if (r.windEvents > 0)
            {
                parts.Add($"порывы ветра ×{r.windEvents}");
            }

            if (r.plantGrowthMultiplier > 1.001f)
            {
                parts.Add($"рост растений ×{r.plantGrowthMultiplier:0.00}");
            }

            return parts.Count == 0
                ? "Погода была мягкой — без ускорения роста."
                : "За отсутствие: " + string.Join(", ", parts) + ".";
        }

        public static void ResolveAfkBand(
            long absenceSeconds,
            out string id,
            out string titleRu,
            out string hintRu)
        {
            var hours = absenceSeconds / 3600.0;
            if (hours >= 30 * 24)
            {
                id = "30d";
                titleRu = "Около месяца отсутствия";
                hintRu = "Ожидай крупные изменения острова и новые возможности природы.";
            }
            else if (hours >= 7 * 24)
            {
                id = "7d";
                titleRu = "Неделя AFK";
                hintRu = "Существенное расширение и развитие экосистемы.";
            }
            else if (hours >= 3 * 24)
            {
                id = "3d";
                titleRu = "Несколько дней AFK";
                hintRu = "Экосистема могла заметно измениться, ищи новые открытия.";
            }
            else if (hours >= 24)
            {
                id = "24h";
                titleRu = "Сутки AFK";
                hintRu = "Новые места обитания и животные — типичный горизонт.";
            }
            else if (hours >= 8)
            {
                id = "8h";
                titleRu = "Около 8 часов AFK";
                hintRu = "Новая земля, растительность и природные события.";
            }
            else if (hours >= 1)
            {
                id = "1h";
                titleRu = "Короткое отсутствие";
                hintRu = "Рост растений и небольшие изменения острова.";
            }
            else
            {
                id = "short";
                titleRu = "Короткий перерыв";
                hintRu = "Остров только начал отвечать на твоё отсутствие.";
            }
        }

        private static int CountLand(GameSaveData save)
        {
            if (save?.worldGrowth?.chunks == null)
            {
                return 0;
            }

            var n = 0;
            foreach (var growth in save.worldGrowth.chunks)
            {
                if (growth == null)
                {
                    continue;
                }

                if (!ChunkMaskCodec.TryDecodeBase64(growth.stageMaskBase64, out var stages, out _))
                {
                    continue;
                }

                for (var i = 0; i < stages.Length; i++)
                {
                    if (stages[i] != (byte)CellStage.Void)
                    {
                        n++;
                    }
                }
            }

            return n;
        }
    }
}
