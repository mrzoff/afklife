using System;
using System.Text;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.UI
{
    /// <summary>
    /// Full-screen return report: «Пока тебя не было».
    /// Blocks play until the player presses Continue.
    /// </summary>
    public sealed class ReturnReportHud : MonoBehaviour
    {
        [SerializeField] private bool visible;

        private ReturnReportData _report;
        private Action _onContinue;
        private Vector2 _scroll;

        public bool IsVisible => visible;

        public void Show(ReturnReportData report, Action onContinue)
        {
            _report = report;
            _onContinue = onContinue;
            visible = report != null;
            _scroll = Vector2.zero;
        }

        public void Hide()
        {
            visible = false;
            _report = null;
            _onContinue = null;
        }

        private void OnGUI()
        {
            if (!visible || _report == null)
            {
                return;
            }

            var dim = new Color(0.05f, 0.12f, 0.1f, 0.72f);
            DrawQuad(new Rect(0, 0, Screen.width, Screen.height), dim);

            var w = Mathf.Min(560f, Screen.width - 36f);
            var h = Mathf.Min(620f, Screen.height - 36f);
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

            DrawQuad(rect, new Color(0.1f, 0.18f, 0.14f, 0.94f));
            DrawBorder(rect, new Color(0.45f, 0.72f, 0.4f, 0.85f));

            GUILayout.BeginArea(new Rect(rect.x + 22f, rect.y + 18f, rect.width - 44f, rect.height - 36f));

            var title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 30,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = new Color(0.85f, 0.95f, 0.8f) }
            };
            var subtitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = new Color(0.7f, 0.82f, 0.68f) }
            };
            var body = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                normal = { textColor = new Color(0.9f, 0.93f, 0.88f) }
            };
            var section = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(0.65f, 0.88f, 0.55f) }
            };
            var button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                fixedHeight = 48f
            };

            GUILayout.Label("Пока тебя не было", title);
            GUILayout.Space(4f);
            GUILayout.Label(
                AfkDurationFormat.FormatCatchUpTriple(
                    _report.absenceSeconds,
                    _report.worldAppliedSeconds,
                    _report.catchUpRemainingSeconds),
                subtitle);
            if (!string.IsNullOrWhiteSpace(_report.afkBandTitleRu))
            {
                GUILayout.Label(_report.afkBandTitleRu, subtitle);
            }

            GUILayout.Space(12f);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            GUILayout.Label("Горизонт AFK", section);
            GUILayout.Label(
                string.IsNullOrWhiteSpace(_report.afkBandHintRu)
                    ? "Чем дольше тебя нет — тем интереснее остров."
                    : _report.afkBandHintRu,
                body);
            if (_report.catchUpRemainingSeconds > 0)
            {
                GUILayout.Space(4f);
                GUILayout.Label(
                    "Остаток догонится при следующих визитах — прогресс не сгорает. " +
                    "Уже обработанное время повторно не начисляется.",
                    body);
            }

            GUILayout.Space(10f);

            GUILayout.Label("Развитие острова", section);
            GUILayout.Label(BuildStatsBlock(_report), body);
            GUILayout.Space(10f);

            GUILayout.Label("Новые участки", section);
            GUILayout.Label(BuildZonesBlock(_report), body);
            GUILayout.Space(10f);

            GUILayout.Label("Природа", section);
            GUILayout.Label(BuildNatureBlock(_report), body);
            GUILayout.Space(10f);

            GUILayout.Label("Поселение", section);
            GUILayout.Label(BuildSettlementBlock(_report), body);
            GUILayout.Space(10f);

            GUILayout.Label("Растения", section);
            GUILayout.Label(BuildPlantsBlock(_report), body);
            GUILayout.Space(10f);

            GUILayout.Label("Живая природа (офлайн)", section);
            GUILayout.Label(BuildEcosystemBlock(_report), body);
            GUILayout.Space(10f);

            // Stage 11C: report-first event cards (session events only; no Save / claim).
            DrawEventsSection(_report, section, body);

            GUILayout.EndScrollView();
            GUILayout.Space(10f);

            if (GUILayout.Button("Продолжить", button))
            {
                var cb = _onContinue;
                Hide();
                cb?.Invoke();
            }

            GUILayout.EndArea();
        }

        private static string BuildStatsBlock(ReturnReportData r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Территория выросла на: +{r.expandedArea:0.#} клеток земли");
            sb.AppendLine($"Новых chunk: +{r.chunksCreated}");
            sb.AppendLine($"Стадий клеток продвинуто: {r.cellsAdvanced}");
            sb.AppendLine($"Открыто участков сейчас: {r.unlockedChunksNow}");
            sb.AppendLine($"Клеток земли сейчас: {r.landCellsNow}");
            sb.AppendLine(
                AfkDurationFormat.FormatCatchUpTriple(
                    r.absenceSeconds, r.worldAppliedSeconds, r.catchUpRemainingSeconds));

            return sb.ToString().TrimEnd();
        }

        private static string BuildZonesBlock(ReturnReportData r)
        {
            if (r.zones == null || r.zones.Count == 0)
            {
                return r.expandedArea > 0.1f
                    ? "Земля расширилась у края острова."
                    : "Новых участков за это отсутствие нет.";
            }

            var sb = new StringBuilder();
            foreach (var z in r.zones)
            {
                if (z == null)
                {
                    continue;
                }

                sb.AppendLine(
                    $"• {z.direction} [{z.chunkX},{z.chunkZ}]: +{z.expandedArea:0.#} клеток" +
                    (z.newChunkCreated ? " — новый участок" : string.Empty));
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildNatureBlock(ReturnReportData r)
        {
            var sb = new StringBuilder();
            if (r.plantsSpawnedByNature > 0)
            {
                sb.AppendLine($"Новых растений: {r.plantsSpawnedByNature}");
            }
            else
            {
                sb.AppendLine("Новых растений: 0");
            }

            // Settlement lines live in their own section; keep plant colonization only here.
            if (!string.IsNullOrWhiteSpace(r.natureColonizationLine))
            {
                foreach (var line in r.natureColonizationLine.Split('\n'))
                {
                    var t = line.Trim();
                    if (string.IsNullOrEmpty(t) ||
                        t.StartsWith("Поселение", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    sb.AppendLine(t);
                }
            }

            sb.AppendLine($"Выросло стадий: {r.plantsGrownStages}");
            sb.AppendLine($"Достигли зрелости: {r.plantsMatured}");
            return sb.ToString().TrimEnd();
        }

        private static string BuildSettlementBlock(ReturnReportData r)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(r.settlementSummaryLine))
            {
                sb.AppendLine(r.settlementSummaryLine.Trim());
            }
            else if (r.settlementCellsAdvanced > 0)
            {
                sb.AppendLine($"Поселение выросло на {r.settlementCellsAdvanced} клетке(ах)");
            }
            else
            {
                sb.AppendLine("За это отсутствие поселение не выросло.");
            }

            sb.AppendLine(
                $"Сейчас: тропы {r.settlementPathsNow} · камни {r.settlementStonesNow} · дома {r.settlementHutsNow}");
            return sb.ToString().TrimEnd();
        }

        private static string BuildPlantsBlock(ReturnReportData r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Растений на острове: {r.plantsPresent}");
            sb.AppendLine($"Выросло стадий: {r.plantsGrownStages}");
            sb.AppendLine($"Достигли зрелости: {r.plantsMatured}");
            if (r.plantsSpawnedByNature > 0)
            {
                sb.AppendLine($"Заселено природой: +{r.plantsSpawnedByNature}");
            }

            if (!string.IsNullOrWhiteSpace(r.plantSummaryLine))
            {
                sb.AppendLine(r.plantSummaryLine.Trim());
            }

            if (r.unknownPlantsSkipped > 0)
            {
                sb.AppendLine($"Пропущено неизвестных растений: {r.unknownPlantsSkipped}");
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildEcosystemBlock(ReturnReportData r)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                $"Влажность {r.humidity:0.00} · Плодородие {r.fertility:0.00} · Биоразнообразие {r.biodiversity:0.00}");
            var weatherRu = r.weatherId?.ToLowerInvariant() switch
            {
                "rain" => "дождь",
                "fog" => "туман",
                "wind" => "ветер",
                _ => "ясно"
            };
            var dayRu = r.timeOfDay01 >= 0.25f && r.timeOfDay01 < 0.83f ? "день" : "ночь";
            sb.AppendLine($"Погода сейчас: {weatherRu}, ближе к «{dayRu}»");
            if (!string.IsNullOrWhiteSpace(r.weatherSummaryLine))
            {
                sb.AppendLine(r.weatherSummaryLine);
            }
            else if (r.rainHours > 0 || r.windEvents > 0 || r.fogHours > 0.1f)
            {
                sb.AppendLine(
                    $"За отсутствие: дождь ~{r.rainHours} ч, туман ~{r.fogHours:0.#} ч, порывы ветра ×{r.windEvents}");
            }

            if (!string.IsNullOrWhiteSpace(r.livingSummaryLine))
            {
                sb.AppendLine(r.livingSummaryLine);
            }
            else if (r.animalsSpawned > 0)
            {
                sb.AppendLine(
                    string.IsNullOrWhiteSpace(r.faunaSummaryLine)
                        ? $"Новых животных: {r.animalsSpawned}"
                        : r.faunaSummaryLine);
            }
            else
            {
                sb.AppendLine("Новых животных за это отсутствие не появилось.");
            }

            if (!string.IsNullOrWhiteSpace(r.faunaSaturationLine))
            {
                sb.AppendLine(r.faunaSaturationLine);
            }

            if (r.habitatsCreated > 0)
            {
                sb.AppendLine($"Новых мест обитания: {r.habitatsCreated}");
            }

            if (r.cellsAdvancedByFauna > 0)
            {
                sb.AppendLine($"Животные помогли природе: +{r.cellsAdvancedByFauna} клеток");
            }

            return sb.ToString().TrimEnd();
        }

        private static void DrawEventsSection(ReturnReportData r, GUIStyle section, GUIStyle body)
        {
            GUILayout.Label("Особые события", section);
            var view = ReturnReportEventsPresenter.Build(r);
            if (!view.HasEvents)
            {
                GUILayout.Label(ReturnReportEventsPresenter.EmptyStateRu, body);
                return;
            }

            var subhead = new GUIStyle(body)
            {
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.72f, 0.84f, 0.7f) }
            };
            var countStyle = new GUIStyle(body)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.82f, 0.92f, 0.75f) }
            };
            var typeStyle = new GUIStyle(body)
            {
                fontSize = 13,
                normal = { textColor = new Color(0.62f, 0.78f, 0.6f) }
            };
            var titleStyle = new GUIStyle(body)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                normal = { textColor = new Color(0.92f, 0.96f, 0.88f) }
            };

            GUILayout.Label(ReturnReportEventsPresenter.SubheadRu, subhead);
            GUILayout.Label(view.CountLineRu, countStyle);
            GUILayout.Space(6f);

            for (var i = 0; i < view.Cards.Count; i++)
            {
                DrawEventCard(view.Cards[i], typeStyle, titleStyle, body);
                GUILayout.Space(8f);
            }

            if (!string.IsNullOrEmpty(view.OverflowLineRu))
            {
                GUILayout.Label(view.OverflowLineRu, subhead);
            }
        }

        private static void DrawEventCard(
            ReturnReportEventsPresenter.EventCardView card,
            GUIStyle typeStyle,
            GUIStyle titleStyle,
            GUIStyle body)
        {
            // Soft panel without interactive controls (read-only card).
            var old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.14f, 0.22f, 0.17f, 0.95f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUI.backgroundColor = old;

            GUILayout.BeginHorizontal();
            var iconStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = card.IconColor },
                fixedWidth = 28f,
                fixedHeight = 28f
            };
            GUILayout.Label(card.IconGlyph, iconStyle);
            GUILayout.BeginVertical();
            GUILayout.Label(card.TypeRu, typeStyle);
            GUILayout.Label(card.TitleRu, titleStyle);
            if (!string.IsNullOrWhiteSpace(card.DetailRu))
            {
                GUILayout.Label(card.DetailRu, body);
            }

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private static string FormatDuration(long seconds) => AfkDurationFormat.Format(seconds);

        private static void DrawQuad(Rect rect, Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static void DrawBorder(Rect rect, Color color)
        {
            const float t = 2f;
            DrawQuad(new Rect(rect.x, rect.y, rect.width, t), color);
            DrawQuad(new Rect(rect.x, rect.yMax - t, rect.width, t), color);
            DrawQuad(new Rect(rect.x, rect.y, t, rect.height), color);
            DrawQuad(new Rect(rect.xMax - t, rect.y, t, rect.height), color);
        }
    }
}
