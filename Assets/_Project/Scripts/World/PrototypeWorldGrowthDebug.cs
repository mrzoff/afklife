using System;
using AfkIsland.Animals;
using AfkIsland.Fishing;
using AfkIsland.Offline;
using AfkIsland.Save;
using AfkIsland.UI;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Temporary prototype control to demo mesh expansion without real-world wait.
    /// Remove or gate behind a define once balance playtests start.
    /// </summary>
    public sealed class PrototypeWorldGrowthDebug : MonoBehaviour
    {
        public const bool Enabled = true;

        [SerializeField] private SaveService saveService;
        [SerializeField] private ChunkManager chunkManager;
        [SerializeField] private IslandMeshVisualizer meshVisualizer;
        [SerializeField] private HomeSettlementVisualizer settlementVisualizer;
        [SerializeField] private IslandExpansionFeedback expansionFeedback;
        [SerializeField] private WorldGrowthConfig config = new();

        private readonly WorldGrowthSimulation _simulation = new();
        private string _lastMessage;

        /// <summary>IMGUI panel in Input-System screen space (origin bottom-left).</summary>
        private static Rect _panelScreenRect;

        /// <summary>Frames remaining where world fishing must ignore pointer (OnGUI click-through).</summary>
        private static int _blockWorldPointerFrames;

        public void Configure(
            SaveService save,
            ChunkManager chunks,
            IslandMeshVisualizer mesh,
            IslandExpansionFeedback feedback,
            WorldGrowthConfig growthConfig = null,
            HomeSettlementVisualizer settlement = null)
        {
            saveService = save;
            chunkManager = chunks;
            meshVisualizer = mesh;
            expansionFeedback = feedback;
            settlementVisualizer = settlement;
            if (growthConfig != null)
            {
                config = growthConfig;
            }
        }

        /// <summary>True when pointer is over debug panel or a panel click was just consumed.</summary>
        public static bool BlocksWorldPointer(Vector2 inputScreenPos)
        {
            if (!Enabled)
            {
                return false;
            }

            if (_blockWorldPointerFrames > 0)
            {
                return true;
            }

            if (_panelScreenRect.width < 1f || _panelScreenRect.height < 1f)
            {
                return false;
            }

            return _panelScreenRect.Contains(inputScreenPos);
        }

        private void LateUpdate()
        {
            if (_blockWorldPointerFrames > 0)
            {
                _blockWorldPointerFrames--;
            }
        }

        private void OnGUI()
        {
            if (!Enabled || saveService == null)
            {
                return;
            }

            var w = 220f;
            var h = 44f;
            var gap = 8f;
            var rows = 6;
            var panelH = rows * h + (rows - 1) * gap + 24f;
            // Left side, below EcosystemHud (~12+56) — keep away from ocean + Collection.
            var x = 16f;
            var top = 76f;
            var panelGui = new Rect(x - 8f, top - 8f, w + 16f, panelH);

            // Input System uses bottom-left origin; IMGUI uses top-left.
            _panelScreenRect = new Rect(
                panelGui.x,
                Screen.height - panelGui.yMax,
                panelGui.width,
                panelGui.height);

            GUI.Box(panelGui, GUIContent.none);

            // Buttons first — never e.Use() before GUI.Button or clicks are eaten.
            var y = top;
            if (GUI.Button(new Rect(x, y, w, h), "Рыбалка: океан"))
            {
                _blockWorldPointerFrames = 5;
                RunOpenOcean();
            }

            y += h + gap;
            if (GUI.Button(new Rect(x, y, w, h), "Рыбалка: пруд"))
            {
                _blockWorldPointerFrames = 5;
                RunOpenPond();
            }

            y += h + gap;
            if (GUI.Button(new Rect(x, y, w, h), "Демо: +8д AFK"))
            {
                _blockWorldPointerFrames = 5;
                RunAdvance(TimeSpan.FromHours(8 * 24));
            }

            y += h + gap;
            if (GUI.Button(new Rect(x, y, w, h), "Демо: +3д AFK"))
            {
                _blockWorldPointerFrames = 5;
                RunAdvance(TimeSpan.FromHours(72));
            }

            y += h + gap;
            if (GUI.Button(new Rect(x, y, w, h), "Демо: +24ч AFK"))
            {
                _blockWorldPointerFrames = 5;
                RunAdvance(TimeSpan.FromHours(24));
            }

            y += h + gap;
            if (GUI.Button(new Rect(x, y, w, h), "Демо: остров +8ч AFK"))
            {
                _blockWorldPointerFrames = 5;
                RunAdvance(TimeSpan.FromHours(8));
            }

            if (!string.IsNullOrEmpty(_lastMessage))
            {
                GUI.Label(new Rect(x, y + h + 10f, w + 40f, 48f), _lastMessage);
            }

            // Gaps / chrome: block world fishing only after buttons had a chance at the event.
            var e = Event.current;
            if (e != null &&
                panelGui.Contains(e.mousePosition) &&
                e.type != EventType.Used &&
                (e.rawType == EventType.MouseDown ||
                 e.rawType == EventType.MouseUp ||
                 e.rawType == EventType.MouseDrag))
            {
                _blockWorldPointerFrames = 3;
                e.Use();
            }
        }

        private void RunOpenPond()
        {
            var fishing = FindFirstObjectByType<FishingController>();
            var pondIn = FindFirstObjectByType<FishingPondInput>();
            if (fishing == null)
            {
                _lastMessage = "Нет FishingController";
                return;
            }

            pondIn?.SetInputEnabled(true);
            var ok = fishing.TryOpenFromPond();
            _lastMessage = ok
                ? "Пруд: UI открыт (кнопка)"
                : $"Пруд: TryOpen fail state={fishing.State}";
            Debug.Log("[AFK Island] Debug button TryOpenFromPond=" + ok);
        }

        private void RunOpenOcean()
        {
            var fishing = FindFirstObjectByType<FishingController>();
            var oceanIn = FindFirstObjectByType<FishingOceanInput>();
            if (fishing == null)
            {
                _lastMessage = "Нет FishingController";
                return;
            }

            oceanIn?.SetInputEnabled(true);
            var ok = fishing.TryOpenFromOcean();
            _lastMessage = ok
                ? "Океан: UI открыт (кнопка)"
                : $"Океан: TryOpen fail state={fishing.State}";
            Debug.Log("[AFK Island] Debug button TryOpenFromOcean=" + ok);
        }

        private void RunAdvance(TimeSpan elapsed)
        {
            var data = saveService.AuthoritativeSnapshot;
            if (data == null)
            {
                if (!saveService.TryRead(out data) || data == null)
                {
                    _lastMessage = "Нет сейва для демо-роста.";
                    return;
                }
            }

            if (config == null)
            {
                config = WorldGrowthConfig.CreateDefault();
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var leaveUtc = now - (long)Math.Max(0, elapsed.TotalSeconds);
            var seed = SaveClone.DeepClone(data);
            if (seed.worldGrowth == null)
            {
                seed.worldGrowth = SaveMigrationService.CreateDefaultWorldGrowth(leaveUtc);
            }

            seed.worldGrowth.lastSimulationUtc = leaveUtc;
            seed.lastSaveUtc = leaveUtc;

            var result = _simulation.Simulate(seed, elapsed, config);
            if (!result.success || result.updatedSave == null)
            {
                _lastMessage = $"Демо-рост не удался: {result.errorCode}";
                return;
            }

            var working = result.updatedSave;
            working.worldGrowth.lastSimulationUtc = leaveUtc + result.appliedSeconds;
            working.lastSaveUtc = now;

            SaveMigrationService.EnsureEcosystemWeatherAnimals(working);
            var livingRng = new DeterministicRng(
                unchecked((uint)(working.worldGrowth.growthSeed ^ (int)result.appliedSeconds ^ 0xEC05)));
            var offlineGrowth = FindFirstObjectByType<OfflineGrowthService>();
            var nature = LivingOfflineNaturePass.Apply(
                working, result.appliedSeconds, livingRng, offlineGrowth, result.appliedSeconds);
            var living = nature.living ?? new LivingWorldResult();
            working.worldGrowth.growthSeed = unchecked((int)livingRng.State);

            SettlementSimulation.RebalanceExcessStones(working);
            FaunaSimulation.EnsureAnimalsOnLand(working);

            if (!saveService.TryWrite(working, out var error))
            {
                _lastMessage = $"Демо: запись сейва не удалась ({error})";
                return;
            }

            chunkManager?.ApplyLogicalState(working);
            saveService.SetAuthoritative(working);
            offlineGrowth?.RestoreVisualsFromCommitted(working);
            meshVisualizer?.RebuildFromSave(working);
            settlementVisualizer?.RebuildFromSave(working);
            expansionFeedback?.RefreshFromSession();
            FindFirstObjectByType<AnimalSpawner>()?.RestoreFromSave(working);
            FindFirstObjectByType<DayNightWeatherVisual>()?.ApplyFromSave(working);
            FindFirstObjectByType<WorldVisualEnvironment>()?.ApplyFromSave(working);
            var faunaN = living.fauna != null ? living.fauna.TotalSpawned : 0;
            var faunaBit = faunaN > 0 ? $", фауна +{faunaN}" : string.Empty;
            var cellBit = living.cellsAdvancedByFauna > 0
                ? $", природа +{living.cellsAdvancedByFauna}"
                : string.Empty;
            var plantsSpawned = nature.colonization?.plantsSpawned ?? 0;
            var plantBit = plantsSpawned > 0 ? $", растений +{plantsSpawned}" : string.Empty;
            var settleAdv = nature.settlement?.cellsAdvanced ?? 0;
            var settleBit = settleAdv > 0 ? $", поселение +{settleAdv}" : string.Empty;
            var plantCount = CountPlants(working);
            var catchUp = Math.Max(0, (long)elapsed.TotalSeconds - result.appliedSeconds);
            var catchBit = catchUp > 0
                ? $", {AfkDurationFormat.FormatCatchUpTriple((long)elapsed.TotalSeconds, result.appliedSeconds, catchUp)}"
                : string.Empty;
            _lastMessage =
                $"Демо: земля +{result.expandedArea:0.#}, участков +{result.chunksCreated}{plantBit}{settleBit}" +
                $" (всего {plantCount}){faunaBit}{cellBit}{catchBit}";
            var colonizeLine = nature.colonization != null ? nature.colonization.ToReportLine() : string.Empty;
            var settleLine = nature.settlement != null ? nature.settlement.ToReportLine() : string.Empty;
            Debug.Log(
                "[AFK Island] " + _lastMessage +
                $"\nWatermark demo: leaveUtc={leaveUtc} afterUtc={working.worldGrowth.lastSimulationUtc} now={now}" +
                "\n" + result.ToReportText() +
                (string.IsNullOrEmpty(colonizeLine) ? string.Empty : "\n" + colonizeLine) +
                (string.IsNullOrEmpty(settleLine) ? string.Empty : "\n" + settleLine) +
                "\n" + SettlementSimulation.ToStatusLine(working) +
                "\n" + living.ToReportLine());
        }

        private static int CountPlants(GameSaveData save)
        {
            return save?.plants != null ? save.plants.Count : 0;
        }
    }
}
