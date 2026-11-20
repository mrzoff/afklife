using System;
using AfkIsland.Collection;
using AfkIsland.Save;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.Fishing
{
    public enum FishingFsmState : byte
    {
        Closed = 0,
        Ready = 1,
        Wait = 2,
        Bite = 3,
        Result = 4
    }

    public enum FishingAttemptOutcome : byte
    {
        None = 0,
        Success = 1,
        Miss = 2,
        EmptyPool = 3,
        Cancelled = 4
    }

    /// <summary>Stage 10: which water body opened the current fishing session.</summary>
    public enum FishingWaterSource : byte
    {
        None = 0,
        Ocean = 1,
        Pond = 2
    }

    /// <summary>
    /// Contract 0.3 Stage 5–6 + Stage 10 pond entry: one FSM/UI; catch pool depends on open source.
    /// Miss / EmptyPool / Cancel never touch Collection or totalCatches.
    /// </summary>
    public sealed class FishingController : MonoBehaviour
    {
        [Header("Timing (seconds, unscaled)")]
        [SerializeField] private float waitMinSeconds = 1.5f;
        [SerializeField] private float waitMaxSeconds = 4f;
        [SerializeField] private float biteWindowSeconds = 2f;

        [SerializeField] private SaveService saveService;
        [SerializeField] private FishCatalog fishCatalog;

        private FishingUI _ui;
        private CollectionService _collection;
        private DeterministicRng _rng;
        private FishingFsmState _state = FishingFsmState.Closed;
        private FishingWaterSource _waterSource = FishingWaterSource.None;
        private float _stateEndsAtUnscaled;
        private bool _resultCommitted;
        private bool _simulationInvokedThisAttempt;
        private bool _catchProgressAppliedThisAttempt;
        private int _attemptSerial;
        private int _resultSerial = -1;
        private FishingAttemptOutcome _outcome;
        private FishData _selectedFish;
        private string _resultMessage = "";

        public FishingFsmState State => _state;
        public bool IsOpen => _state != FishingFsmState.Closed;
        public FishingWaterSource ActiveWaterSource => _waterSource;
        public FishingAttemptOutcome LastOutcome => _outcome;
        public FishData LastSelectedFish => _selectedFish;
        public string LastResultMessage => _resultMessage;
        public bool SimulationInvokedThisAttempt => _simulationInvokedThisAttempt;
        public bool CatchProgressAppliedThisAttempt => _catchProgressAppliedThisAttempt;
        public int AttemptSerial => _attemptSerial;

        public void Configure(
            SaveService save,
            FishCatalog catalog,
            FishingUI ui,
            CollectionService collection = null,
            DeterministicRng rng = null)
        {
            saveService = save;
            fishCatalog = catalog;
            _ui = ui;
            _collection = collection;
            _rng = rng ?? new DeterministicRng(unchecked((uint)Environment.TickCount));
            waitMinSeconds = Mathf.Max(0.25f, waitMinSeconds);
            waitMaxSeconds = Mathf.Max(waitMinSeconds, waitMaxSeconds);
            biteWindowSeconds = Mathf.Max(0.35f, biteWindowSeconds);
        }

        public void SetCatalog(FishCatalog catalog) => fishCatalog = catalog;

        public void SetRng(DeterministicRng rng)
        {
            if (rng != null)
            {
                _rng = rng;
            }
        }

        /// <summary>Ocean tap entry. Ignored if already open.</summary>
        public bool TryOpenFromOcean() => TryOpen(FishingWaterSource.Ocean);

        /// <summary>Stage 10 pond tap entry. Ignored if already open.</summary>
        public bool TryOpenFromPond() => TryOpen(FishingWaterSource.Pond);

        private bool TryOpen(FishingWaterSource source)
        {
            if (_state != FishingFsmState.Closed || source == FishingWaterSource.None)
            {
                return false;
            }

            _waterSource = source;
            EnterReady();
            return true;
        }

        public void RequestCast()
        {
            if (_state != FishingFsmState.Ready)
            {
                return;
            }

            BeginAttempt();
        }

        public void RequestReact()
        {
            if (_state != FishingFsmState.Bite || _resultCommitted)
            {
                return;
            }

            CommitSuccess();
        }

        public void RequestAgain()
        {
            if (_state != FishingFsmState.Result)
            {
                return;
            }

            EnterReady();
        }

        public void RequestClose()
        {
            if (_state == FishingFsmState.Closed)
            {
                return;
            }

            if (_state is FishingFsmState.Wait or FishingFsmState.Bite)
            {
                _outcome = FishingAttemptOutcome.Cancelled;
                _selectedFish = null;
                _resultMessage = "";
                // No simulation / no save side effects on cancel.
            }

            EnterClosed();
        }

        private void Update()
        {
            if (_state == FishingFsmState.Wait)
            {
                if (Time.unscaledTime >= _stateEndsAtUnscaled)
                {
                    EnterBite();
                }

                return;
            }

            if (_state == FishingFsmState.Bite && !_resultCommitted)
            {
                if (Time.unscaledTime >= _stateEndsAtUnscaled)
                {
                    CommitMiss("Клёв упущен.");
                }
            }
        }

        private void BeginAttempt()
        {
            _attemptSerial++;
            _resultCommitted = false;
            _simulationInvokedThisAttempt = false;
            _catchProgressAppliedThisAttempt = false;
            _outcome = FishingAttemptOutcome.None;
            _selectedFish = null;
            _resultMessage = "";
            _resultSerial = -1;

            _rng ??= new DeterministicRng(unchecked((uint)Environment.TickCount) ^ (uint)_attemptSerial);
            var span = waitMaxSeconds - waitMinSeconds;
            var wait = waitMinSeconds + (float)(_rng.NextDouble() * span);
            _state = FishingFsmState.Wait;
            _stateEndsAtUnscaled = Time.unscaledTime + Mathf.Max(0.05f, wait);
            _ui?.PresentWait();
        }

        private void EnterBite()
        {
            if (_state != FishingFsmState.Wait || _resultCommitted)
            {
                return;
            }

            _state = FishingFsmState.Bite;
            _stateEndsAtUnscaled = Time.unscaledTime + biteWindowSeconds;
            _ui?.PresentBite();
        }

        private void CommitSuccess()
        {
            if (_resultCommitted || _state != FishingFsmState.Bite)
            {
                return;
            }

            _resultCommitted = true;
            _resultSerial = _attemptSerial;

            var weatherId = WeatherSimulation.Sunny;
            var timeOfDay = 0.5f;
            var snap = saveService != null ? saveService.AuthoritativeSnapshot : null;
            if (snap?.weather != null)
            {
                if (!string.IsNullOrWhiteSpace(snap.weather.weatherId))
                {
                    weatherId = snap.weather.weatherId;
                }

                timeOfDay = snap.weather.timeOfDay01;
            }

            _simulationInvokedThisAttempt = true;
            FishData fish = null;
            var selected = false;
            if (fishCatalog != null)
            {
                selected = _waterSource == FishingWaterSource.Pond
                    ? FishingSimulation.TrySelectPondCatch(
                        fishCatalog, weatherId, timeOfDay, out fish, _rng)
                    : FishingSimulation.TrySelectOceanCatch(
                        fishCatalog, weatherId, timeOfDay, out fish, _rng);
            }

            if (!selected || fish == null)
            {
                _outcome = FishingAttemptOutcome.EmptyPool;
                _selectedFish = null;
                _resultMessage = "Пока ничего не клюёт.";
            }
            else
            {
                _outcome = FishingAttemptOutcome.Success;
                _selectedFish = fish;
                _resultMessage = $"Улов: {fish.displayName}";
                ApplyCatchProgressOnce(fish);
            }

            EnterResult();
        }

        /// <summary>
        /// Stage 6: exactly one progress apply per successful attempt Result.
        /// </summary>
        private void ApplyCatchProgressOnce(FishData fish)
        {
            if (_catchProgressAppliedThisAttempt || fish == null)
            {
                return;
            }

            if (_collection == null || saveService == null)
            {
                return;
            }

            if (!FishingCatchProgress.TryApply(saveService, _collection, fish.fishId))
            {
                Debug.LogWarning(
                    "[AFK Island] Catch progress apply failed for " + fish.fishId);
                return;
            }

            _catchProgressAppliedThisAttempt = true;
            // CIF-A3: catch must not be lost to SaveNow debounce.
            saveService.SaveNow(force: true);
        }

        private void CommitMiss(string message)
        {
            if (_resultCommitted || _state != FishingFsmState.Bite)
            {
                return;
            }

            _resultCommitted = true;
            _resultSerial = _attemptSerial;
            _simulationInvokedThisAttempt = false;
            _outcome = FishingAttemptOutcome.Miss;
            _selectedFish = null;
            _resultMessage = string.IsNullOrWhiteSpace(message) ? "Промах." : message;
            EnterResult();
        }

        private void EnterReady()
        {
            _state = FishingFsmState.Ready;
            _resultCommitted = false;
            _simulationInvokedThisAttempt = false;
            _catchProgressAppliedThisAttempt = false;
            _outcome = FishingAttemptOutcome.None;
            _selectedFish = null;
            _resultMessage = "";
            _ui?.PresentReady();
        }

        private void EnterResult()
        {
            _state = FishingFsmState.Result;
            _ui?.PresentResult(_outcome, _selectedFish, _resultMessage);
        }

        private void EnterClosed()
        {
            _state = FishingFsmState.Closed;
            _waterSource = FishingWaterSource.None;
            _resultCommitted = true;
            _simulationInvokedThisAttempt = false;
            _catchProgressAppliedThisAttempt = false;
            _selectedFish = null;
            _resultMessage = "";
            _ui?.PresentClosed();
        }

#if UNITY_EDITOR
        /// <summary>Editor/smoke: force Wait timer to elapse immediately.</summary>
        public void DebugForceBiteNow()
        {
            if (_state == FishingFsmState.Wait)
            {
                _stateEndsAtUnscaled = Time.unscaledTime - 0.01f;
                EnterBite();
            }
        }

        public void DebugForceBiteTimeout()
        {
            if (_state == FishingFsmState.Bite && !_resultCommitted)
            {
                CommitMiss("Клёв упущен.");
            }
        }

        public void DebugSetTiming(float waitMin, float waitMax, float biteWindow)
        {
            waitMinSeconds = Mathf.Max(0.01f, waitMin);
            waitMaxSeconds = Mathf.Max(waitMinSeconds, waitMax);
            biteWindowSeconds = Mathf.Max(0.01f, biteWindow);
        }
#endif
    }
}
