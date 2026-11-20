using System.Collections.Generic;
using UnityEngine;

namespace AfkIsland.Fishing
{
    [CreateAssetMenu(fileName = "FishCatalog", menuName = "AFK Island/Fish Catalog")]
    public sealed class FishCatalog : ScriptableObject
    {
        [SerializeField] private List<FishData> fish = new();

        private Dictionary<string, FishData> _byId;
        private bool _indexBuilt;

        public IReadOnlyList<FishData> Fish => fish;

        public void Configure(IEnumerable<FishData> fishList)
        {
            fish = fishList != null ? new List<FishData>(fishList) : new List<FishData>();
            _byId = null;
            _indexBuilt = false;
            EnsureIndex();
        }

        public bool TryGetById(string fishId, out FishData data)
        {
            EnsureIndex();
            if (string.IsNullOrWhiteSpace(fishId))
            {
                data = null;
                return false;
            }

            return _byId.TryGetValue(fishId, out data) && data != null;
        }

        public FishData GetByIdOrNull(string fishId)
        {
            return TryGetById(fishId, out var data) ? data : null;
        }

        /// <summary>
        /// P0 ocean catch-pool source: unique fishId, ocean/both water, catchWeight &gt; 0.
        /// Does not apply weather/time filters (FishingSimulation does that later).
        /// </summary>
        public void CollectOceanEligible(List<FishData> into)
        {
            CollectEligible(into, ocean: true);
        }

        /// <summary>
        /// Stage 10 pond catch-pool source: unique fishId, <see cref="FishData.IsPondEligible"/>, catchWeight &gt; 0.
        /// Does not apply weather/time filters (FishingSimulation does that later).
        /// </summary>
        public void CollectPondEligible(List<FishData> into)
        {
            CollectEligible(into, ocean: false);
        }

        private void CollectEligible(List<FishData> into, bool ocean)
        {
            if (into == null)
            {
                return;
            }

            into.Clear();
            EnsureIndex();
            if (fish == null)
            {
                return;
            }

            var added = new HashSet<string>();
            foreach (var entry in fish)
            {
                if (entry == null ||
                    string.IsNullOrWhiteSpace(entry.fishId) ||
                    !entry.HasPositiveCatchWeight)
                {
                    continue;
                }

                if (ocean)
                {
                    if (!entry.IsOceanEligible)
                    {
                        continue;
                    }
                }
                else if (!entry.IsPondEligible)
                {
                    continue;
                }

                // Only indexed (first) instance of each fishId.
                if (!_byId.TryGetValue(entry.fishId, out var canonical) || canonical != entry)
                {
                    continue;
                }

                if (!added.Add(entry.fishId))
                {
                    continue;
                }

                into.Add(entry);
            }
        }

        /// <summary>
        /// Returns false if duplicate fishId, empty ids, or unknown weatherConditions exist.
        /// </summary>
        public bool TryValidateIntegrity(out string error)
        {
            error = null;
            if (fish == null)
            {
                return true;
            }

            var seen = new HashSet<string>();
            foreach (var entry in fish)
            {
                if (entry == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.fishId))
                {
                    error = $"FishData '{entry.name}' has empty fishId.";
                    return false;
                }

                if (!seen.Add(entry.fishId))
                {
                    error = $"Duplicate fishId '{entry.fishId}' in FishCatalog.";
                    return false;
                }

                if (!entry.HasValidWeatherConditions(out var badWeather))
                {
                    error =
                        $"FishData '{entry.fishId}' has unknown weatherConditions '{badWeather}'.";
                    return false;
                }
            }

            return true;
        }

        private void OnEnable()
        {
            _byId = null;
            _indexBuilt = false;
        }

        private void EnsureIndex()
        {
            if (_indexBuilt && _byId != null)
            {
                return;
            }

            _byId = new Dictionary<string, FishData>();
            if (fish == null)
            {
                fish = new List<FishData>();
            }

            var seen = new HashSet<string>();
            foreach (var entry in fish)
            {
                if (entry == null)
                {
                    Debug.LogWarning("[AFK Island] FishCatalog contains a null FishData entry.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.fishId))
                {
                    Debug.LogWarning($"[AFK Island] FishData '{entry.name}' has empty fishId.");
                    continue;
                }

                if (!seen.Add(entry.fishId))
                {
                    Debug.LogError(
                        $"[AFK Island] FishCatalog duplicate fishId '{entry.fishId}'. Keeping first entry.");
                    continue;
                }

                _byId.Add(entry.fishId, entry);
            }

            _indexBuilt = true;
        }
    }
}
