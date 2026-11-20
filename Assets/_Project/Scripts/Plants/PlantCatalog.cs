using System.Collections.Generic;
using UnityEngine;

namespace AfkIsland.Plants
{
    [CreateAssetMenu(fileName = "PlantCatalog", menuName = "AFK Island/Plant Catalog")]
    public sealed class PlantCatalog : ScriptableObject
    {
        [SerializeField] private List<PlantData> plants = new();

        private Dictionary<string, PlantData> _byId;
        private bool _indexBuilt;

        public IReadOnlyList<PlantData> Plants => plants;

        public void Configure(IEnumerable<PlantData> plantList)
        {
            plants = plantList != null ? new List<PlantData>(plantList) : new List<PlantData>();
            _byId = null;
            _indexBuilt = false;
            EnsureIndex();
        }

        public bool TryGetById(string plantId, out PlantData data)
        {
            EnsureIndex();
            if (string.IsNullOrWhiteSpace(plantId))
            {
                data = null;
                return false;
            }

            return _byId.TryGetValue(plantId, out data) && data != null;
        }

        public PlantData GetByIdOrNull(string plantId)
        {
            return TryGetById(plantId, out var data) ? data : null;
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

            _byId = new Dictionary<string, PlantData>();
            if (plants == null)
            {
                plants = new List<PlantData>();
            }

            var seen = new HashSet<string>();
            foreach (var plant in plants)
            {
                if (plant == null)
                {
                    Debug.LogWarning("[AFK Island] PlantCatalog contains a null PlantData entry.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(plant.plantId))
                {
                    Debug.LogWarning(
                        $"[AFK Island] PlantData '{plant.name}' has empty plantId.");
                    continue;
                }

                if (!seen.Add(plant.plantId))
                {
                    Debug.LogError(
                        $"[AFK Island] PlantCatalog duplicate plantId '{plant.plantId}'. Keeping first entry.");
                    continue;
                }

                _byId.Add(plant.plantId, plant);
            }

            _indexBuilt = true;
        }
    }
}
