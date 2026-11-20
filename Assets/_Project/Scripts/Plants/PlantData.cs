using UnityEngine;

namespace AfkIsland.Plants
{
    [CreateAssetMenu(fileName = "PlantData", menuName = "AFK Island/Plant")]
    public sealed class PlantData : ScriptableObject
    {
        public string plantId = "oak_starter";
        public string displayName = "Starter Tree";

        [Tooltip("Visuals for each growth stage. Index 0 = seed.")]
        public GameObject[] growthStagePrefabs;

        [Tooltip("Seconds required to finish each stage and advance to the next.")]
        public float[] stageDurationsSeconds = { 15f, 30f, 45f, 60f };

        public int MaxStageIndex =>
            growthStagePrefabs == null || growthStagePrefabs.Length == 0
                ? 0
                : growthStagePrefabs.Length - 1;

        public float GetStageDuration(int stageIndex)
        {
            if (stageDurationsSeconds == null || stageDurationsSeconds.Length == 0)
            {
                return 30f;
            }

            var i = Mathf.Clamp(stageIndex, 0, stageDurationsSeconds.Length - 1);
            return Mathf.Max(0.1f, stageDurationsSeconds[i]);
        }
    }
}
