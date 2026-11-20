using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.Plants
{
    /// <summary>
    /// Optional realtime growth while the app is open. AFK still remains the main loop.
    /// </summary>
    public sealed class OnlineGrowthTicker : MonoBehaviour
    {
        [SerializeField] private PlantingSystem plantingSystem;
        [SerializeField] private bool enabledInPlay = true;
        [SerializeField] private float autosaveIntervalSeconds = 5f;

        private float _autosaveTimer;

        public void Configure(PlantingSystem planting)
        {
            plantingSystem = planting;
        }

        private void Update()
        {
            if (!enabledInPlay || plantingSystem == null)
            {
                return;
            }

            var dt = Time.deltaTime;
            foreach (var plot in plantingSystem.Plots)
            {
                plot?.CurrentPlant?.ApplyOfflineSeconds(dt);
            }

            _autosaveTimer += dt;
            if (_autosaveTimer >= autosaveIntervalSeconds)
            {
                _autosaveTimer = 0f;
                SaveService.Instance?.SaveNow();
            }
        }
    }
}