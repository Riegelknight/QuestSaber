using System;
using UnityEngine;

namespace QuestSaber
{
    /// <summary>
    /// Provides the state of the Quest controllers once per frame:
    /// X/Y (left), A/B (right), grip (side button) and main/index trigger with
    /// edge detection and hold time. Uses UnityEngine.XR.InputDevices - independent
    /// of XRI/InputSystem.
    /// Button mapping: grip (side) = easter egg double click (saber retract/extend),
    /// main/index trigger = "Confirm" in menu navigation.
    /// </summary>
    public sealed class ControllerInput
    {
        public bool LeftXWasPressedThisFrame { get; private set; }
        public bool LeftYWasPressedThisFrame { get; private set; }
        public bool RightAWasPressedThisFrame { get; private set; }
        public bool RightBWasPressedThisFrame { get; private set; }

        /// <summary>Side button (grip) - pressed edge (ONLY easter egg double click: saber retract/extend).</summary>
        public bool LeftGripWasPressedThisFrame { get; private set; }
        public bool RightGripWasPressedThisFrame { get; private set; }

        /// <summary>Main/index trigger - pressed edge ("Confirm" in menu navigation).</summary>
        public bool LeftMainTriggerWasPressedThisFrame { get; private set; }
        public bool RightMainTriggerWasPressedThisFrame { get; private set; }

        /// <summary>Double click (2 presses within ~0.4 s) of the grip button - easter egg:
        /// retracts/extends the respective saber (possible at any time).</summary>
        public bool LeftGripDoubleClicked { get; private set; }
        public bool RightGripDoubleClicked { get; private set; }

        /// <summary>Released after &lt; 2 s (short) - for opening/closing the menu.</summary>
        public bool LeftYReleasedShort { get; private set; }
        public bool RightBReleasedShort { get; private set; }

        /// <summary>Analog stick ("joystick") per hand: x/y -1..1 (up/forward = +1).</summary>
        public float LeftThumbX { get; private set; }
        public float LeftThumbY { get; private set; }
        public float RightThumbX { get; private set; }
        public float RightThumbY { get; private set; }

        /// <summary>Y held for >= 2 s - with <see cref="RightBHeldLong"/> the ONE way to quit
        /// (once per hold duration). Deliberately the only quit path: no menu-button hold and no
        /// Y+B combo (removed - "ausschliesslich Y/B ueber 2 s").</summary>
        public bool LeftYHeldLong { get; private set; }
        private bool _leftYLongFired;

        /// <summary>B held for >= 2 s - with <see cref="LeftYHeldLong"/> the ONE way to quit
        /// (once per hold duration).</summary>
        public bool RightBHeldLong { get; private set; }
        private bool _rightBLongFired;

        /// <summary>Hold time (s) of the respective button, 0 when not pressed.</summary>
        public float LeftYHoldTime { get; private set; }
        public float RightBHoldTime { get; private set; }

        private bool _leftXWas;
        private bool _leftYWas;
        private bool _rightAWas;
        private bool _rightBWas;
        private bool _leftGripWas;
        private bool _rightGripWas;
        private bool _leftMainTriggerWas;
        private bool _rightMainTriggerWas;
        private float _lastLeftGripPress = -1f;
        private float _lastRightGripPress = -1f;
        private const float GripDoubleClickWindow = 0.4f;
        private float _leftYDownSince;
        private float _rightBDownSince;

        private UnityEngine.XR.InputDevice _left;
        private UnityEngine.XR.InputDevice _right;

        public ControllerInput()
        {
            RefreshDevices();
        }

        private void RefreshDevices()
        {
            if (!_left.isValid) _left = GetDevice(UnityEngine.XR.XRNode.LeftHand);
            if (!_right.isValid) _right = GetDevice(UnityEngine.XR.XRNode.RightHand);
        }

        // Reused buffer: no list allocation when controllers need to be (re)detected
        // (e.g. after a brief loss of tracking).
        private static readonly System.Collections.Generic.List<UnityEngine.XR.InputDevice> _deviceBuffer
            = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>(2);

        private static UnityEngine.XR.InputDevice GetDevice(UnityEngine.XR.XRNode node)
        {
            _deviceBuffer.Clear();
            UnityEngine.XR.InputDevices.GetDevicesAtXRNode(node, _deviceBuffer);
            for (int i = 0; i < _deviceBuffer.Count; i++)
            {
                if (_deviceBuffer[i].isValid) return _deviceBuffer[i];
            }
            return default;
        }

        /// <summary>Should be called every frame.</summary>
        public void Update()
        {
            RefreshDevices();

            bool x = ReadButton(_left, UnityEngine.XR.CommonUsages.primaryButton);
            bool y = ReadButton(_left, UnityEngine.XR.CommonUsages.secondaryButton);
            bool a = ReadButton(_right, UnityEngine.XR.CommonUsages.primaryButton);
            bool b = ReadButton(_right, UnityEngine.XR.CommonUsages.secondaryButton);
            // Side button (grip) - ONLY easter egg double click (saber retract/extend)
            bool lg = ReadButton(_left, UnityEngine.XR.CommonUsages.gripButton);
            bool rg = ReadButton(_right, UnityEngine.XR.CommonUsages.gripButton);
            // Main/index trigger - "Confirm" in menu navigation
            bool lmt = ReadButton(_left, UnityEngine.XR.CommonUsages.triggerButton);
            bool rmt = ReadButton(_right, UnityEngine.XR.CommonUsages.triggerButton);

            // Analog sticks (joystick)
            Vector2 lstick = ReadAxis(_left, UnityEngine.XR.CommonUsages.primary2DAxis);
            Vector2 rstick = ReadAxis(_right, UnityEngine.XR.CommonUsages.primary2DAxis);
            LeftThumbX = lstick.x; LeftThumbY = lstick.y;
            RightThumbX = rstick.x; RightThumbY = rstick.y;

            // ----- Edges -----
            LeftXWasPressedThisFrame = x && !_leftXWas;
            LeftYWasPressedThisFrame = y && !_leftYWas;
            RightAWasPressedThisFrame = a && !_rightAWas;
            RightBWasPressedThisFrame = b && !_rightBWas;
            LeftGripWasPressedThisFrame = lg && !_leftGripWas;
            RightGripWasPressedThisFrame = rg && !_rightGripWas;
            LeftMainTriggerWasPressedThisFrame = lmt && !_leftMainTriggerWas;
            RightMainTriggerWasPressedThisFrame = rmt && !_rightMainTriggerWas;

            // ---- Double click (easter egg): two grip presses (SIDE button) within
            // a short window retract/extend the respective saber. ----
            LeftGripDoubleClicked = false;
            RightGripDoubleClicked = false;
            if (LeftGripWasPressedThisFrame)
            {
                float now = Time.unscaledTime;
                LeftGripDoubleClicked = _lastLeftGripPress >= 0f
                                        && now - _lastLeftGripPress <= GripDoubleClickWindow;
                // Count anew after a double click; expired single presses are
                // automatically discarded by the window check above.
                _lastLeftGripPress = LeftGripDoubleClicked ? -1f : now;
            }
            if (RightGripWasPressedThisFrame)
            {
                float now = Time.unscaledTime;
                RightGripDoubleClicked = _lastRightGripPress >= 0f
                                         && now - _lastRightGripPress <= GripDoubleClickWindow;
                _lastRightGripPress = RightGripDoubleClicked ? -1f : now;
            }

            // ----- Hold times -----
            if (y && !_leftYWas) _leftYDownSince = Time.unscaledTime;
            if (b && !_rightBWas) _rightBDownSince = Time.unscaledTime;

            LeftYHoldTime = y ? Time.unscaledTime - _leftYDownSince : 0f;
            RightBHoldTime = b ? Time.unscaledTime - _rightBDownSince : 0f;

            // Short release (duration < 2 s)
            LeftYReleasedShort = (!y && _leftYWas) && (Time.unscaledTime - _leftYDownSince) < 2f;
            RightBReleasedShort = (!b && _rightBWas) && (Time.unscaledTime - _rightBDownSince) < 2f;

            // ---- Holding Y or B long: the ONE quit path (>= 2 s, see LeftYHeldLong) ----
            if (y && LeftYHoldTime >= 2f && !_leftYLongFired)
            {
                _leftYLongFired = true;
                LeftYHeldLong = true;
            }
            else
            {
                LeftYHeldLong = false;
            }
            if (!y) _leftYLongFired = false;

            if (b && RightBHoldTime >= 2f && !_rightBLongFired)
            {
                _rightBLongFired = true;
                RightBHeldLong = true;
            }
            else
            {
                RightBHeldLong = false;
            }
            if (!b) _rightBLongFired = false;

            _leftXWas = x;
            _leftYWas = y;
            _rightAWas = a;
            _rightBWas = b;
            _leftGripWas = lg;
            _rightGripWas = rg;
            _leftMainTriggerWas = lmt;
            _rightMainTriggerWas = rmt;
        }

        private static bool ReadButton(UnityEngine.XR.InputDevice device, UnityEngine.XR.InputFeatureUsage<bool> usage)
        {
            if (!device.isValid) return false;
            return device.TryGetFeatureValue(usage, out bool value) && value;
        }

        private static Vector2 ReadAxis(UnityEngine.XR.InputDevice device, UnityEngine.XR.InputFeatureUsage<Vector2> usage)
        {
            if (!device.isValid) return Vector2.zero;
            return device.TryGetFeatureValue(usage, out Vector2 value) ? value : Vector2.zero;
        }
    }
}
