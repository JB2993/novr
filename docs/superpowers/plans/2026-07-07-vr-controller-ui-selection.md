# VR Controller Ray UI Selection

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow VR hand controllers to cast a ray along the controller's pointing axis to select/interact with UI elements, complementing the existing mouse-driven cursor.

**Architecture:** A new `VrControllerInput` static class reads controller pose and trigger state via the legacy `UnityEngine.XR` API (same API family as the existing `NOVRHeadsetData`). `VrUiCursor` is extended with a controller-input mode that replaces the mouse-based probe ray with a controller-originated ray (`VrCanvasHitTester` already supports world-space ray intersection). The trigger button drives click/drag events. A config toggle selects between mouse, controller, or auto-detect input mode. A simple laser line visual (LineRenderer) shows the ray from controller to canvas hit point.

**Tech Stack:** Unity C#, UnityEngine.XR (InputDevices), InputSystem (VirtualMouse), UGUI (GraphicRaycaster, Canvas, RectTransform), BepInEx Config

## Global Constraints

- All VR canvases use `RenderMode.WorldSpace` with `worldCamera = APIBus.CockpitHudCamera`
- Canvases are on layer 30 (`VrUi`)
- Camera is head-tracked via `NOVRPoseDriver` (XRNode.CenterEye) in Update, LateUpdate, AND OnBeforeRender
- Controller pose is read via `UnityEngine.XR.InputDevices.GetDeviceAtXRNode()` — the legacy XR API (no Input System dependency for controller data)
- The existing `VrCanvasHitTester` is used for world-space ray-plane intersection (no changes needed)
- The trigger button is used for "click" (replaces mouse left button in controller mode)
- Config entries go in `ModConfiguration.cs` alongside existing entries

---

## File Structure

| File | Responsibility | Change |
|------|---------------|--------|
| `NOVR/VrUi/VrControllerInput.cs` | Read controller pose + trigger state via legacy XR API, expose statically | **Create** |
| `NOVR/VrUi/VrUiCursor.cs` | Cursor pipeline: probe ray source, event dispatch | **Modify** — add controller input mode alongside mouse |
| `NOVR/VrUi/VrControllerLaser.cs` | Optional visual laser line from controller to canvas hit | **Create** |
| `NOVR/ModConfiguration.cs` | BepInEx config entries | **Modify** — add `CursorInputMode` config |
| `NOVR/VrUi/NOUIManager.cs` | Creates VrUiCursor and the laser | **Modify** — create VrControllerLaser |

---

### Task 1: Create VrControllerInput — controller pose + trigger reader

**Files:**
- Create: `NOVR/VrUi/VrControllerInput.cs`

**Interfaces:**
- Produces: `VrControllerInput.TryGetDominantHand(out Vector3 pos, out Quaternion rot, out bool triggerPressed) → bool`
- Consumes: `UnityEngine.XR.InputDevices`, `UnityEngine.XR.XRNode`, `UnityEngine.XR.CommonUsages`

**Design:**
A static utility that polls `InputDevices.GetDeviceAtXRNode()` for both `XRNode.LeftHand` and `XRNode.RightHand` each frame. Caches the device references (re-queries when a device becomes invalid). Exposes a single `TryGetDominantHand` method that returns the most recently active (tracked + trigger was pressed) hand's pose and trigger state. Falls back to whichever hand is tracked.

- [ ] **Step 1: Write `VrControllerInput.cs`**

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace NOVR.VrUi
{
    internal static class VrControllerInput
    {
        private static InputDevice _leftDevice;
        private static InputDevice _rightDevice;
        private static bool _leftQueried;
        private static bool _rightQueried;
        private static bool _leftTriggerWasPressed;
        private static bool _rightTriggerWasPressed;

        private static InputDevice GetDevice(XRNode hand)
        {
            if (hand == XRNode.LeftHand)
            {
                if (!_leftQueried || !_leftDevice.isValid)
                {
                    _leftDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                    _leftQueried = true;
                }
                return _leftDevice;
            }
            else
            {
                if (!_rightQueried || !_rightDevice.isValid)
                {
                    _rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
                    _rightQueried = true;
                }
                return _rightDevice;
            }
        }

        public static bool TryGetPose(XRNode hand, out Vector3 position, out Quaternion rotation)
        {
            var device = GetDevice(hand);
            if (!device.isValid)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }

            if (!device.TryGetFeatureValue(CommonUsages.devicePosition, out position))
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }

            if (!device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation))
            {
                rotation = Quaternion.identity;
                return false;
            }

            return true;
        }

        public static bool GetTrigger(XRNode hand)
        {
            var device = GetDevice(hand);
            if (!device.isValid) return false;
            return device.TryGetFeatureValue(CommonUsages.triggerButton, out bool pressed) && pressed;
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

        /// <summary>
        /// Returns the dominant hand's pose and trigger state.
        /// "Dominant" = the hand that was most recently used (had a trigger press).
        /// Falls back to whichever hand is tracked. Prefers right hand on first use.
        /// </summary>
        public static bool TryGetDominantHand(out Vector3 position, out Quaternion rotation, out bool triggerPressed)
        {
            bool leftValid = TryGetPose(XRNode.LeftHand, out var leftPos, out var leftRot);
            bool rightValid = TryGetPose(XRNode.RightHand, out var rightPos, out var rightRot);

            // If only one hand is tracked, use it
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

            // Both tracked — pick dominant by most recent trigger press, fallback to right
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

            // Neither pressed — use right by default
            position = rightPos;
            rotation = rightRot;
            triggerPressed = false;
            return true;
        }
    }
}
```

- [ ] **Step 2: Verify compilation**

Run: `dotnet build NOVR/NOVR.csproj`
Expected: Build succeeds, no errors.

- [ ] **Step 3: Commit**

```bash
git add NOVR/VrUi/VrControllerInput.cs
git commit -m "feat: add VrControllerInput for reading VR controller pose and trigger state"
```

---

### Task 2: Add CursorInputMode config entry to ModConfiguration

**Files:**
- Modify: `NOVR/ModConfiguration.cs`

**Interfaces:**
- Produces: `ModConfiguration.CursorInputMode` — a `ConfigEntry<string>` with values `"Auto"`, `"Mouse"`, `"Controller"`
- Consumed by: `VrUiCursor` (Task 3)

- [ ] **Step 1: Add config entry**

Add after line 48 (after `NativeMenuHeightOffset`):

```csharp
public readonly ConfigEntry<string> CursorInputMode;

// In constructor, after NativeMenuHeightOffset binding:
CursorInputMode = config.Bind(
    "Experimental",
    "Cursor Input Mode",
    "Auto",
    "Selects input source for the VR UI cursor. 'Auto' = use controller if tracked, else mouse. 'Mouse' = always use mouse. 'Controller' = always use controller ray.");
```

- [ ] **Step 2: Verify compilation**

Run: `dotnet build NOVR/NOVR.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add NOVR/ModConfiguration.cs
git commit -m "feat: add CursorInputMode config entry for controller vs mouse input"
```

---

### Task 3: Extend VrUiCursor with controller input mode

**Files:**
- Modify: `NOVR/VrUi/VrUiCursor.cs`

**Design:**
Add controller input mode alongside the existing mouse mode. In `Update()`, if controller mode is active and a tracked controller is detected, use `VrControllerInput.TryGetDominantHand()` for the probe ray origin/direction and trigger for click state. The existing `VrCanvasHitTester.RaycastCanvasPlanes()` and `FirePointerEvents()` work unchanged — they consume the cursor world position and a boolean click state.

Key changes in `VrUiCursor`:
1. Add `_controllerModeActive` bool field
2. Add `_triggerWasPressed` and `_triggerIsPressed` state tracking fields  
3. Modify `UpdateCursorAngles()` to accept an optional `useControllerRay` parameter — in controller mode, compute probe ray from controller position * controller forward
4. Modify `Update()` to read config mode, poll controller, and branch accordingly
5. Replace `realMouse.leftButton.isPressed` with trigger state in controller mode for `FirePointerEvents` call
6. Modify `UpdateCursorAnimation()` to use trigger state instead of mouse button state in controller mode
7. Modify diagnostic logging to include controller pose

- [ ] **Step 1: Add fields and helper methods**

After the existing field declarations (around line 77), add:

```csharp
// Controller input mode
private bool _controllerModeActive;
private bool _triggerIsPressed;
private bool _triggerWasPressed;
private Vector3 _controllerOrigin;
private Vector3 _controllerDirection;
```

After `GetScreenPoint()` (after line 115), add:

```csharp
public bool IsControllerModeActive => _controllerModeActive;
```

- [ ] **Step 2: Modify `Update()` method**

Replace lines 134-200 (the `Update()` method) with:

```csharp
private void Update()
{
    if (!Application.isFocused)
    {
        if (_cursor != null && _cursor.activeSelf)
            _cursor.SetActive(false);
        return;
    }

    if (_realMouse == null)
    {
        _realMouse = Mouse.current ?? throw new System.InvalidOperationException(
            $"[{nameof(VrUiCursor)}] Unity InputSystem could not find an active hardware Mouse device during initialization.");
    }

    if (Time.frameCount < 120)
        DisableStandardUIModule();

    UpdateStandardUIModuleState();
    if (_texture == null) return;

    // Determine input mode
    string modeSetting = ModConfiguration.Instance.CursorInputMode.Value;
    bool controllerAvailable = VrControllerInput.TryGetDominantHand(
        out _controllerOrigin, out _controllerDirection, out _triggerIsPressed);

    bool useController = modeSetting == "Controller" ||
                         (modeSetting == "Auto" && controllerAvailable);

    if (useController && controllerAvailable)
    {
        _controllerModeActive = true;
        _triggerWasPressed = _triggerIsPressed && !_triggerWasPressed;

        // Use trigger was-pressed tracking for animation
        bool triggerDownThisFrame = VrControllerInput.GetTriggerWasPressedThisFrame(
            XRNode.RightHand) || VrControllerInput.GetTriggerWasPressedThisFrame(
            XRNode.LeftHand);

        UpdateCursorAnglesFromController();
        _controllerModeActive = true;

        Vector2 screenPoint = GetScreenPoint();
        if (!_isOffscreen)
        {
            _feedFrame = Time.frameCount;
            _feedScreenPoint = screenPoint;
            _feedCursorWorldPos = _cursor != null ? _cursor.transform.position : Vector3.zero;
            var snapCam = UiCamera;
            if (snapCam != null)
            {
                _feedCameraPos = snapCam.transform.position;
                _feedCameraRot = snapCam.transform.rotation;
                var p = snapCam.projectionMatrix;
                _feedProjM00 = p.m00; _feedProjM11 = p.m11;
                _feedProjM02 = p.m02; _feedProjM12 = p.m12;
            }

            FirePointerEvents(screenPoint, _triggerIsPressed);
        }

        UpdateCursorAnimation(triggerDownThisFrame, _triggerIsPressed);

        if (triggerDownThisFrame)
            LogRaycastAtCursor();

        _triggerWasPressed = _triggerIsPressed;
    }
    else
    {
        _controllerModeActive = false;
        if (!IsRealCursorVisible())
        {
            if (_cursor != null)
                _cursor.SetActive(false);
            return;
        }

        UpdateCursorAngles();
        var realMouse = _realMouse;
        if (realMouse == null) return;

        Vector2 screenPoint = GetScreenPoint();
        if (!_isOffscreen)
        {
            _feedFrame = Time.frameCount;
            _feedScreenPoint = screenPoint;
            _feedCursorWorldPos = _cursor != null ? _cursor.transform.position : Vector3.zero;
            var snapCam = UiCamera;
            if (snapCam != null)
            {
                _feedCameraPos = snapCam.transform.position;
                _feedCameraRot = snapCam.transform.rotation;
                var p = snapCam.projectionMatrix;
                _feedProjM00 = p.m00; _feedProjM11 = p.m11;
                _feedProjM02 = p.m02; _feedProjM12 = p.m12;
            }
            FirePointerEvents(screenPoint, realMouse.leftButton.isPressed);
        }

        UpdateCursorAnimation(realMouse.leftButton.wasPressedThisFrame, realMouse.leftButton.isPressed);

        if (realMouse.leftButton.wasPressedThisFrame)
            LogRaycastAtCursor();
    }
}
```

- [ ] **Step 3: Add `UpdateCursorAnglesFromController()` method**

Add after `UpdateCursorAngles()` (after line 412):

```csharp
private void UpdateCursorAnglesFromController()
{
    var camera = UiCamera;
    if (camera == null) return;

    EnsureCursorCanvas(camera);
    if (_cursor == null || _cursorRectTransform == null)
        return;

    if (!_cursor.activeSelf)
        _cursor.SetActive(true);

    Ray probeRay = new Ray(_controllerOrigin, _controllerDirection);
    _lastProbeRay = probeRay;

    if (VrCanvasHitTester.RaycastCanvasPlanes(probeRay, out var hit))
    {
        _activeCanvas = hit.Canvas;
        _hasActiveCanvas = true;
        _lastCanvasName = hit.Canvas.name;
        _lastCursorTargetPos = hit.WorldPoint;
        VrCanvasHitTester.LastActiveCanvas = hit.Canvas;

        _cursor.transform.position = hit.WorldPoint;
        var rt = hit.Canvas.GetComponent<RectTransform>();
        _cursor.transform.rotation = Quaternion.LookRotation(rt.forward, rt.up);
    }
    else
    {
        _hasActiveCanvas = false;
        _activeCanvas = null;
        VrCanvasHitTester.LastActiveCanvas = null;
        _lastCanvasName = "(none)";
        _cursor.SetActive(false);
    }
}
```

- [ ] **Step 4: Modify `UpdateCursorAnimation()` signature and body**

Replace the existing `UpdateCursorAnimation(Mouse realMouse)` method (lines 464-503) with:

```csharp
private void UpdateCursorAnimation(bool wasPressed, bool isPressed)
{
    if (_cursor == null || _cursorImage == null) return;

    if (wasPressed)
        _lastCursorClickTime = Time.unscaledTime;

    var idlePulse = Mathf.Sin(Time.unscaledTime * CursorIdlePulseSpeed) * CursorIdlePulseScale;
    var clickProgress = Mathf.Clamp01((Time.unscaledTime - _lastCursorClickTime) / CursorClickPulseDuration);
    var clickPulse = clickProgress < 1f
        ? Mathf.Sin((1f - clickProgress) * Mathf.PI) * CursorClickPulseScale
        : 0f;

    var targetVisualScale = 1f + idlePulse + clickPulse;
    if (_cursorOverInteractive)
        targetVisualScale *= CursorHoverScale;
    if (isPressed)
        targetVisualScale *= CursorPressedScale;

    var targetScale = Vector3.one * (CursorCanvasScale * targetVisualScale);
    _cursor.transform.localScale = Vector3.Lerp(_cursor.transform.localScale, targetScale, Time.unscaledDeltaTime * CursorAnimationLerpSpeed);

    var targetColor = CursorNormalColor;
    if (_cursorOverInteractive)
        targetColor = CursorHoverColor;
    if (isPressed)
        targetColor = CursorPressedColor;

    _cursorImage.color = Color.Lerp(_cursorImage.color, targetColor, Time.unscaledDeltaTime * CursorAnimationLerpSpeed);
}
```

Also update the call sites within `Update()` — replace `UpdateCursorAnimation(realMouse)` with `UpdateCursorAnimation(realMouse.leftButton.wasPressedThisFrame, realMouse.leftButton.isPressed)` in the mouse path.

Wait — this was already done in Step 2's code. The mouse path in Step 2's `Update()` already calls `UpdateCursorAnimation(realMouse.leftButton.wasPressedThisFrame, realMouse.leftButton.isPressed)` and the controller path calls `UpdateCursorAnimation(triggerDownThisFrame, _triggerIsPressed)`.

- [ ] **Step 5: Update diagnostic logging**

In `LogRaycastAtCursor()`, add controller data after the "Input state" section (after line 656):

```csharp
if (_controllerModeActive)
{
    lines.Add("--- Controller Input ---");
    lines.Add($"ControllerOrigin: {_controllerOrigin:F3}");
    lines.Add($"ControllerDirection: {_controllerDirection:F3}");
    lines.Add($"TriggerPressed: {_triggerIsPressed}");
}
```

- [ ] **Step 6: Verify compilation**

Run: `dotnet build NOVR/NOVR.csproj`
Expected: Build succeeds.

- [ ] **Step 7: Commit**

```bash
git add NOVR/VrUi/VrUiCursor.cs NOVR/ModConfiguration.cs
git commit -m "feat: add controller input mode to VrUiCursor with trigger-based click"
```

---

### Task 4: Create VrControllerLaser — visual laser line from controller to canvas

**Files:**
- Create: `NOVR/VrUi/VrControllerLaser.cs`

**Interfaces:**
- Produces: Visual laser line from controller position to cursor position
- Consumes: `VrUiCursor.IsControllerModeActive`, `VrUiCursor.CursorPosition`, `VrControllerInput`

**Design:**
A simple `MonoBehaviour` that creates a `LineRenderer` to draw a thin line from the controller origin to the canvas hit point when controller mode is active. Uses a bright green/cyan color. Only visible when a canvas hit is active.

- [ ] **Step 1: Write `VrControllerLaser.cs`**

```csharp
using UnityEngine;

namespace NOVR.VrUi
{
    [DefaultExecutionOrder(-900)]
    public class VrControllerLaser : NOVRBehaviour
    {
        private LineRenderer? _lineRenderer;
        private static readonly Color LaserColor = new Color32(0, 255, 200, 180);
        private const float LaserWidth = 0.002f;
        private const float LaserMaxDistance = 50f;

        protected override void Awake()
        {
            base.Awake();
            CreateLaser();
        }

        private void CreateLaser()
        {
            var go = new GameObject("VrControllerLaser");
            go.transform.SetParent(transform, false);
            _lineRenderer = go.AddComponent<LineRenderer>();

            _lineRenderer.positionCount = 2;
            _lineRenderer.startWidth = LaserWidth;
            _lineRenderer.endWidth = LaserWidth * 0.5f;
            _lineRenderer.startColor = LaserColor;
            _lineRenderer.endColor = new Color(LaserColor.r / 255f, LaserColor.g / 255f, LaserColor.b / 255f, 0f);
            _lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            _lineRenderer.sortingOrder = short.MaxValue;
            _lineRenderer.enabled = false;
        }

        private void Update()
        {
            var cursor = VrUiCursor.I;
            if (cursor == null || !cursor.IsActive || !cursor.IsControllerModeActive)
            {
                if (_lineRenderer != null)
                    _lineRenderer.enabled = false;
                return;
            }

            var cam = APIBus.CockpitHudCamera;
            if (cam == null)
            {
                if (_lineRenderer != null)
                    _lineRenderer.enabled = false;
                return;
            }

            var controllerPos = cam.transform.position; // fallback
            var cursorPos = cursor.CursorPosition;

            // Read current frame controller origin for the line start
            if (VrControllerInput.TryGetDominantHand(out var handPos, out _, out _))
                controllerPos = handPos;

            float distance = Vector3.Distance(controllerPos, cursorPos);
            if (distance > LaserMaxDistance || distance < 0.01f)
            {
                _lineRenderer.enabled = false;
                return;
            }

            _lineRenderer.enabled = true;
            _lineRenderer.SetPosition(0, controllerPos);
            _lineRenderer.SetPosition(1, cursorPos);
        }
    }
}
```

- [ ] **Step 2: Verify compilation**

Run: `dotnet build NOVR/NOVR.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add NOVR/VrUi/VrControllerLaser.cs
git commit -m "feat: add VrControllerLaser for visual ray feedback from controller to canvas"
```

---

### Task 5: Wire VrControllerLaser into NOUIManager

**Files:**
- Modify: `NOVR/VrUi/NOUIManager.cs`

- [ ] **Step 1: Add VrControllerLaser creation in `Start()`**

Add after line 48 (after `Create<NativeVrUiRoot>(transform)`):

```csharp
Create<VrControllerLaser>(transform);
```

- [ ] **Step 2: Verify compilation**

Run: `dotnet build NOVR/NOVR.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add NOVR/VrUi/NOUIManager.cs
git commit -m "feat: wire VrControllerLaser into NOUIManager startup"
```

---

### Task 6: Test and verify

- [ ] **Step 1: Full build**

Run: `dotnet build NOVR/NOVR.csproj`
Expected: Clean build, no warnings.

- [ ] **Step 2: Integration smoke test**

Verify with a VR headset connected:
1. Launch the game with the mod
2. Verify cursor appears and tracks when no controller is present (mouse mode fallback)
3. Point a VR controller forward — verify the laser line appears and cursor follows
4. Pull trigger — verify UI buttons respond (hover highlight + click)
5. Release trigger — verify no inadvertent double-clicks
6. Switch config to `"Mouse"` mode — verify controller no longer controls cursor
7. Switch config to `"Controller"` mode — verify mouse no longer controls cursor

- [ ] **Step 3: Commit final**

```bash
git commit --allow-empty -m "feat: complete VR controller ray UI selection implementation"
```
