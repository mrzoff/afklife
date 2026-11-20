using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Day/night on the main directional light and ambient.
    /// Package D: fog weather is a distant haze. Rain, fog, and wind do not recolor the light.
    /// WeatherSimulation and the saved weatherId are not changed here.
    /// </summary>
    public sealed class DayNightWeatherVisual : MonoBehaviour
    {
        // ExponentialSquared. Old linear densities 0.02 / 0.012 grayed the yard.
        // At ~20 m the haze is barely there; the far shore goes pale.
        private const float DistantHazeDensity = 0.0055f;
        private static readonly Color DistantHaze = new(0.68f, 0.78f, 0.86f);
        [SerializeField] private SaveService saveService;
        [SerializeField] private Light sunLight;

        private Color _baseSun = new(1f, 0.96f, 0.88f);
        private float _baseIntensity = 1.1f;

        public void Configure(SaveService save, Light sun = null)
        {
            saveService = save;
            if (sun != null)
            {
                sunLight = sun;
            }
        }

        private void Awake()
        {
            // Scene setup may leave exponential fog on. Package A keeps the island readable.
            RenderSettings.fog = false;

            if (sunLight == null)
            {
                sunLight = FindFirstObjectByType<Light>();
            }

            if (sunLight != null)
            {
                _baseSun = sunLight.color;
                _baseIntensity = sunLight.intensity;
            }
        }

        public void ApplyFromSave(GameSaveData data = null)
        {
            data ??= saveService != null ? saveService.AuthoritativeSnapshot : null;
            var foggy = data?.weather != null && WeatherSimulation.IsFoggy(data.weather);
            RenderSettings.fog = foggy;
            if (foggy)
            {
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogDensity = DistantHazeDensity;
                RenderSettings.fogColor = DistantHaze;
            }

            if (data?.weather == null)
            {
                return;
            }

            if (sunLight == null)
            {
                sunLight = FindFirstObjectByType<Light>();
                if (sunLight == null)
                {
                    return;
                }

                _baseSun = sunLight.color;
                _baseIntensity = sunLight.intensity;
            }

            var tod = Mathf.Repeat(data.weather.timeOfDay01, 1f);
            var dayFactor = DayFactor(tod);

            var sunColor = Color.Lerp(new Color(0.35f, 0.4f, 0.7f), _baseSun, dayFactor);
            sunLight.color = sunColor;
            sunLight.intensity = Mathf.Lerp(0.25f, _baseIntensity, dayFactor);

            var ambient = Color.Lerp(
                new Color(0.08f, 0.1f, 0.18f),
                new Color(0.45f, 0.5f, 0.55f),
                dayFactor);
            RenderSettings.ambientLight = ambient;

            // Shared living-world visuals: wind globals for vegetation shaders.
            var env = WorldVisualEnvironment.Instance
                      ?? FindFirstObjectByType<WorldVisualEnvironment>();
            env?.ApplyFromSave(data);
        }

        private static float DayFactor(float tod01)
        {
            // Peak at noon (0.5), dark near 0/1.
            var dist = Mathf.Abs(tod01 - 0.5f) * 2f;
            return Mathf.Clamp01(1f - dist * 1.15f);
        }
    }
}
