using AfkIsland.CameraControl;
using AfkIsland.Input;
using AfkIsland.Plants;
using AfkIsland.World;
using UnityEngine;

namespace AfkIsland.Fishing
{
    /// <summary>
    /// Stage 10B corrective: pond tap → open fishing.
    /// Child PondHitProxy + RaycastNonAlloc (yard may occlude first hit).
    /// </summary>
    public sealed class FishingPondInput : MonoBehaviour
    {
        public const string WaterSourceName = "WaterSource";
        public const string ProxyChildName = "PondHitProxy";

        [SerializeField] private FishingController controller;
        [SerializeField] private Camera rayCamera;
        [SerializeField] private IsometricCameraController cameraController;
        [SerializeField] private PlantingSystem plantingSystem;
        [SerializeField] private float maxTapMovePixels = WorldPointerGesture.DefaultMaxTapMovePixels;
        [SerializeField] private float maxTapSeconds = WorldPointerGesture.DefaultMaxTapSeconds;
        [SerializeField] private LayerMask rayMask = ~0;

        private bool _inputEnabled;
        private WorldPointerGesture.PressState _press;
        private bool _pondPressCandidate;
        private bool _suppressedGameplay;
        private bool _proxyReady;

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

            _proxyReady = EnsurePondHitProxy();
        }

        public void SetInputEnabled(bool enabled)
        {
            _inputEnabled = enabled;
            if (!enabled)
            {
                WorldPointerGesture.Reset(ref _press);
                _pondPressCandidate = false;
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

            _proxyReady = EnsurePondHitProxy();
        }

        private void Update()
        {
            // Re-validate every frame: settlement upgrade Destroy()s the old Island at
            // end-of-frame, so a same-frame Ensure can bind to a dying WaterSource and
            // leave _proxyReady stuck true with no live PondHitProxy.
            if (!TryGetPondHitProxy(out _))
            {
                _proxyReady = EnsurePondHitProxy();
            }
            else
            {
                _proxyReady = true;
            }

            if (controller != null && controller.IsOpen)
            {
                SuppressWorldInput(true);
                SetCameraGestureBlocked(false);
                WorldPointerGesture.Reset(ref _press);
                _pondPressCandidate = false;
                return;
            }

            SuppressWorldInput(false);

            if (!_inputEnabled || controller == null)
            {
                SetCameraGestureBlocked(false);
                return;
            }

            PollPointer();
            // Hold camera still while a pond tap candidate is alive (below drag threshold).
            SetCameraGestureBlocked(_press.Active && _pondPressCandidate && !_press.CancelledAsDrag);
        }

        private void OnDisable()
        {
            SuppressWorldInput(false);
            SetCameraGestureBlocked(false);
            WorldPointerGesture.Reset(ref _press);
            _pondPressCandidate = false;
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
            var touch = UnityEngine.InputSystem.Touchscreen.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;

            // BlueStacks / hybrid: only treat real active touches as touch; otherwise use mouse.
            var hasTouch = touch != null && touch.touches.Count > 0 && touch.touches[0].press.isPressed;
            if (hasTouch)
            {
                var t = touch.touches[0];
                var pos = t.position.ReadValue();
                if (!_press.Active)
                {
                    BeginCandidate(pos, t.touchId.ReadValue());
                }
                else if (_press.FingerId == t.touchId.ReadValue() || _press.FingerId < 0)
                {
                    WorldPointerGesture.TrackMove(ref _press, pos, maxTapMovePixels);
                    if (_press.CancelledAsDrag)
                    {
                        _pondPressCandidate = false;
                    }
                }

                return;
            }

            if (_press.Active && _press.FingerId >= 0)
            {
                var releasePos = touch != null
                    ? touch.primaryTouch.position.ReadValue()
                    : _press.StartScreenPos;
                EndCandidate(releasePos);
                return;
            }

            if (mouse != null && mouse.leftButton.isPressed)
            {
                var pos = mouse.position.ReadValue();
                if (!_press.Active)
                {
                    BeginCandidate(pos, -1);
                }
                else
                {
                    WorldPointerGesture.TrackMove(ref _press, pos, maxTapMovePixels);
                    if (_press.CancelledAsDrag)
                    {
                        _pondPressCandidate = false;
                    }
                }

                return;
            }

            if (mouse != null && _press.Active && _press.FingerId < 0 && !mouse.leftButton.isPressed)
            {
                EndCandidate(mouse.position.ReadValue());
            }
        }

        private void BeginCandidate(Vector2 screenPos, int fingerId)
        {
            if (PrototypeWorldGrowthDebug.BlocksWorldPointer(screenPos))
            {
                WorldPointerGesture.Reset(ref _press);
                _pondPressCandidate = false;
                return;
            }

            WorldPointerGesture.BeginPress(ref _press, screenPos, fingerId);
            var hit = _press.Active && RayHitsPond(screenPos);
            _pondPressCandidate = hit;
            if (_press.Active && !hit)
            {
                Debug.Log(
                    $"[AFK Island] Pond tap miss (no hit). proxy={TryGetPondHitProxy(out _)} " +
                    $"pos={screenPos} enabled={_inputEnabled}");
            }
        }

        private void EndCandidate(Vector2 screenPos)
        {
            var wasCandidate = _pondPressCandidate;
            if (!WorldPointerGesture.TryCompleteTap(
                    ref _press,
                    screenPos,
                    out var tapPos,
                    maxTapMovePixels,
                    maxTapSeconds))
            {
                _pondPressCandidate = false;
                return;
            }

            _pondPressCandidate = false;
            if (PrototypeWorldGrowthDebug.BlocksWorldPointer(tapPos))
            {
                return;
            }

            if (!wasCandidate || !RayHitsPond(tapPos))
            {
                Debug.Log(
                    $"[AFK Island] Pond tap ignored (candidate={wasCandidate}, " +
                    $"hit={RayHitsPond(tapPos)}, pos={tapPos})");
                return;
            }

            var opened = controller.TryOpenFromPond();
            Debug.Log($"[AFK Island] Pond tap → TryOpenFromPond={opened}");
        }

        private bool RayHitsPond(Vector2 screenPos)
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
            return RayHitsPond(ray, rayMask);
        }

        /// <summary>
        /// Stage 11E: nearest solid hit must be the pond (same contract as ocean).
        /// Occluding trees / yard / buildings block the tap — do not "see through" them.
        /// </summary>
        public static bool RayHitsPond(Ray ray, int layerMask = ~0)
        {
            if (!Physics.Raycast(ray, out var hit, 500f, layerMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            return IsPondCollider(hit.collider);
        }

        /// <summary>
        /// Smoke/editor helper: when input is enabled, treat a screen point as a completed world tap
        /// (ray must hit pond). Does not bypass SetInputEnabled / open-state guards.
        /// Full UI EventSystem blocking remains a device freeform (10C) check.
        /// </summary>
        public bool SmokeTryWorldTap(Vector2 screenPos)
        {
            if (rayCamera == null)
            {
                rayCamera = Camera.main;
            }

            if (rayCamera == null)
            {
                return false;
            }

            return SmokeTryWorldRay(rayCamera.ScreenPointToRay(screenPos));
        }

        /// <summary>Smoke helper with an explicit world ray (batch-stable without Game View).</summary>
        public bool SmokeTryWorldRay(Ray ray)
        {
            if (!_inputEnabled || controller == null || controller.IsOpen)
            {
                return false;
            }

            if (!RayHitsPond(ray, rayMask))
            {
                return false;
            }

            return controller.TryOpenFromPond();
        }

        public static bool IsPondCollider(Collider col)
        {
            if (col == null)
            {
                return false;
            }

            var t = col.transform;
            while (t != null)
            {
                if (t.name == ProxyChildName || t.name == WaterSourceName)
                {
                    return true;
                }

                // Never treat ocean as pond.
                if (t.name == "Ocean")
                {
                    return false;
                }

                t = t.parent;
            }

            return false;
        }

        public static bool TryGetPondHitProxy(out Transform proxy)
        {
            proxy = null;
            var pond = GameObject.Find(WaterSourceName);
            if (pond == null)
            {
                return false;
            }

            var child = pond.transform.Find(ProxyChildName);
            if (child == null)
            {
                return false;
            }

            proxy = child;
            return child.GetComponent<Collider>() != null;
        }

        /// <summary>
        /// Ensures WaterSource/PondHitProxy hit collider exists. Idempotent; never touches Ocean.
        /// Pass the live pond instance when possible — GameObject.Find can hit a Destroy()-pending
        /// WaterSource during settlement upgrade.
        /// Returns false when WaterSource is not in the scene yet.
        /// </summary>
        public static bool EnsurePondHitProxy(GameObject pond = null)
        {
            if (pond == null)
            {
                pond = GameObject.Find(WaterSourceName);
            }

            if (pond == null || !pond.scene.IsValid() || pond.name != WaterSourceName)
            {
                return false;
            }

            // Visual WaterSource stays collider-free; hit target is the child proxy only.
            StripRootMeshColliders(pond);

            var proxy = pond.transform.Find(ProxyChildName);
            if (proxy == null)
            {
                var go = new GameObject(ProxyChildName);
                proxy = go.transform;
                proxy.SetParent(pond.transform, false);
            }

            proxy.localPosition = Vector3.zero;
            proxy.localRotation = Quaternion.identity;
            // Cancel flat WaterSource Y so box height is world metres, not crushed.
            var parentSy = Mathf.Abs(pond.transform.localScale.y);
            if (parentSy < 1e-4f)
            {
                parentSy = 1e-4f;
            }

            proxy.localScale = new Vector3(1f, 1f / parentSy, 1f);

            // Collapse accidental duplicate proxy children into one.
            for (var i = pond.transform.childCount - 1; i >= 0; i--)
            {
                var child = pond.transform.GetChild(i);
                if (child != null &&
                    child.name == ProxyChildName &&
                    child != proxy)
                {
                    if (Application.isPlaying)
                    {
                        Object.Destroy(child.gameObject);
                    }
                    else
                    {
                        Object.DestroyImmediate(child.gameObject);
                    }
                }
            }

            var meshCols = proxy.GetComponents<MeshCollider>();
            for (var i = 0; i < meshCols.Length; i++)
            {
                if (Application.isPlaying)
                {
                    Object.Destroy(meshCols[i]);
                }
                else
                {
                    Object.DestroyImmediate(meshCols[i]);
                }
            }

            var box = proxy.GetComponent<BoxCollider>();
            if (box == null)
            {
                box = proxy.gameObject.AddComponent<BoxCollider>();
            }

            // Match visible disc (±margin). Shallow slab — not a tall false-positive volume.
            box.center = new Vector3(0f, 0.2f, 0f);
            box.size = new Vector3(1.05f, 0.55f, 1.05f);
            box.isTrigger = false;
            return true;
        }

        private static void StripRootMeshColliders(GameObject pond)
        {
            var cols = pond.GetComponents<MeshCollider>();
            for (var i = 0; i < cols.Length; i++)
            {
                var col = cols[i];
                if (col == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    Object.Destroy(col);
                }
                else
                {
                    Object.DestroyImmediate(col);
                }
            }
        }
    }
}
