using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.Animals
{
    public sealed class AnimalInstance : MonoBehaviour
    {
        public const int MaxAnimatedAnimals = 24;

        [SerializeField] private string instanceId;
        [SerializeField] private string animalId;
        [SerializeField] private float moveSpeed = 0.55f;
        [SerializeField] private float wanderRadius = 1.8f;
        [SerializeField] private float idleSecondsMin = 1.5f;
        [SerializeField] private float idleSecondsMax = 4f;
        [SerializeField] private bool faceMovement = true;

        private Vector3 _home;
        private Vector3 _target;
        private float _idleUntil;
        private bool _moving;
        private AnimalVisualMotion _visual;
        private Animator _animator;
        private static readonly int IsMovingHash = Animator.StringToHash("IsMoving");

        public string InstanceId => instanceId;
        public string AnimalId => animalId;
        public bool IsMoving => _moving;

        public void Configure(AnimalSaveData data, AnimalData catalog)
        {
            instanceId = data.instanceId;
            animalId = data.animalId;
            transform.position = new Vector3(data.posX, data.posY, data.posZ);
            _home = transform.position;
            if (catalog != null)
            {
                moveSpeed = catalog.moveSpeed;
                wanderRadius = catalog.wanderRadius;
                idleSecondsMin = catalog.idleSecondsMin;
                idleSecondsMax = catalog.idleSecondsMax;
            }

            _visual = GetComponentInChildren<AnimalVisualMotion>(true);
            _animator = GetComponentInChildren<Animator>(true);
            // Prefer cheap procedural motion; Animator is optional and capped elsewhere if added later.
            if (_animator != null && _visual != null)
            {
                _animator.enabled = false;
            }

            var idleMin = Mathf.Max(0.2f, idleSecondsMin);
            var idleMax = Mathf.Max(idleMin, idleSecondsMax);
            _idleUntil = Time.time + Random.Range(idleMin * 0.35f, idleMax * 0.65f);
            _moving = false;
            PushMotionState();
        }

        private void Update()
        {
            if (_moving)
            {
                var step = moveSpeed * Time.deltaTime;
                transform.position = Vector3.MoveTowards(transform.position, _target, step);
                if (faceMovement)
                {
                    var dir = _target - transform.position;
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 0.0001f)
                    {
                        transform.rotation = Quaternion.Slerp(
                            transform.rotation,
                            Quaternion.LookRotation(dir.normalized, Vector3.up),
                            1f - Mathf.Exp(-10f * Time.deltaTime));
                    }
                }

                if ((transform.position - _target).sqrMagnitude < 0.0004f)
                {
                    _moving = false;
                    var idleMin = Mathf.Max(0.2f, idleSecondsMin);
                    var idleMax = Mathf.Max(idleMin, idleSecondsMax);
                    _idleUntil = Time.time + Random.Range(idleMin, idleMax);
                    PushMotionState();
                }

                return;
            }

            if (Time.time < _idleUntil)
            {
                return;
            }

            var angle = Random.Range(0f, Mathf.PI * 2f);
            var dist = Random.Range(0.25f, wanderRadius);
            _target = _home + new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);
            _target.y = _home.y;
            _moving = true;
            PushMotionState();
        }

        private void PushMotionState()
        {
            if (_visual != null)
            {
                _visual.SetMoving(_moving);
            }

            if (_animator != null && _animator.enabled && _animator.runtimeAnimatorController != null)
            {
                _animator.SetBool(IsMovingHash, _moving);
            }
        }

        public AnimalSaveData Capture()
        {
            return new AnimalSaveData
            {
                instanceId = instanceId,
                animalId = animalId,
                posX = transform.position.x,
                posY = transform.position.y,
                posZ = transform.position.z,
                state = (byte)(_moving ? AnimalMoveState.Move : AnimalMoveState.Idle),
                spawnedUtc = 0
            };
        }
    }
}
