using AfkIsland.Collection;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.Fishing
{
    /// <summary>
    /// Contract 0.3 Stage 6: one atomic logical apply for a successful catch Result.
    /// Order: RegisterDiscovery first, then totalCatches++. If discovery fails, catches stay unchanged.
    /// Caller must invoke at most once per successful Result attempt.
    /// </summary>
    public static class FishingCatchProgress
    {
        public static bool TryApply(
            SaveService saveService,
            CollectionService collection,
            string fishId)
        {
            if (saveService == null || collection == null || string.IsNullOrWhiteSpace(fishId))
            {
                return false;
            }

            var save = saveService.AuthoritativeSnapshot;
            if (save == null)
            {
                Debug.LogWarning(
                    "[AFK Island] FishingCatchProgress ignored: authoritative save missing.");
                return false;
            }

            SaveMigrationService.EnsureCollectionAndFishingDefaults(save);

            var catchesBefore = save.fishing.totalCatches;
            if (catchesBefore < 0)
            {
                catchesBefore = 0;
                save.fishing.totalCatches = 0;
            }

            var discoveryBefore = collection.GetDiscoveryCount(fishId);

            if (!collection.RegisterDiscovery(fishId))
            {
                return false;
            }

            var discoveryAfter = collection.GetDiscoveryCount(fishId);
            var discoveryAdvanced =
                discoveryAfter > discoveryBefore ||
                (discoveryBefore == int.MaxValue && discoveryAfter == int.MaxValue);

            if (!discoveryAdvanced)
            {
                Debug.LogError(
                    "[AFK Island] FishingCatchProgress: discovery did not advance; " +
                    "skipping totalCatches to avoid split state.");
                return false;
            }

            if (save.fishing.totalCatches < int.MaxValue)
            {
                save.fishing.totalCatches = catchesBefore + 1;
            }

            return true;
        }
    }
}
