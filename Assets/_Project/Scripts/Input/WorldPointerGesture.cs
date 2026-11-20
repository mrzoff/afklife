using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace AfkIsland.Input
{
    /// <summary>
    /// CIF-B1: shared tap-vs-drag + UI hit tests for world interactions (planting first; fishing adopts in 10B).
    /// </summary>
    public static class WorldPointerGesture
    {
        public const float DefaultMaxTapMovePixels = 28f;
        public const float DefaultMaxTapSeconds = 0.45f;

        public struct PressState
        {
            public bool Active;
            public Vector2 StartScreenPos;
            public float StartUnscaledTime;
            public int FingerId;
            public bool CancelledAsDrag;
        }

        public static bool IsPointerOverUi(int fingerId = -1)
        {
            if (EventSystem.current == null)
            {
                return false;
            }

            if (fingerId >= 0)
            {
                return EventSystem.current.IsPointerOverGameObject(fingerId);
            }

            return EventSystem.current.IsPointerOverGameObject();
        }

        public static void BeginPress(ref PressState state, Vector2 screenPos, int fingerId)
        {
            if (IsPointerOverUi(fingerId))
            {
                Reset(ref state);
                return;
            }

            state.Active = true;
            state.StartScreenPos = screenPos;
            state.StartUnscaledTime = Time.unscaledTime;
            state.FingerId = fingerId;
            state.CancelledAsDrag = false;
        }

        public static void TrackMove(
            ref PressState state,
            Vector2 screenPos,
            float maxTapMovePixels = DefaultMaxTapMovePixels)
        {
            if (!state.Active || state.CancelledAsDrag)
            {
                return;
            }

            if (Vector2.Distance(screenPos, state.StartScreenPos) > maxTapMovePixels)
            {
                state.CancelledAsDrag = true;
            }
        }

        /// <summary>
        /// Ends the press. Returns true when it qualifies as a short, small-move world tap (not UI, not drag).
        /// </summary>
        public static bool TryCompleteTap(
            ref PressState state,
            Vector2 screenPos,
            out Vector2 tapScreenPos,
            float maxTapMovePixels = DefaultMaxTapMovePixels,
            float maxTapSeconds = DefaultMaxTapSeconds)
        {
            tapScreenPos = screenPos;
            if (!state.Active)
            {
                return false;
            }

            var cancelled = state.CancelledAsDrag;
            var shortEnough = (Time.unscaledTime - state.StartUnscaledTime) <= maxTapSeconds;
            var smallMove = Vector2.Distance(screenPos, state.StartScreenPos) <= maxTapMovePixels;
            Reset(ref state);

            if (cancelled || !shortEnough || !smallMove)
            {
                return false;
            }

            if (IsPointerOverUi(-1))
            {
                return false;
            }

            return true;
        }

        public static void Reset(ref PressState state)
        {
            state.Active = false;
            state.CancelledAsDrag = false;
            state.FingerId = -1;
        }

        /// <summary>
        /// Polls mouse and the pressed touch slot. Invokes <paramref name="onTap"/> when a world tap completes.
        /// </summary>
        public static void Poll(
            ref PressState state,
            System.Action<Vector2> onTap,
            float maxTapMovePixels = DefaultMaxTapMovePixels,
            float maxTapSeconds = DefaultMaxTapSeconds)
        {
            if (onTap == null)
            {
                return;
            }

            var touch = Touchscreen.current;
            var mouse = Mouse.current;
            var mouseDown = mouse != null && mouse.leftButton.isPressed;

            // BlueStacks delivers adb/host taps on a touch slot while primaryTouch stays idle.
            // A held mouse button still owns the gesture so a stuck slot cannot swallow clicks.
            if (!mouseDown && TryGetPressedTouch(touch, out var pos, out var fingerId))
            {
                if (!state.Active)
                {
                    BeginPress(ref state, pos, fingerId);
                }
                else if (state.FingerId == fingerId || state.FingerId < 0)
                {
                    TrackMove(ref state, pos, maxTapMovePixels);
                }

                return;
            }

            if (state.Active && state.FingerId >= 0)
            {
                var releasePos = state.StartScreenPos;
                if (touch != null)
                {
                    var slots = touch.touches;
                    for (var i = 0; i < slots.Count; i++)
                    {
                        if (slots[i].touchId.ReadValue() == state.FingerId)
                        {
                            releasePos = slots[i].position.ReadValue();
                            break;
                        }
                    }
                }

                if (TryCompleteTap(
                        ref state,
                        releasePos,
                        out var tapPos,
                        maxTapMovePixels,
                        maxTapSeconds))
                {
                    onTap(tapPos);
                }

                return;
            }

            if (mouseDown)
            {
                var mousePos = mouse.position.ReadValue();
                if (!state.Active)
                {
                    BeginPress(ref state, mousePos, -1);
                }
                else
                {
                    TrackMove(ref state, mousePos, maxTapMovePixels);
                }

                return;
            }

            if (mouse != null && state.Active && state.FingerId < 0 && !mouseDown)
            {
                if (TryCompleteTap(
                        ref state,
                        mouse.position.ReadValue(),
                        out var tapPos,
                        maxTapMovePixels,
                        maxTapSeconds))
                {
                    onTap(tapPos);
                }
            }
        }

        /// <summary>First touch slot that is actually pressed. Ignores an idle primaryTouch alias.</summary>
        private static bool TryGetPressedTouch(Touchscreen touch, out Vector2 pos, out int fingerId)
        {
            pos = default;
            fingerId = -1;
            if (touch == null)
            {
                return false;
            }

            var slots = touch.touches;
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (!slot.press.isPressed)
                {
                    continue;
                }

                pos = slot.position.ReadValue();
                fingerId = slot.touchId.ReadValue();
                return true;
            }

            return false;
        }
    }
}
