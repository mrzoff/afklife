using System.Collections.Generic;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.UI
{
    /// <summary>
    /// Stage 11C: presentation-only mapping for Return Report event cards.
    /// Reads session <see cref="ReturnReportData.events"/> — never writes Save / re-sims.
    /// </summary>
    public static class ReturnReportEventsPresenter
    {
        public const int MaxVisibleCards = 4;
        public const string EmptyStateRu =
            "Особых событий не произошло — остров отдыхал спокойно.";
        public const string SubheadRu = "За время вашего отсутствия";
        public const string UnknownTypeRu = "Событие острова";

        public readonly struct EventCardView
        {
            public readonly string EventId;
            public readonly string TitleRu;
            public readonly string DetailRu;
            public readonly string TypeRu;
            public readonly string IconGlyph;
            public readonly Color IconColor;
            public readonly int ChunkX;
            public readonly int ChunkZ;
            public readonly float LocalX;
            public readonly float LocalZ;
            public readonly int CellsAffected;

            public EventCardView(
                string eventId,
                string titleRu,
                string detailRu,
                string typeRu,
                string iconGlyph,
                Color iconColor,
                int chunkX,
                int chunkZ,
                float localX,
                float localZ,
                int cellsAffected)
            {
                EventId = eventId ?? string.Empty;
                TitleRu = titleRu ?? string.Empty;
                DetailRu = detailRu ?? string.Empty;
                TypeRu = typeRu ?? UnknownTypeRu;
                IconGlyph = iconGlyph ?? "◆";
                IconColor = iconColor;
                ChunkX = chunkX;
                ChunkZ = chunkZ;
                LocalX = localX;
                LocalZ = localZ;
                CellsAffected = cellsAffected;
            }
        }

        public readonly struct EventsSectionView
        {
            public readonly int TotalCount;
            public readonly string CountLineRu;
            public readonly IReadOnlyList<EventCardView> Cards;
            public readonly string OverflowLineRu;

            public EventsSectionView(
                int totalCount,
                string countLineRu,
                IReadOnlyList<EventCardView> cards,
                string overflowLineRu)
            {
                TotalCount = totalCount;
                CountLineRu = countLineRu ?? string.Empty;
                Cards = cards ?? System.Array.Empty<EventCardView>();
                OverflowLineRu = overflowLineRu ?? string.Empty;
            }

            public bool HasEvents => TotalCount > 0;
        }

        /// <summary>Build section from session report events only (sim order preserved).</summary>
        public static EventsSectionView Build(ReturnReportData report)
        {
            var source = report?.events;
            if (source == null || source.Count == 0)
            {
                return new EventsSectionView(0, string.Empty, System.Array.Empty<EventCardView>(), string.Empty);
            }

            var cards = new List<EventCardView>(MaxVisibleCards);
            for (var i = 0; i < source.Count; i++)
            {
                if (cards.Count >= MaxVisibleCards)
                {
                    break;
                }

                var e = source[i];
                if (e == null)
                {
                    continue;
                }

                cards.Add(ToCard(e));
            }

            // Count non-null events for plural / overflow (nulls skipped in cards).
            var total = 0;
            for (var i = 0; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    total++;
                }
            }

            if (total <= 0)
            {
                return new EventsSectionView(0, string.Empty, System.Array.Empty<EventCardView>(), string.Empty);
            }

            var overflow = string.Empty;
            if (total > MaxVisibleCards)
            {
                var rest = total - MaxVisibleCards;
                overflow = $"…и ещё {rest} {EventNounForm(rest)}";
            }

            return new EventsSectionView(total, FormatCountLine(total), cards, overflow);
        }

        public static string FormatCountLine(int count)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            return $"{count} {NewEventsPhrase(count)}";
        }

        /// <summary>1 новое событие / 2–4 новых события / 5+ новых событий</summary>
        public static string NewEventsPhrase(int count)
        {
            var n = Mathf.Abs(count) % 100;
            var n1 = n % 10;
            if (n > 10 && n < 20)
            {
                return "новых событий";
            }

            if (n1 == 1)
            {
                return "новое событие";
            }

            if (n1 >= 2 && n1 <= 4)
            {
                return "новых события";
            }

            return "новых событий";
        }

        private static string EventNounForm(int count)
        {
            var n = Mathf.Abs(count) % 100;
            var n1 = n % 10;
            if (n > 10 && n < 20)
            {
                return "событий";
            }

            if (n1 == 1)
            {
                return "событие";
            }

            if (n1 >= 2 && n1 <= 4)
            {
                return "события";
            }

            return "событий";
        }

        public static EventCardView ToCard(NatureEventSaveData e)
        {
            ResolvePresentation(e?.eventId, out var typeRu, out var glyph, out var color);
            var title = !string.IsNullOrWhiteSpace(e?.titleRu) ? e.titleRu.Trim() : (e?.eventId ?? "событие");
            var detail = e?.detailRu?.Trim() ?? string.Empty;
            return new EventCardView(
                e?.eventId ?? string.Empty,
                title,
                detail,
                typeRu,
                glyph,
                color,
                e?.chunkX ?? 0,
                e?.chunkZ ?? 0,
                e?.localX ?? 0f,
                e?.localZ ?? 0f,
                e?.cellsAffected ?? 0);
        }

        public static void ResolvePresentation(
            string eventId,
            out string typeRu,
            out string iconGlyph,
            out Color iconColor)
        {
            switch (eventId)
            {
                case WorldEventIds.MossCover:
                    typeRu = "Новая природная зона";
                    iconGlyph = "◍";
                    iconColor = new Color(0.45f, 0.78f, 0.55f);
                    return;
                case WorldEventIds.UnusualFlora:
                    typeRu = "Новая природная зона";
                    iconGlyph = "❁";
                    iconColor = new Color(0.55f, 0.85f, 0.65f);
                    return;
                case WorldEventIds.YoungPlants:
                    typeRu = "Природное развитие";
                    iconGlyph = "⁕";
                    iconColor = new Color(0.5f, 0.82f, 0.4f);
                    return;
                case WorldEventIds.ExtraTrees:
                    typeRu = "Природное развитие";
                    iconGlyph = "♣";
                    iconColor = new Color(0.35f, 0.7f, 0.4f);
                    return;
                case WorldEventIds.RockZone:
                    typeRu = "Изменение ландшафта";
                    iconGlyph = "▲";
                    iconColor = new Color(0.7f, 0.68f, 0.55f);
                    return;
                case WorldEventIds.FertileLand:
                    typeRu = "Расширение острова";
                    iconGlyph = "▣";
                    iconColor = new Color(0.75f, 0.62f, 0.35f);
                    return;
                default:
                    typeRu = UnknownTypeRu;
                    iconGlyph = "◆";
                    iconColor = new Color(0.65f, 0.72f, 0.68f);
                    return;
            }
        }
    }
}
