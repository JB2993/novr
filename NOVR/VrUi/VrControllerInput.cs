using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;

namespace NOVR.VrUi
{
    internal static class VrControllerInput
    {
        private static bool _diagnosticsLogged;
        private static bool _leftTriggerWasPressed;
        private static bool _rightTriggerWasPressed;

        private static InputAction? _rightAimPos;
        private static InputAction? _rightAimRot;
        private static InputAction? _leftAimPos;
        private static InputAction? _leftAimRot;
        private static InputAction? _rightTrigger;
        private static InputAction? _leftTrigger;
        private static bool _actionsInitialized;

        private static void EnsureActions()
        {
            if (_actionsInitialized) return;
            _actionsInitialized = true;

            _rightAimPos = new InputAction(binding: "<XRController>{RightHand}/pointerPosition");
            _rightAimPos.AddBinding("<XRController>{RightHand}/devicePosition");
            _rightAimRot = new InputAction(binding: "<XRController>{RightHand}/pointerRotation");
            _rightAimRot.AddBinding("<XRController>{RightHand}/deviceRotation");
            _leftAimPos = new InputAction(binding: "<XRController>{LeftHand}/pointerPosition");
            _leftAimPos.AddBinding("<XRController>{LeftHand}/devicePosition");
            _leftAimRot = new InputAction(binding: "<XRController>{LeftHand}/pointerRotation");
            _leftAimRot.AddBinding("<XRController>{LeftHand}/deviceRotation");

            _rightTrigger = new InputAction(type: InputActionType.Button, binding: "<XRController>{RightHand}/triggerPressed");
            _rightTrigger.AddBinding("<XRController>{RightHand}/trigger");
            _leftTrigger = new InputAction(type: InputActionType.Button, binding: "<XRController>{LeftHand}/triggerPressed");
            _leftTrigger.AddBinding("<XRController>{LeftHand}/trigger");

            _rightAimPos.Enable();
            _rightAimRot.Enable();
            _leftAimPos.Enable();
            _leftAimRot.Enable();
            _rightTrigger.Enable();
            _leftTrigger.Enable();

            InputSystem.onDeviceChange += OnDeviceChange;
        }

        private static void OnDeviceChange(UnityEngine.InputSystem.InputDevice device, InputDeviceChange change)
        {
            Debug.Log($"[VrControllerInput] DeviceChange: {change} name={device.name} layout={device.layout}");
            if (change == InputDeviceChange.Added || change == InputDeviceChange.Removed)
                LogDiagnostics();
        }

        internal static void LogDiagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== VrControllerInput Diagnostics ===");

            sb.AppendLine("--- InputSystem.devices ---");
            foreach (var d in InputSystem.devices)
            {
                bool isXR = d is XRController;
                sb.AppendLine($"  {(isXR ? "[XR]" : "     ")} name={d.name} layout={d.layout} usages=[{string.Join(",", d.usages)}] desc.interface={d.description.interfaceName} desc.product={d.description.product}");
            }

            sb.AppendLine("--- InputSystem.GetUnsupportedDevices ---");
            foreach (var d in InputSystem.GetUnsupportedDevices())
                sb.AppendLine($"  interface={d.interfaceName} product={d.product} manufacturer={d.manufacturer}");

            sb.AppendLine("--- XRInputSubsystem ---");
            var subsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);
            foreach (var s in subsystems)
                sb.AppendLine($"  running={s.running}");

            // Try to log OpenXR features via reflection (OpenXR might not be directly referenced)
            try
            {
                var openXRSettingsType = System.Type.GetType("UnityEngine.XR.OpenXR.OpenXRSettings, Unity.XR.OpenXR");
                if (openXRSettingsType != null)
                {
                    var instanceProp = openXRSettingsType.GetProperty("Instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (instanceProp != null)
                    {
                        var instance = instanceProp.GetValue(null);
                        var featuresProp = openXRSettingsType.GetProperty("features");
                        if (featuresProp != null && instance != null)
                        {
                            var features = featuresProp.GetValue(instance) as System.Collections.IList;
                            if (features != null)
                            {
                                sb.AppendLine("--- OpenXR Features ---");
                                foreach (var f in features)
                                {
                                    var nameProp = f.GetType().GetProperty("name");
                                    var enabledProp = f.GetType().GetProperty("enabled");
                                    string fName = nameProp?.GetValue(f)?.ToString() ?? "(no name)";
                                    bool fEnabled = enabledProp != null && (bool)(enabledProp.GetValue(f) ?? false);
                                    sb.AppendLine($"  {fName} enabled={fEnabled}");
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                sb.AppendLine($"  (OpenXR diagnostics error: {ex.Message})");
            }

            sb.AppendLine("--- InputAction bindings (sample values) ---");
            var rightPos = TryReadActionVec3(_rightAimPos);
            var rightRot = TryReadActionQuat(_rightAimRot);
            var leftPos = TryReadActionVec3(_leftAimPos);
            var leftRot = TryReadActionQuat(_leftAimRot);
            sb.AppendLine($"  RightHand aimPos={rightPos} aimRot={rightRot.eulerAngles}");
            sb.AppendLine($"  LeftHand  aimPos={leftPos} aimRot={leftRot.eulerAngles}");
            sb.AppendLine($"  RightHand trigger={(_rightTrigger?.ReadValue<float>() ?? 0):F2}");
            sb.AppendLine($"  LeftHand  trigger={(_leftTrigger?.ReadValue<float>() ?? 0):F2}");

            sb.AppendLine("=== End Diagnostics ===");
            Debug.Log(sb.ToString());
        }

        private static Vector3 TryReadActionVec3(InputAction? action)
        {
            if (action == null) return Vector3.zero;
            try { return action.ReadValue<Vector3>(); }
            catch { return Vector3.zero; }
        }

        private static Quaternion TryReadActionQuat(InputAction? action)
        {
            if (action == null) return Quaternion.identity;
            try { return action.ReadValue<Quaternion>(); }
            catch { return Quaternion.identity; }
        }

        public static bool TryGetPose(XRNode hand, out Vector3 position, out Quaternion rotation)
        {
            EnsureActions();
            if (!_diagnosticsLogged)
            {
                _diagnosticsLogged = true;
                LogDiagnostics();
            }

            bool isLeft = hand == XRNode.LeftHand;
            var posAction = isLeft ? _leftAimPos : _rightAimPos;
            var rotAction = isLeft ? _leftAimRot : _rightAimRot;

            if (posAction != null && rotAction != null)
            {
                try
                {
                    position = posAction.ReadValue<Vector3>();
                    rotation = rotAction.ReadValue<Quaternion>();
                    // Check if the values are meaningful (non-zero position suggests tracking)
                    if (position.sqrMagnitude > 0.0001f)
                        return true;
                }
                catch
                {
                    // Action not bound to any device
                }
            }

            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        public static bool GetTrigger(XRNode hand)
        {
            EnsureActions();
            var action = hand == XRNode.LeftHand ? _leftTrigger : _rightTrigger;
            if (action == null) return false;
            try { return action.ReadValue<float>() > 0.5f; }
            catch { return false; }
        }

        public static bool GetTriggerWasPressedThisFrame(XRNode hand)
        {
            bool current = GetTrigger(hand);
            bool prev = hand == XRNode.LeftHand ? _leftTriggerWasPressed : _rightTriggerWasPressed;
            if (hand == XRNode.LeftHand)
                _leftTriggerWasPressed = current;
            else
                _rightTriggerWasPressed = current;
            return current && !prev;
        }

        public static bool GetTriggerWasReleasedThisFrame(XRNode hand)
        {
            bool current = GetTrigger(hand);
            bool prev = hand == XRNode.LeftHand ? _leftTriggerWasPressed : _rightTriggerWasPressed;
            if (hand == XRNode.LeftHand)
                _leftTriggerWasPressed = current;
            else
                _rightTriggerWasPressed = current;
            return !current && prev;
        }

        public static bool TryGetDominantHand(out Vector3 position, out Quaternion rotation, out bool triggerPressed)
        {
            bool leftValid = TryGetPose(XRNode.LeftHand, out var leftPos, out var leftRot);
            bool rightValid = TryGetPose(XRNode.RightHand, out var rightPos, out var rightRot);

            if (leftValid && !rightValid)
            {
                position = leftPos;
                rotation = leftRot;
                triggerPressed = GetTrigger(XRNode.LeftHand);
                return true;
            }
            if (rightValid && !leftValid)
            {
                position = rightPos;
                rotation = rightRot;
                triggerPressed = GetTrigger(XRNode.RightHand);
                return true;
            }

            if (!leftValid && !rightValid)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                triggerPressed = false;
                return false;
            }

            bool rightTrigger = GetTrigger(XRNode.RightHand);
            bool leftTrigger = GetTrigger(XRNode.LeftHand);

            if (rightTrigger)
            {
                position = rightPos;
                rotation = rightRot;
                triggerPressed = true;
                return true;
            }
            if (leftTrigger)
            {
                position = leftPos;
                rotation = leftRot;
                triggerPressed = true;
                return true;
            }

            position = rightPos;
            rotation = rightRot;
            triggerPressed = false;
            return true;
        }
    }
}
