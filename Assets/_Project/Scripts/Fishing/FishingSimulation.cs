using System.Collections.Generic;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.Fishing
{
    /// <summary>
    /// Contract 0.3 Stage 4: ocean catch pool + weighted pick after a successful player reaction.
    /// Does not own FSM, UI, Collection, or Save.
    /// </summary>
    public static class FishingSimulation
    {
        private static readonly List<FishData> PoolScratch = new(16);

        /// <summary>
        /// Selects one ocean-eligible fish for the given weather/time. Call only after a successful bite reaction.
        /// </summary>
        public static bool TrySelectOceanCatch(
            FishCatalog catalog,
            string weatherId,
            float timeOfDay01,
            out FishData selectedFish,
            DeterministicRng rng = null)
        {
            return TrySelectCatch(
                catalog, weatherId, timeOfDay01, out selectedFish, rng, pond: false);
        }

        /// <summary>
        /// Stage 10: selects one pond-eligible fish. Call only after a successful bite reaction.
        /// </summary>
        public static bool TrySelectPondCatch(
            FishCatalog catalog,
            string weatherId,
            float timeOfDay01,
            out FishData selectedFish,
            DeterministicRng rng = null)
        {
            return TrySelectCatch(
                catalog, weatherId, timeOfDay01, out selectedFish, rng, pond: true);
        }

        /// <summary>Test/helper: fill <paramref name="into"/> with the current ocean catch pool.</summary>
        public static void CollectOceanCatchPool(
            FishCatalog catalog,
            string weatherId,
            float timeOfDay01,
            List<FishData> into)
        {
            CollectCatchPool(catalog, weatherId, timeOfDay01, into, pond: false);
        }

        /// <summary>Stage 10 test/helper: fill <paramref name="into"/> with the current pond catch pool.</summary>
        public static void CollectPondCatchPool(
            FishCatalog catalog,
            string weatherId,
            float timeOfDay01,
            List<FishData> into)
        {
            CollectCatchPool(catalog, weatherId, timeOfDay01, into, pond: true);
        }

        private static bool TrySelectCatch(
            FishCatalog catalog,
            string weatherId,
            float timeOfDay01,
            out FishData selectedFish,
            DeterministicRng rng,
            bool pond)
        {
            selectedFish = null;
            if (catalog == null)
            {
                return false;
            }

            if (float.IsNaN(timeOfDay01) || float.IsInfinity(timeOfDay01))
            {
                return false;
            }

            BuildCatchPool(catalog, weatherId, timeOfDay01, PoolScratch, pond);
            if (PoolScratch.Count == 0)
            {
                return false;
            }

            rng ??= new DeterministicRng(unchecked((uint)(Time.frameCount * 397) ^ 0xF15C_AFEEu));
            selectedFish = PickWeighted(PoolScratch, rng);
            return selectedFish != null;
        }

        private static void CollectCatchPool(
            FishCatalog catalog,
            string weatherId,
            float timeOfDay01,
            List<FishData> into,
            bool pond)
        {
            if (into == null)
            {
                return;
            }

            into.Clear();
            if (catalog == null || float.IsNaN(timeOfDay01) || float.IsInfinity(timeOfDay01))
            {
                return;
            }

            BuildCatchPool(catalog, weatherId, timeOfDay01, into, pond);
        }

        public static bool IsValidCatchWeight(float catchWeight)
        {
            return catchWeight > 0f &&
                   !float.IsNaN(catchWeight) &&
                   !float.IsInfinity(catchWeight);
        }

        private static void BuildCatchPool(
            FishCatalog catalog,
            string weatherId,
            float timeOfDay01,
            List<FishData> into,
            bool pond)
        {
            into.Clear();
            if (pond)
            {
                catalog.CollectPondEligible(into);
            }
            else
            {
                catalog.CollectOceanEligible(into);
            }

            for (var i = into.Count - 1; i >= 0; i--)
            {
                var fish = into[i];
                var waterOk = pond ? fish != null && fish.IsPondEligible : fish != null && fish.IsOceanEligible;
                if (fish == null ||
                    string.IsNullOrWhiteSpace(fish.fishId) ||
                    !waterOk ||
                    !IsValidCatchWeight(fish.catchWeight) ||
                    !fish.MatchesWeather(weatherId) ||
                    !fish.MatchesTimeOfDay(timeOfDay01))
                {
                    into.RemoveAt(i);
                }
            }
        }

        private static FishData PickWeighted(List<FishData> pool, DeterministicRng rng)
        {
            if (pool == null || pool.Count == 0 || rng == null)
            {
                return null;
            }

            if (pool.Count == 1)
            {
                return pool[0];
            }

            double sum = 0;
            for (var i = 0; i < pool.Count; i++)
            {
                var w = pool[i] != null ? pool[i].catchWeight : 0f;
                if (IsValidCatchWeight(w))
                {
                    sum += w;
                }
            }

            if (sum <= 0d)
            {
                return null;
            }

            var roll = rng.NextDouble() * sum;
            double acc = 0;
            FishData lastValid = null;
            for (var i = 0; i < pool.Count; i++)
            {
                var fish = pool[i];
                if (fish == null || !IsValidCatchWeight(fish.catchWeight))
                {
                    continue;
                }

                lastValid = fish;
                acc += fish.catchWeight;
                if (roll <= acc)
                {
                    return fish;
                }
            }

            return lastValid;
        }
    }
}
