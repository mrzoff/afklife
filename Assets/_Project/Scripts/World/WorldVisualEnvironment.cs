using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Single visual weather → environment bridge. One Update drives GPU wind globals
    /// for all vegetation materials. Does not alter WeatherSimulation / saves.
    /// </summary>
    public sealed class WorldVisualEnvironment : MonoBehaviour
    {
        public static WorldVisualEnvironment Instance { get; private set; }

        private static readonly int WindParamsId = Shader.PropertyToID("_AfkWindParams");

        [SerializeField] private SaveService saveService;
        [SerializeField] private float baseWind = 0.22f;
        [SerializeField] private float windyBonus = 0.55f;
        [SerializeField] private float rainBonus = 0.18f;
        [SerializeField] private float gustSpeed = 0.65f;

        private Vector2 _windDir = new(1f, 0.15f);
        private float _targetStrength = 0.25f;
        private float _strength = 0.25f;
        private float _gustPhase;

        public float WindStrength => _strength;
        public Vector2 WindDirection => _windDir;
        public bool IsRaining { get; private set; }
        public bool IsFoggy { get; private set; }
        public bool IsWindy { get; private set; }

        public void Configure(SaveService save)
        {
            saveService = save;
        }

        private void Awake()
        {
            Instance = this;
            PushGlobals(0f);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        public void ApplyFromSave(GameSaveData data = null)
        {
            data ??= saveService != null ? saveService.AuthoritativeSnapshot : null;
            if (data?.weather == null)
            {
                _targetStrength = baseWind;
                IsRaining = false;
                IsFoggy = false;
                IsWindy = false;
                return;
            }

            IsRaining = WeatherSimulation.IsRaining(data.weather);
            IsFoggy = WeatherSimulation.IsFoggy(data.weather);
            IsWindy = WeatherSimulation.IsWindy(data.weather);

            // Deterministic-ish direction from seed so sessions feel stable.
            var seed = data.worldGrowth != null ? data.worldGrowth.growthSeed : 1;
            var ang = (seed % 628) / 100f;
            _windDir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)).normalized;

            _targetStrength = baseWind;
            if (IsWindy)
            {
                _targetStrength += windyBonus;
            }

            if (IsRaining)
            {
                _targetStrength += rainBonus;
            }

            if (IsFoggy)
            {
                _targetStrength *= 0.75f;
            }

            _targetStrength = Mathf.Clamp01(_targetStrength);
            _strength = _targetStrength;
            PushGlobals(SampleGust());
        }

        private void Update()
        {
            _strength = Mathf.MoveTowards(_strength, _targetStrength, Time.deltaTime * 0.35f);
            _gustPhase += Time.deltaTime * gustSpeed * (0.6f + _strength);
            PushGlobals(SampleGust());
        }

        private float SampleGust()
        {
            // Soft pulses — no per-object scripts.
            var g = Mathf.Sin(_gustPhase) * 0.5f + 0.5f;
            g = Mathf.Pow(g, 1.6f);
            if (IsWindy)
            {
                g = Mathf.Max(g, Mathf.Sin(_gustPhase * 2.3f) * 0.5f + 0.5f);
            }

            return Mathf.Clamp01(g * (0.4f + _strength));
        }

        private void PushGlobals(float gust)
        {
            Shader.SetGlobalVector(
                WindParamsId,
                new Vector4(_windDir.x, _windDir.y, _strength, gust));
        }
    }
}
