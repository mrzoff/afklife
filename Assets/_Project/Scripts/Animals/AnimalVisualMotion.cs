using UnityEngine;

namespace AfkIsland.Animals
{
    /// <summary>
    /// Lightweight procedural visual life for animal prefabs (no per-bone Animator required).
    /// Driven by AnimalInstance IsMoving — does not touch save/fauna simulation.
    /// </summary>
    public sealed class AnimalVisualMotion : MonoBehaviour
    {
        public enum MotionKind
        {
            Butterfly,
            Bird,
            Frog
        }

        [SerializeField] private MotionKind kind = MotionKind.Butterfly;
        [SerializeField] private Transform visualRoot;
        [SerializeField] private Transform wingLeft;
        [SerializeField] private Transform wingRight;
        [SerializeField] private float idleAmplitude = 0.04f;
        [SerializeField] private float moveAmplitude = 0.08f;
        [SerializeField] private float flapSpeedIdle = 8f;
        [SerializeField] private float flapSpeedMove = 16f;

        private bool _moving;
        private Vector3 _baseLocalPos;
        private Quaternion _baseLocalRot;
        private Quaternion _wingL0;
        private Quaternion _wingR0;
        private float _phase;

        public void SetMoving(bool moving) => _moving = moving;

        public void Configure(
            MotionKind motionKind,
            Transform root,
            Transform leftWing = null,
            Transform rightWing = null)
        {
            kind = motionKind;
            visualRoot = root != null ? root : transform;
            wingLeft = leftWing;
            wingRight = rightWing;
            if (motionKind == MotionKind.Bird)
            {
                // Softer bob/tilt — less mechanical plastic-toy motion.
                idleAmplitude = 0.022f;
                moveAmplitude = 0.045f;
                flapSpeedIdle = 5.5f;
                flapSpeedMove = 11f;
            }

            CacheBases();
        }

        private void Awake()
        {
            if (visualRoot == null)
            {
                visualRoot = transform;
            }

            CacheBases();
        }

        private void CacheBases()
        {
            if (visualRoot != null)
            {
                _baseLocalPos = visualRoot.localPosition;
                _baseLocalRot = visualRoot.localRotation;
            }

            if (wingLeft != null)
            {
                _wingL0 = wingLeft.localRotation;
            }

            if (wingRight != null)
            {
                _wingR0 = wingRight.localRotation;
            }
        }

        private void LateUpdate()
        {
            if (visualRoot == null)
            {
                return;
            }

            // Optional weather coupling: wind only scales motion, never writes save/fauna.
            var wind = 1f;
            var env = AfkIsland.World.WorldVisualEnvironment.Instance;
            if (env != null)
            {
                wind = 1f + env.WindStrength * 0.65f;
            }

            var speed = (_moving ? flapSpeedMove : flapSpeedIdle) * wind;
            _phase += Time.deltaTime * speed;
            var amp = (_moving ? moveAmplitude : idleAmplitude) * (0.85f + wind * 0.15f);
            var wave = Mathf.Sin(_phase);

            switch (kind)
            {
                case MotionKind.Butterfly:
                    ApplyWingFlap(wave, _moving ? 42f : 28f);
                    visualRoot.localPosition = _baseLocalPos + Vector3.up * (wave * amp * 0.6f);
                    break;
                case MotionKind.Bird:
                    ApplyWingFlap(wave, _moving ? 22f : 8f);
                    visualRoot.localPosition = _baseLocalPos + Vector3.up * (wave * amp * 0.28f);
                    if (_moving)
                    {
                        visualRoot.localRotation = _baseLocalRot * Quaternion.Euler(4.5f * wave, 0f, 0f);
                    }
                    else
                    {
                        visualRoot.localRotation = _baseLocalRot * Quaternion.Euler(1.2f * wave, 0f, 0f);
                    }

                    break;
                case MotionKind.Frog:
                    if (_moving)
                    {
                        var hop = Mathf.Abs(wave);
                        visualRoot.localPosition = _baseLocalPos + Vector3.up * (hop * moveAmplitude * 1.6f);
                        visualRoot.localRotation = _baseLocalRot * Quaternion.Euler(-12f * hop, 0f, 0f);
                    }
                    else
                    {
                        visualRoot.localPosition = _baseLocalPos + Vector3.up * (wave * idleAmplitude * 0.25f);
                        visualRoot.localRotation = _baseLocalRot;
                    }

                    break;
            }
        }

        private void ApplyWingFlap(float wave, float degrees)
        {
            if (wingLeft != null)
            {
                wingLeft.localRotation = _wingL0 * Quaternion.Euler(0f, 0f, wave * degrees);
            }

            if (wingRight != null)
            {
                wingRight.localRotation = _wingR0 * Quaternion.Euler(0f, 0f, -wave * degrees);
            }
        }
    }
}
