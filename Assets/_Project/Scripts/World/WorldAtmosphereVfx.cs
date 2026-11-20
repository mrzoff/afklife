using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// P2 living atmosphere: leaves, pollen, rain particles driven by WorldVisualEnvironment.
    /// Fog density stays in DayNightWeatherVisual. Does not touch saves / weather sim.
    /// Stage 11E-1: never move GameSystems / Plants / Animals — only an isolated VFX root follows the camera.
    /// </summary>
    public sealed class WorldAtmosphereVfx : MonoBehaviour
    {
        public const string IsolatedRootName = "WorldAtmosphereVfxRoot";

        [SerializeField] private Transform follow;
        [SerializeField] private float followHeight = 6f;
        [SerializeField] private float boxSize = 28f;

        private ParticleSystem _leaves;
        private ParticleSystem _pollen;
        private ParticleSystem _rain;
        private bool _built;

        /// <summary>
        /// Ensures a scene-root VFX host that is safe to follow the camera.
        /// Never attaches to GameSystems (Plants/Animals live there).
        /// </summary>
        public static WorldAtmosphereVfx EnsureIsolatedInstance()
        {
            var rootGo = GameObject.Find(IsolatedRootName);
            if (rootGo == null)
            {
                rootGo = new GameObject(IsolatedRootName);
            }

            if (rootGo.transform.parent != null)
            {
                rootGo.transform.SetParent(null, true);
            }

            var isolated = rootGo.GetComponent<WorldAtmosphereVfx>()
                           ?? rootGo.AddComponent<WorldAtmosphereVfx>();

            // Strip legacy copies left on GameSystems / other gameplay hosts.
            var all = Object.FindObjectsByType<WorldAtmosphereVfx>();
            for (var i = 0; i < all.Length; i++)
            {
                var other = all[i];
                if (other == null || other == isolated)
                {
                    continue;
                }

                Debug.LogWarning(
                    "[AFK Island] Removing misplaced WorldAtmosphereVfx from " + other.gameObject.name +
                    ". Atmosphere follow stays on " + IsolatedRootName + ".");
                if (Application.isPlaying)
                {
                    Object.Destroy(other);
                }
                else
                {
                    Object.DestroyImmediate(other);
                }
            }

            return isolated;
        }

        public void Configure(Transform followTarget = null)
        {
            if (followTarget != null)
            {
                follow = followTarget;
            }
        }

        private void Awake()
        {
            if (!IsSafeMotionHost())
            {
                // EnsureIsolatedInstance strips this component; do not touch fields after.
                var followTarget = follow;
                var isolated = EnsureIsolatedInstance();
                if (isolated != null && followTarget != null)
                {
                    isolated.Configure(followTarget);
                }

                return;
            }

            PromoteToIsolatedRoot();
            EnsureSystems();
            ApplyState();
        }

        private void LateUpdate()
        {
            TickFollowAndState(forceState: false);
        }

        /// <summary>Editor/smoke: one follow + emission tick without waiting for LateUpdate.</summary>
        public void TickFollowAndState(bool forceState)
        {
            if (!IsSafeMotionHost())
            {
                return;
            }

            EnsureSystems();
            if (follow == null)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    follow = cam.transform;
                }
            }

            if (follow != null)
            {
                var p = follow.position;
                transform.position = new Vector3(p.x, p.y + followHeight * 0.35f, p.z);
            }

            ApplyState();
        }

        private void PromoteToIsolatedRoot()
        {
            if (gameObject.name != IsolatedRootName)
            {
                gameObject.name = IsolatedRootName;
            }

            if (transform.parent != null)
            {
                transform.SetParent(null, true);
            }
        }

        private bool IsSafeMotionHost()
        {
            if (gameObject.name is "GameSystems" or "World")
            {
                return false;
            }

            for (var i = 0; i < transform.childCount; i++)
            {
                var childName = transform.GetChild(i).name;
                if (childName is "Plants" or "Animals" or "Island" or "Settlement" or "WaterSource"
                    or "GrownLand")
                {
                    return false;
                }
            }

            return true;
        }

        private void EnsureSystems()
        {
            if (_built)
            {
                return;
            }

            _leaves = CreateLeafSystem();
            _pollen = CreatePollenSystem();
            _rain = CreateRainSystem();
            _built = true;
        }

        private void ApplyState()
        {
            var env = WorldVisualEnvironment.Instance
                      ?? Object.FindFirstObjectByType<WorldVisualEnvironment>();
            var raining = env != null && env.IsRaining;
            var foggy = env != null && env.IsFoggy;
            var windy = env != null && env.IsWindy;
            var wind = env != null ? env.WindStrength : 0.25f;
            var dir = env != null ? env.WindDirection : new Vector2(1f, 0.15f);

            var leafRate = Mathf.Lerp(2f, 18f, wind) * (windy ? 1.4f : 1f) * (raining ? 1.25f : 1f);
            SetEmission(_leaves, leafRate);
            PushWindVelocity(_leaves, dir, wind, 1.8f + wind * 4f);

            var pollenRate = raining || foggy ? 0f : Mathf.Lerp(4f, 14f, 1f - wind);
            SetEmission(_pollen, pollenRate);
            PushWindVelocity(_pollen, dir, wind, 0.4f + wind * 1.2f);

            // Drops only. The sun and ambient stay on the day/night colors.
            SetEmission(_rain, raining ? 180f : 0f);
            PushWindVelocity(_rain, dir, wind, 1f + wind * 2.5f, downward: 9f);
            if (!raining)
            {
                _rain.Clear(true);
            }
        }

        private static void SetEmission(ParticleSystem ps, float rate)
        {
            if (ps == null)
            {
                return;
            }

            var emission = ps.emission;
            emission.rateOverTime = Mathf.Max(0f, rate);
            if (rate > 0.01f && !ps.isPlaying)
            {
                ps.Play(true);
            }
            else if (rate <= 0.01f && ps.isPlaying)
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        private static void PushWindVelocity(
            ParticleSystem ps,
            Vector2 dir,
            float wind,
            float horizontal,
            float downward = 0.35f)
        {
            if (ps == null)
            {
                return;
            }

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = dir.x * horizontal * (0.4f + wind);
            vel.z = dir.y * horizontal * (0.4f + wind);
            vel.y = -downward;
        }

        private ParticleSystem CreateLeafSystem()
        {
            var go = new GameObject("Leaves");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.18f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.8f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.45f, 0.55f, 0.22f, 0.9f),
                new Color(0.75f, 0.45f, 0.18f, 0.85f));
            main.maxParticles = 120;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0.15f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(boxSize, 4f, boxSize);

            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(0.5f, 2.2f);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var leafMat = CreateParticleMaterial(new Color(0.55f, 0.6f, 0.25f, 0.85f), "AfkLeafMat");
            AssignLeafTexture(leafMat);
            renderer.sharedMaterial = leafMat;
            return ps;
        }

        private ParticleSystem CreatePollenSystem()
        {
            var go = new GameObject("Pollen");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(5f, 10f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.06f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.25f);
            main.startColor = new Color(1f, 0.92f, 0.55f, 0.55f);
            main.maxParticles = 160;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = -0.02f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(boxSize * 0.7f, 3f, boxSize * 0.7f);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = CreateParticleMaterial(new Color(1f, 0.95f, 0.7f, 0.5f), "AfkPollenMat");
            return ps;
        }

        private ParticleSystem CreateRainSystem()
        {
            var go = new GameObject("Rain");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.7f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.035f);
            main.startSpeed = 0f;
            main.startColor = new Color(0.7f, 0.8f, 0.9f, 0.45f);
            main.maxParticles = 600;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 1.4f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(boxSize, 0.5f, boxSize);
            shape.position = new Vector3(0f, followHeight, 0f);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 4f;
            renderer.velocityScale = 0.08f;
            renderer.sharedMaterial = CreateParticleMaterial(new Color(0.75f, 0.82f, 0.9f, 0.4f), "AfkRainMat");
            return ps;
        }

        private static Material CreateParticleMaterial(Color color, string name)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                         ?? Shader.Find("Particles/Standard Unlit")
                         ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader) { name = name, hideFlags = HideFlags.DontSave };
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }
            else
            {
                mat.color = color;
            }

            return mat;
        }

        private static void AssignLeafTexture(Material mat)
        {
            if (mat == null)
            {
                return;
            }

            var tex = Resources.Load<Texture2D>("VFX/Leaf_Zsky");
            if (tex == null)
            {
                return;
            }

            if (mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", tex);
            }
            else
            {
                mat.mainTexture = tex;
            }
        }
    }
}
