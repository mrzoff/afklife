using AfkIsland.Input;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace AfkIsland.CameraControl
{
    /// <summary>
    /// Orthographic isometric camera: drag to pan, pinch/scroll/keys to zoom.
    /// Pan starts only after drag threshold so short taps (pond/ocean/plant) are not stolen.
    /// Stage 11E: pressed-touch count (not slot Count). BlueStacks: mouse + keys beat phantom touch.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class IsometricCameraController : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 0.015f;
        [SerializeField] private float zoomSpeed = 2f;
        [SerializeField] private float minOrthoSize = 5f;
        [SerializeField] private float maxOrthoSize = 16f;
        [SerializeField] private float defaultOrthoSize = 8.5f;
        [SerializeField] private Vector2 panLimit = new Vector2(12f, 12f);
        [SerializeField] private Vector3 focusPoint = new Vector3(0f, 1.2f, 0f);
        [SerializeField] private float panDragThresholdPixels = WorldPointerGesture.DefaultMaxTapMovePixels;
        [SerializeField] private float keyZoomStep = 0.55f;

        private Camera _camera;
        private Vector2? _pressOrigin;
        private Vector2? _lastPointerPos;
        private bool _panning;
        private float _lastPinchDistance = -1f;

        /// <summary>When true, pan/zoom ignore pointers (water tap / fishing / UI holds the gesture).</summary>
        public bool WorldGestureBlocked { get; set; }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _camera.orthographic = true;
            ApplyIsometricPose();
            AndroidMouseScroll.Install();
        }

        private void Update()
        {
            if (WorldGestureBlocked)
            {
                ResetPanTracking();
                _lastPinchDistance = -1f;
                AndroidMouseScroll.ConsumeY();
                return;
            }

            HandlePan();
            HandleZoom();
            ClampPosition();
        }

        private void ApplyIsometricPose()
        {
            transform.rotation = Quaternion.Euler(45f, 45f, 0f);
            var offset = transform.rotation * new Vector3(0f, 0f, -40f);
            transform.position = focusPoint + offset;
        }

        private void HandlePan()
        {
            var mouse = Mouse.current;
            var touch = Touchscreen.current;
            var mouseDown = mouse != null && mouse.leftButton.isPressed;
            var pressed = CountPressedTouches(touch);

            // BlueStacks / desktop: LMB owns pan — ignore phantom Touchscreen presses.
            if (mouseDown)
            {
                _lastPinchDistance = -1f;
                var pos = mouse.position.ReadValue();
                if (WorldPointerGesture.IsPointerOverUi(-1))
                {
                    ResetPanTracking();
                    return;
                }

                TrackPanPress(pos);
                return;
            }

            if (pressed >= 2)
            {
                ResetPanTracking();
                return;
            }

            if (pressed == 1 && TryGetPressedTouch(touch, 0, out var primary))
            {
                var pos = primary.position.ReadValue();
                if (WorldPointerGesture.IsPointerOverUi(primary.touchId.ReadValue()))
                {
                    ResetPanTracking();
                    return;
                }

                TrackPanPress(pos);
                return;
            }

            ResetPanTracking();
        }

        private void TrackPanPress(Vector2 pos)
        {
            if (!_pressOrigin.HasValue)
            {
                _pressOrigin = pos;
                _lastPointerPos = pos;
                _panning = false;
                return;
            }

            if (!_panning &&
                Vector2.Distance(pos, _pressOrigin.Value) > Mathf.Max(1f, panDragThresholdPixels))
            {
                _panning = true;
                _lastPointerPos = pos;
                return;
            }

            if (_panning && _lastPointerPos.HasValue)
            {
                var delta = pos - _lastPointerPos.Value;
                if (delta.sqrMagnitude > 0.0001f)
                {
                    PanByScreenDelta(delta);
                }
            }

            _lastPointerPos = pos;
        }

        private void ResetPanTracking()
        {
            _pressOrigin = null;
            _lastPointerPos = null;
            _panning = false;
        }

        private void PanByScreenDelta(Vector2 screenDelta)
        {
            var right = transform.right;
            var up = Vector3.ProjectOnPlane(transform.up, Vector3.up).normalized;
            var move = (-right * screenDelta.x - up * screenDelta.y) * moveSpeed * (_camera.orthographicSize / 10f);
            transform.position += move;
            focusPoint += move;
        }

        private void HandleZoom()
        {
            var mouse = Mouse.current;
            var touch = Touchscreen.current;
            var keyboard = Keyboard.current;
            var mouseDown = mouse != null && mouse.leftButton.isPressed;
            var hasMouse = mouse != null;
            var pressed = CountPressedTouches(touch);

            // True-device pinch only when there is no mouse (BlueStacks ghosts look like 2 touches).
            if (!hasMouse &&
                pressed >= 2 &&
                TryGetPressedTouch(touch, 0, out var t0) &&
                TryGetPressedTouch(touch, 1, out var t1))
            {
                var a = t0.position.ReadValue();
                var b = t1.position.ReadValue();
                var dist = Vector2.Distance(a, b);
                if (_lastPinchDistance > 0f)
                {
                    ApplyOrthoDelta((_lastPinchDistance - dist) * 0.02f);
                }

                _lastPinchDistance = dist;
                return;
            }

            _lastPinchDistance = -1f;

            // Keyboard zoom — reliable on BlueStacks even when scroll is swallowed.
            if (keyboard != null)
            {
                if (keyboard.equalsKey.wasPressedThisFrame || keyboard.numpadPlusKey.wasPressedThisFrame)
                {
                    ApplyOrthoDelta(-keyZoomStep);
                }

                if (keyboard.minusKey.wasPressedThisFrame || keyboard.numpadMinusKey.wasPressedThisFrame)
                {
                    ApplyOrthoDelta(keyZoomStep);
                }
            }

            // Scroll: only suppress while LMB is held (click-injected ticks). Do NOT gate on
            // pressed-touch count — BlueStacks often leaves a phantom pressed slot forever.
            if (mouseDown)
            {
                return;
            }

            var scroll = ReadScrollY(mouse);
            if (Mathf.Abs(scroll) < 0.01f)
            {
                return;
            }

            // Input System may report ~1 or ~120 per notch depending on platform.
            var delta = Mathf.Abs(scroll) >= 20f
                ? scroll * 0.01f * zoomSpeed
                : scroll * 0.45f * zoomSpeed;
            ApplyOrthoDelta(delta);
        }

        private static float ReadScrollY(Mouse mouse)
        {
            var mouseY = mouse != null ? mouse.scroll.ReadValue().y : 0f;
            return AndroidMouseScroll.Prefer(mouseY, AndroidMouseScroll.ConsumeY());
        }

        private void ApplyOrthoDelta(float delta)
        {
            if (_camera == null || Mathf.Abs(delta) < 0.0001f)
            {
                return;
            }

            _camera.orthographicSize = Mathf.Clamp(
                _camera.orthographicSize + delta,
                minOrthoSize,
                maxOrthoSize);
        }

        private void ClampPosition()
        {
            var pos = transform.position;
            var offset = pos - focusPoint;
            focusPoint.x = Mathf.Clamp(focusPoint.x, -panLimit.x, panLimit.x);
            focusPoint.z = Mathf.Clamp(focusPoint.z, -panLimit.y, panLimit.y);
            transform.position = focusPoint + offset;
        }

        public void ResetToIslandCenter()
        {
            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
            }

            focusPoint = new Vector3(0f, 1.2f, 0f);
            ApplyIsometricPose();
            _camera.orthographic = true;
            _camera.orthographicSize = defaultOrthoSize;
        }

        private static int CountPressedTouches(Touchscreen touch)
        {
            if (touch == null)
            {
                return 0;
            }

            var n = 0;
            var slots = touch.touches;
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i].press.isPressed)
                {
                    n++;
                }
            }

            return n;
        }

        private static bool TryGetPressedTouch(Touchscreen touch, int pressedIndex, out TouchControl control)
        {
            control = null;
            if (touch == null)
            {
                return false;
            }

            var seen = 0;
            var slots = touch.touches;
            for (var i = 0; i < slots.Count; i++)
            {
                if (!slots[i].press.isPressed)
                {
                    continue;
                }

                if (seen == pressedIndex)
                {
                    control = slots[i];
                    return true;
                }

                seen++;
            }

            return false;
        }
    }
}
