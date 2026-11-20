using System;
using System.Collections.Generic;
using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.Collection
{
    /// <summary>
    /// Sole discovery-progress API for Contract 0.3. Traits stay in catalogs; save holds progress only.
    /// </summary>
    public sealed class CollectionService : MonoBehaviour
    {
        [SerializeField] private SaveService saveService;

        /// <summary>Optional direct bind for tests / callers that already hold GameSaveData.</summary>
        private GameSaveData _boundSave;

        /// <summary>
        /// Wire to SaveService. Does not snapshot GameSaveData — each call reads
        /// <see cref="SaveService.AuthoritativeSnapshot"/> (post-migrate / post-load).
        /// Clears any test Bind so production always hits the save Capture/TryWrite uses.
        /// </summary>
        public void Configure(SaveService save)
        {
            saveService = save;
            _boundSave = null;
        }

        /// <summary>
        /// Bind an explicit save instance (editor/unit tests without SaveService).
        /// When SaveService has an AuthoritativeSnapshot, that snapshot wins in ResolveSave.
        /// Does not create a private progress store when save is null.
        /// </summary>
        public void Bind(GameSaveData save)
        {
            _boundSave = save;
            if (save != null)
            {
                SaveMigrationService.EnsureCollectionAndFishingDefaults(save);
            }
        }

        public void ClearBind()
        {
            _boundSave = null;
        }

        public bool RegisterDiscovery(string entryId)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return false;
            }

            var save = ResolveSave();
            if (save == null)
            {
                Debug.LogWarning(
                    "[AFK Island] CollectionService.RegisterDiscovery ignored: save not initialized.");
                return false;
            }

            SaveMigrationService.EnsureCollectionAndFishingDefaults(save);
            var entries = save.collection.entries;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var existing = FindEntry(entries, entryId);
            if (existing == null)
            {
                entries.Add(new CollectionEntrySaveData
                {
                    entryId = entryId.Trim(),
                    discovered = true,
                    discoveryCount = 1,
                    firstDiscoveredUtc = now,
                    lastDiscoveredUtc = now
                });
                return true;
            }

            existing.discovered = true;
            if (existing.discoveryCount < 0)
            {
                existing.discoveryCount = 0;
            }

            // Unchecked int wrap would go negative — clamp at MaxValue instead.
            if (existing.discoveryCount < int.MaxValue)
            {
                existing.discoveryCount += 1;
            }

            if (existing.firstDiscoveredUtc <= 0)
            {
                existing.firstDiscoveredUtc = now;
            }

            existing.lastDiscoveredUtc = now;
            return true;
        }

        public bool IsDiscovered(string entryId)
        {
            if (!TryFindLive(entryId, out var entry))
            {
                return false;
            }

            return entry.discovered;
        }

        public int GetDiscoveryCount(string entryId)
        {
            if (!TryFindLive(entryId, out var entry))
            {
                return 0;
            }

            return entry.discoveryCount < 0 ? 0 : entry.discoveryCount;
        }

        public bool TryGetEntry(string entryId, out CollectionEntrySaveData entry)
        {
            if (!TryFindLive(entryId, out var live))
            {
                entry = null;
                return false;
            }

            entry = SaveMigrationService.CloneCollectionEntry(live);
            return true;
        }

        /// <summary>Defensive copies — callers cannot mutate save through the returned list.</summary>
        public IReadOnlyList<CollectionEntrySaveData> GetEntries()
        {
            var save = ResolveSave();
            if (save?.collection?.entries == null)
            {
                return Array.Empty<CollectionEntrySaveData>();
            }

            var copy = new List<CollectionEntrySaveData>(save.collection.entries.Count);
            foreach (var e in save.collection.entries)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.entryId))
                {
                    continue;
                }

                copy.Add(SaveMigrationService.CloneCollectionEntry(e));
            }

            return copy;
        }

        private bool TryFindLive(string entryId, out CollectionEntrySaveData entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return false;
            }

            var save = ResolveSave();
            if (save?.collection?.entries == null)
            {
                return false;
            }

            entry = FindEntry(save.collection.entries, entryId);
            return entry != null;
        }

        private GameSaveData ResolveSave()
        {
            // Prefer live authoritative (after load/migrate/offline apply), never an Awake-time copy.
            if (saveService != null)
            {
                var authoritative = saveService.AuthoritativeSnapshot;
                if (authoritative != null)
                {
                    return authoritative;
                }
            }

            return _boundSave;
        }

        private static CollectionEntrySaveData FindEntry(
            List<CollectionEntrySaveData> entries,
            string entryId)
        {
            if (entries == null || string.IsNullOrWhiteSpace(entryId))
            {
                return null;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e != null &&
                    string.Equals(e.entryId, entryId, StringComparison.Ordinal))
                {
                    return e;
                }
            }

            // Also accept trim-equal match for legacy whitespace ids without creating new rows.
            var trimmed = entryId.Trim();
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e != null &&
                    !string.IsNullOrWhiteSpace(e.entryId) &&
                    string.Equals(e.entryId.Trim(), trimmed, StringComparison.Ordinal))
                {
                    return e;
                }
            }

            return null;
        }
    }
}
