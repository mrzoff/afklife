using AfkIsland.CameraControl;
using AfkIsland.Input;
using AfkIsland.Plants;
using AfkIsland.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AfkIsland.Fishing
{
    /// <summary>
    /// Ocean tap → open fishing. Distinguishes tap from camera drag; ignores UI and land hits.
    /// Does not touch WaterSource / pond.
    /// </summary>
    public sealed class FishingOceanInput : MonoBehaviour
    {
        private const int RayHitBufferSize = 32;

        [SerializeField] private FishingController controller;
        [SerializeField] private Camera rayCamera;
        [SerializeField] private IsometricCameraController cameraController;
        [SerializeField] private PlantingSystem plantingSystem;
        [SerializeField] private float maxTapMovePixels = WorldPointerGesture.DefaultMaxTapMovePixels;
        [SerializeField] private float maxTapSeconds = WorldPointerGesture.DefaultMaxTapSeconds;
        [SerializeField] private LayerMask rayMask = ~0;

        private static readonly RaycastHit[] RayHits = new RaycastHit[RayHitBufferSize];

        private bool _inputEnabled;
        private bool _pointerDown;
        private bool _oceanPressCandidate;
        private Vector2 _pressScreenPos;
        private float _pressUnscaledTime;
        private int _pressFingerId = -1;
        private bool _suppressedGameplay;

        public void Configure(
            FishingController fishingController,
            Camera cam = null,
            IsometricCameraController camController = null,
            PlantingSystem planting = null)
        {
            controller = fishingController;
            if (cam != null)
            {
                rayCamera = cam;
            }

            if (camController != null)
            {
                cameraController = camController;
            }

            if (planting != null)
            {
                plantingSystem = planting;
            }
        }

        public void SetInputEnabled(bool enabled)
        {
            _inputEnabled = enabled;
            if (!enabled)
            {
                ResetPress();
            }
        }

        private void Awake()
        {
            if (rayCamera == null)
            {
                rayCamera = Camera.main;
            }

            if (cameraController == null && rayCamera != null)
            {
                cameraController = rayCamera.GetComponent<IsometricCameraController>();
            }

            EnsureOceanHitProxy();
        }

        private void Update()
        {
            if (controller != null && controller.IsOpen)
            {
                SuppressWorldInput(true);
                SetCameraGestureBlocked(false);
                ResetPress();
                return;
            }

            SuppressWorldInput(false);

            if (!_inputEnabled || controller == null)
            {
                SetCameraGestureBlocked(false);
                return;
            }

            PollPointer();
            SetCameraGestureBlocked(_pointerDown && _oceanPressCandidate);
        }

        private void OnDisable()
        {
            SuppressWorldInput(false);
            SetCameraGestureBlocked(false);
            ResetPress();
        }

        private void SetCameraGestureBlocked(bool blocked)
        {
            if (cameraController == null && rayCamera != null)
            {
                cameraController = rayCamera.GetComponent<IsometricCameraController>();
            }

            if (cameraController != null)
            {
                cameraController.WorldGestureBlocked = blocked;
            }
        }

        private void SuppressWorldInput(bool suppress)
        {
            if (suppress == _suppressedGameplay)
            {
                return;
            }

            _suppressedGameplay = suppress;
            if (cameraController != null)
            {
                cameraController.enabled = !suppress && _inputEnabled;
            }

            if (plantingSystem != null)
            {
                plantingSystem.enabled = !suppress && _inputEnabled;
            }
        }

        private void PollPointer()
        {
            var touch = Touchscreen.current;
            var mouse = Mouse.current;

            var hasTouch = touch != null && touch.touches.Count > 0 && touch.touches[0].press.isPressed;
            if (hasTouch)
            {
                var t = touch.touches[0];
                var pos = t.position.ReadValue();
                if (!_pointerDown)
                {
                    BeginPress(pos, t.touchId.ReadValue());
                }
                else if (_pressFingerId == t.touchId.ReadValue() || _pressFingerId < 0)
                {
                    TrackMove(pos);
                }

                return;
            }

            if (_pointerDown && _pressFingerId >= 0)
            {
                var releasePos = touch != null
                    ? touch.primaryTouch.position.ReadValue()
                    : _pressScreenPos;
                EndPress(releasePos);
                return;
            }

            if (mouse != null && mouse.leftButton.isPressed)
            {
                var pos = mouse.position.ReadValue();
                if (!_pointerDown)
                {
                    BeginPress(pos, -1);
                }
                else
                {
                    TrackMove(pos);
                }

                return;
            }

            if (mouse != null && _pointerDown && _pressFingerId < 0 && !mouse.leftButton.isPressed)
            {
                EndPress(mouse.position.ReadValue());
            }
        }

        private void BeginPress(Vector2 screenPos, int fingerId)
        {
            if (PrototypeWorldGrowthDebug.BlocksWorldPointer(screenPos) ||
                IsPointerOverUi(screenPos, fingerId))
            {
                ResetPress();
                return;
            }

            _pointerDown = true;
            _pressFingerId = fingerId;
            _pressScreenPos = screenPos;
            _pressUnscaledTime = Time.unscaledTime;
            _oceanPressCandidate = RayHitsOcean(screenPos);
            if (!_oceanPressCandidate)
            {
                Debug.Log($"[AFK Island] Ocean tap miss (nearest not ocean). pos={screenPos}");
            }
        }

        private void TrackMove(Vector2 screenPos)
        {
            if (!_pointerDown)
            {
                return;
            }

            if (Vector2.Distance(screenPos, _pressScreenPos) > maxTapMovePixels)
            {
                _oceanPressCandidate = false;
            }
        }

        private void EndPress(Vector2 screenPos)
        {
            if (!_pointerDown)
            {
                return;
            }

            var wasCandidate = _oceanPressCandidate;
            var shortEnough = (Time.unscaledTime - _pressUnscaledTime) <= maxTapSeconds;
            var smallMove = Vector2.Distance(screenPos, _pressScreenPos) <= maxTapMovePixels;
            ResetPress();

            if (!wasCandidate || !shortEnough || !smallMove)
            {
                return;
            }

            if (IsPointerOverUi(screenPos, -1))
            {
                return;
            }

            if (!RayHitsOcean(screenPos))
            {
                Debug.Log($"[AFK Island] Ocean tap ignored on release. pos={screenPos}");
                return;
            }

            // Explicit isolation: never open ocean if this ray prefers pond.
            if (RayHitsPondFirst(screenPos))
            {
                Debug.Log("[AFK Island] Ocean tap blocked — pond hit preferred on ray.");
                return;
            }

            var opened = controller.TryOpenFromOcean();
            Debug.Log($"[AFK Island] Ocean tap → TryOpenFromOcean={opened}");
        }

        private void ResetPress()
        {
            _pointerDown = false;
            _oceanPressCandidate = false;
            _pressFingerId = -1;
        }

        private bool RayHitsOcean(Vector2 screenPos)
        {
            if (rayCamera == null)
            {
                rayCamera = Camera.main;
            }

            if (rayCamera == null)
            {
                return false;
            }

            var ray = rayCamera.ScreenPointToRay(screenPos);
            // Nearest solid hit must be ocean — tapping land/trees must not open fishing.
            if (!Physics.Raycast(ray, out var hit, 500f, rayMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            return IsOceanCollider(hit.collider);
        }

        private bool RayHitsPondFirst(Vector2 screenPos)
        {
            if (rayCamera == null)
            {
                return false;
            }

            var ray = rayCamera.ScreenPointToRay(screenPos);
            var count = Physics.RaycastNonAlloc(
                ray, RayHits, 500f, rayMask, QueryTriggerInteraction.Ignore);
            var bestDist = float.PositiveInfinity;
            Collider best = null;
            for (var i = 0; i < count; i++)
            {
                var h = RayHits[i];
                if (h.collider == null || h.distance >= bestDist)
                {
                    continue;
                }

                bestDist = h.distance;
                best = h.collider;
            }

            if (count >= RayHits.Length)
            {
                var all = Physics.RaycastAll(ray, 500f, rayMask, QueryTriggerInteraction.Ignore);
                for (var i = 0; i < all.Length; i++)
                {
                    var h = all[i];
                    if (h.collider == null || h.distance >= bestDist)
                    {
                        continue;
                    }

                    bestDist = h.distance;
                    best = h.collider;
                }
            }

            return FishingPondInput.IsPondCollider(best);
        }

        /// <summary>True only for Ocean ancestry; WaterSource / PondHitProxy always false.</summary>
        public static bool IsOceanCollider(Collider col)
        {
            if (col == null)
            {
                return false;
            }

            var t = col.transform;
            while (t != null)
            {
                if (t.name == "Ocean")
                {
                    return true;
                }

                if (t.name == "WaterSource" || t.name == FishingPondInput.ProxyChildName)
                {
                    return false;
                }

                t = t.parent;
            }

            return false;
        }

        private static bool IsPointerOverUi(Vector2 screenPos, int fingerId)
        {
            _ = screenPos;
            return WorldPointerGesture.IsPointerOverUi(fingerId);
        }

        /// <summary>Ensures Ocean has a MeshCollider for taps without altering WaterSource.</summary>
        public static void EnsureOceanHitProxy()
        {
            var ocean = GameObject.Find("Ocean");
            if (ocean == null)
            {
                return;
            }

            var filter = ocean.GetComponent<MeshFilter>();
            var col = ocean.GetComponent<MeshCollider>();
            if (col == null)
            {
                col = ocean.AddComponent<MeshCollider>();
            }

            if (filter != null && filter.sharedMesh != null)
            {
                col.sharedMesh = filter.sharedMesh;
            }

            col.convex = false;
            col.isTrigger = false;
        }
    }
}
