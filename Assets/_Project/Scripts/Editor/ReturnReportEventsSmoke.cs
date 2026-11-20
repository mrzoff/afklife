#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AfkIsland.Save;
using AfkIsland.UI;
using AfkIsland.World;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    /// <summary>Stage 11C: Events section presenter — 0/1/3/5 cards, plural, unknown id.</summary>
    public static class ReturnReportEventsSmoke
    {
        private static readonly string ResultPath =
            Path.Combine("Builds", "Share", "return_report_events_smoke_result.txt");

        [MenuItem("AFK Island/Smoke Return Report Events 11C")]
        public static void RunMenu()
        {
            var code = RunInternal();
            if (code != 0)
            {
                throw new Exception("ReturnReportEvents smoke failed with code " + code);
            }

            Debug.Log("[AFK Island] ReturnReportEvents 11C smoke OK.");
        }

        public static void RunBatch()
        {
            EditorApplication.Exit(RunInternal());
        }

        private static int RunInternal()
        {
            var log = new StringBuilder();
            try
            {
                // Pluralization
                if (ReturnReportEventsPresenter.FormatCountLine(1) != "1 новое событие" ||
                    ReturnReportEventsPresenter.FormatCountLine(2) != "2 новых события" ||
                    ReturnReportEventsPresenter.FormatCountLine(3) != "3 новых события" ||
                    ReturnReportEventsPresenter.FormatCountLine(4) != "4 новых события" ||
                    ReturnReportEventsPresenter.FormatCountLine(5) != "5 новых событий" ||
                    ReturnReportEventsPresenter.FormatCountLine(21) != "21 новое событие")
                {
                    return Fail(2, "Russian plural count lines mismatch.", log);
                }

                log.AppendLine("plural=PASS");

                // 0 events
                var empty = ReturnReportEventsPresenter.Build(new ReturnReportData());
                if (empty.HasEvents || empty.Cards.Count != 0 ||
                    !string.IsNullOrEmpty(empty.CountLineRu) ||
                    !string.IsNullOrEmpty(empty.OverflowLineRu))
                {
                    return Fail(3, "Empty report must have no count/cards.", log);
                }

                if (ReturnReportEventsPresenter.EmptyStateRu.IndexOf("спокойно", StringComparison.Ordinal) < 0)
                {
                    return Fail(4, "Empty-state copy missing.", log);
                }

                log.AppendLine("empty=PASS");

                // 1 event
                var one = ReturnReportEventsPresenter.Build(MakeReport(
                    MakeEvt(WorldEventIds.MossCover, "Новая территория покрылась мхом", "Деталь мха.")));
                if (!one.HasEvents || one.TotalCount != 1 || one.Cards.Count != 1 ||
                    one.CountLineRu != "1 новое событие" ||
                    one.Cards[0].TypeRu != "Новая природная зона" ||
                    one.Cards[0].TitleRu != "Новая территория покрылась мхом" ||
                    !string.IsNullOrEmpty(one.OverflowLineRu))
                {
                    return Fail(5, "Single-event section mismatch.", log);
                }

                log.AppendLine("one=PASS");

                // 3 events — order preserved
                var three = ReturnReportEventsPresenter.Build(MakeReport(
                    MakeEvt(WorldEventIds.MossCover, "A", "a"),
                    MakeEvt(WorldEventIds.YoungPlants, "B", "b"),
                    MakeEvt(WorldEventIds.FertileLand, "C", "c")));
                if (three.TotalCount != 3 || three.Cards.Count != 3 ||
                    three.CountLineRu != "3 новых события" ||
                    three.Cards[0].TitleRu != "A" ||
                    three.Cards[1].TitleRu != "B" ||
                    three.Cards[2].TitleRu != "C" ||
                    three.Cards[2].TypeRu != "Расширение острова")
                {
                    return Fail(6, "Three-event order/type mismatch.", log);
                }

                log.AppendLine("three=PASS");

                // 5 events — overflow after 4
                var five = ReturnReportEventsPresenter.Build(MakeReport(
                    MakeEvt(WorldEventIds.MossCover, "1", ""),
                    MakeEvt(WorldEventIds.YoungPlants, "2", ""),
                    MakeEvt(WorldEventIds.ExtraTrees, "3", ""),
                    MakeEvt(WorldEventIds.RockZone, "4", ""),
                    MakeEvt(WorldEventIds.FertileLand, "5", "")));
                if (five.TotalCount != 5 || five.Cards.Count != 4 ||
                    five.CountLineRu != "5 новых событий" ||
                    five.OverflowLineRu.IndexOf("ещё 1", StringComparison.Ordinal) < 0 ||
                    five.Cards[3].TitleRu != "4")
                {
                    return Fail(7, "Five-event overflow mismatch.", log);
                }

                log.AppendLine("five_overflow=PASS");

                // Unknown id
                ReturnReportEventsPresenter.ResolvePresentation("totally_unknown", out var type, out _, out _);
                if (type != ReturnReportEventsPresenter.UnknownTypeRu)
                {
                    return Fail(8, "Unknown eventId must map to Событие острова.", log);
                }

                var unk = ReturnReportEventsPresenter.ToCard(new NatureEventSaveData
                {
                    eventId = "weird_seed",
                    titleRu = "Странное",
                    detailRu = "ok",
                    chunkX = 1,
                    chunkZ = -1
                });
                if (unk.TypeRu != ReturnReportEventsPresenter.UnknownTypeRu || unk.ChunkX != 1 || unk.ChunkZ != -1)
                {
                    return Fail(9, "Unknown card presentation / location fields failed.", log);
                }

                log.AppendLine("unknown_id=PASS");
                log.AppendLine("source=report.events_only");
                log.AppendLine("save_write=none");
                log.AppendLine("PASS");
                WriteResult(0, log.ToString());
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError("[AFK Island] ReturnReportEvents smoke exception: " + e);
                WriteResult(1, "FAIL 1\n" + e);
                return 1;
            }
        }

        private static ReturnReportData MakeReport(params NatureEventSaveData[] events)
        {
            return new ReturnReportData
            {
                events = new List<NatureEventSaveData>(events)
            };
        }

        private static NatureEventSaveData MakeEvt(string id, string title, string detail)
        {
            return new NatureEventSaveData
            {
                eventId = id,
                instanceId = "smoke_" + id + "_" + title,
                titleRu = title,
                detailRu = detail,
                cellsAffected = 3,
                chunkX = 0,
                chunkZ = 1
            };
        }

        private static int Fail(int code, string message, StringBuilder log)
        {
            Debug.LogError($"[AFK Island] ReturnReportEvents smoke FAIL {code}: {message}");
            log?.AppendLine($"FAIL {code}: {message}");
            WriteResult(code, log?.ToString() ?? $"FAIL {code}\n");
            return code;
        }

        private static void WriteResult(int code, string body)
        {
            try
            {
                var dir = Path.GetDirectoryName(ResultPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var text = code == 0
                    ? (string.IsNullOrEmpty(body) ? "PASS\n" : body)
                    : body;
                if (!text.EndsWith("\n", StringComparison.Ordinal))
                {
                    text += "\n";
                }

                File.WriteAllText(ResultPath, text);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AFK Island] Could not write events smoke result: " + e.Message);
            }
        }
    }
}
#endif
