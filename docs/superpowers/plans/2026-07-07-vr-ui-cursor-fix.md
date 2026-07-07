# VR UI Cursor / Hit-Test Misalignment Fix

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix VR UI cursor/hit-test misalignment (GitHub issue #19 — "Mouse off position when in bindings menu") by eliminating the camera coordinate round-trip and implementing panel-relative mouse mapping.

**Architecture:** Two-phase structural refactor of `VrUiCursor.cs`. Phase 1 replaces the `WorldToViewportPoint → Screen → RaycastAll` round-trip with direct world-space ray-plane intersection against canvas surfaces, eliminating head-pose coupling (Term A) and pixel-space scale mismatch (Term C). Phase 2 replaces the angle-based mouse-to-direction mapping (`ProjectPitchAngle`/`ProjectYawAngle`) with direct mouse-to-canvas-rect mapping, eliminating FOV/aspect mismatch (Term B). Followed by a DynamicMapVrCursorPatch consistency fix. Canvas discovery uses explicit registration (no per-frame scan). Event dispatch cancellation depends on pose-timing symmetry (documented, not automated).

**Tech Stack:** Unity C#, InputSystem, UGUI (GraphicRaycaster, Canvas, RectTransform)

## Global Constraints

- All VR canvases use `RenderMode.WorldSpace` with `worldCamera = APIBus.CockpitHudCamera`
- Cursor visibility and hit-test must end up in the SAME coordinate space
- VirtualMouse position (for `InputSystemUIInputModule`) must produce consistent raycast results
- No `CanvasScaler` components in the codebase — scale is managed via `transform.localScale`
- Canvases are on layer 30 (`VrUi`)
- Camera is head-tracked via `NOVRPoseDriver` (XRNode.CenterEye) in **Update, LateUpdate, AND OnBeforeRender**

---

## File Structure

| File | Responsibility | Change |
|------|---------------|--------|
| `NOVR/VrUi/VrUiCursor.cs` | Cursor pipeline: position, rotation, screen point | Major: rewrite `UpdateCursorAngles`, `GetScreenPoint`, remove `GetDistanceUnderCursor`, add sticky-canvas tracking, add frustum-guard for VirtualMouse |
| `NOVR/VrUi/DynamicMapVrCursorPatch.cs` | Map interaction patches | Minor: VrCanvasHitTester early-exit in SelectFromMap |
| (new) `NOVR/VrUi/VrCanvasHitTester.cs` | World-space ray-plane intersection against registered VR canvases | Create with registration API |
| (new) `NOVR/VrUi/IVrCanvasProvider.cs` | Interface for canvases to self-register | Create (optional — inline in VrCanvasHitTester if simpler) |

---

## Pose-Ordering Invariant

`NOVRPoseDriver` writes the head-camera pose in **three** lifecycle events per frame: `Update`, `LateUpdate`, and `OnBeforeRender`. The VirtualMouse round-trip (cursor world → `WorldToScreenPoint` → VirtualMouse → `InputSystemUIInputModule` → `GraphicRaycaster.ScreenPointToRay`) cancels only when both the projection AND unprojection use the same camera pose.

**Rule:** Feed the VirtualMouse position from `LateUpdate` or later — after the final pose write in the frame cycle. `VrUiCursor.UpdateCursorAngles` already runs in `Update()` (current code line 160-175). This is fine for cursor *positioning* (Phase 1 uses world canvas intersection, independent of camera pose), but the *VirtualMouse feed* must defer.

**Implementation:** Move the VirtualMouse state write (`InputState.Change(_virtualMouse, ...)`) into `LateUpdate()`. The cursor world-position computation stays in `Update()` for responsiveness. The VirtualMouse write copies the already-computed screen position in LateUpdate, after `NOVRPoseDriver` has written the final frame pose. This guarantees `WorldToScreenPoint` (computed from cursor world pos) and `GraphicRaycaster.ScreenPointToRay` (computed from VirtualMouse screen pos) use the same camera transform.

**PR documentation note:** Term A is eliminated for cursor *positioning* (world-space canvas intersection, no camera dependency). For event *dispatch*, Term A is cancelled-by-symmetry (same camera pose for projection and unprojection), not eliminated — the camera round-trip is still present in the EventSystem path. Document this as a deliberate trade-off to avoid replacing the entire InputSystemUIInputModule pipeline.

---

### Task 1: Create VrCanvasHitTester — world-space canvas intersection with explicit registration

**Files:**
- Create: `NOVR/VrUi/VrCanvasHitTester.cs`

**Interfaces:**
- Produces: `VrCanvasHitTester.Register(Canvas c)` / `Unregister(Canvas c)` / `RaycastCanvases(Ray ray, out CanvasHit hit, bool acceptBackFace = false) → bool`

**Design:**
A static utility with an explicit registration API. Canvases register themselves in `OnEnable`/`OnDisable`. No per-frame `FindObjectsOfType` scan. The `RaycastCanvases` method intersects a ray against the RectTransform plane of each registered canvas, culls back-face hits (unless `acceptBackFace` is true), checks rect bounds, and returns the closest hit with fallthrough logic: if the closest canvas's `GraphicRaycaster` reports no graphic at the hit point, attempt the next-closest canvas. This prevents an empty canvas from occluding a canvas behind it.

`CanvasHit` struct fields:
- `Canvas Canvas` — the hit canvas component
- `Vector3 WorldPoint` — intersection point in world space
- `Vector2 LocalPoint` — intersection point in canvas RectTransform local space
- `float Distance` — distance from ray origin
- `bool HasGraphic` — whether the GraphicRaycaster reports a graphic at this point

- [ ] **Step 1: Write `VrCanvasHitTester.cs`**

```csharp
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System.Collections.Generic;

namespace NOVR.VrUi
{
    internal readonly struct CanvasHit
    {
        public readonly Canvas Canvas;
        public readonly Vector3 WorldPoint;
        public readonly Vector2 LocalPoint;
        public readonly float Distance;
        public readonly bool HasGraphic;

        public CanvasHit(Canvas canvas, Vector3 worldPoint, Vector2 localPoint, float distance, bool hasGraphic)
        {
            Canvas = canvas;
            WorldPoint = worldPoint;
            LocalPoint = localPoint;
            Distance = distance;
            HasGraphic = hasGraphic;
        }
    }

    internal static class VrCanvasHitTester
    {
        private static readonly List<Canvas> _registeredCanvases = new();
        private static readonly List<RaycastResult> _graphicResults = new();

        public static void Register(Canvas canvas)
        {
            if (canvas != null && !_registeredCanvases.Contains(canvas))
                _registeredCanvases.Add(canvas);
        }

        public static void Unregister(Canvas canvas)
        {
            _registeredCanvases.Remove(canvas);
        }

        /// <summary>
        /// Intersect ray against registered canvases. Returns closest valid hit.
        /// If the closest canvas has no graphic at the hit point, falls through to
        /// the next-closest canvas to avoid empty regions occluding interactive content.
        /// </summary>
        /// <param name="acceptBackFace">If true, accept hits from behind the canvas plane.</param>
        public static bool RaycastCanvases(Ray ray, out CanvasHit hit, bool acceptBackFace = false)
        {
            hit = default;
            // Process in distance order: collect all hits, sort, then check graphics
            List<(float distance, Canvas canvas, Vector3 worldPoint, Vector2 localPoint)> candidates =
                new(_registeredCanvases.Count);

            var uiCamera = APIBus.CockpitHudCamera;

            foreach (var canvas in _registeredCanvases)
            {
                if (canvas == null || !canvas.gameObject.activeInHierarchy) continue;
                if (canvas.worldCamera != uiCamera) continue;

                var rectTransform = canvas.GetComponent<RectTransform>();
                if (rectTransform == null) continue;

                Vector3 planeNormal = rectTransform.forward;
                Vector3 planePoint = rectTransform.position;

                float denominator = Vector3.Dot(planeNormal, ray.direction);
                if (Mathf.Abs(denominator) < 0.0001f) continue;

                // Back-face cull: denominator > 0 means ray and normal point same direction → entering from behind
                if (!acceptBackFace && denominator > 0f) continue;

                float t = Vector3.Dot(planeNormal, planePoint - ray.origin) / denominator;
                if (t < 0f) continue;

                Vector3 worldPoint = ray.GetPoint(t);
                Vector3 localPos = rectTransform.InverseTransformPoint(worldPoint);
                Vector2 localPoint = new Vector2(localPos.x, localPos.y);

                if (!rectTransform.rect.Contains(localPoint)) continue;

                candidates.Add((t, canvas, worldPoint, localPoint));
            }

            if (candidates.Count == 0) return false;

            // Sort by distance, closest first
            candidates.Sort((a, b) => a.distance.CompareTo(b.distance));

            // Walk candidates: accept the first one with a graphic under the hit point.
            // Fall through only when the closest canvas has no graphic.
            foreach (var (distance, canvas, worldPoint, localPoint) in candidates)
            {
                bool hasGraphic = HasGraphicAtPoint(canvas, localPoint);
                hit = new CanvasHit(canvas, worldPoint, localPoint, distance, hasGraphic);

                // Important: prefer the first canvas even without a graphic (stability),
                // but fall through if it's truly empty so deeper canvases are reachable.
                // If hasGraphic is false but this is the only candidate, still accept it.
                if (hasGraphic || candidates.Count == 1)
                    return true;

                // hasGraphic == false and there are more candidates — continue loop
            }

            // All canvases had no graphic; return the closest one anyway
            var last = candidates[0];
            hit = new CanvasHit(last.canvas, last.worldPoint, last.localPoint, last.distance, false);
            return true;
        }

        private static bool HasGraphicAtPoint(Canvas canvas, Vector2 localPoint)
        {
            var raycaster = canvas.GetComponent<GraphicRaycaster>();
            if (raycaster == null) return false;

            // Convert canvas-local point to screen point for GraphicRaycaster
            // Use a minimal pointer event to check if any graphic exists at this point
            var camera = canvas.worldCamera;
            if (camera == null) return false;

            Vector3 worldPoint = canvas.transform.TransformPoint(localPoint);
            Vector3 screenPoint = camera.WorldToScreenPoint(worldPoint);

            var pointerEventData = new PointerEventData(EventSystem.current)
            {
                position = new Vector2(screenPoint.x, screenPoint.y)
            };

            _graphicResults.Clear();
            raycaster.Raycast(pointerEventData, _graphicResults);
            return _graphicResults.Count > 0;
        }

        /// <summary>
        /// Clear all registered canvases. Used on scene transitions.
        /// </summary>
        public static void Clear()
        {
            _registeredCanvases.Clear();
        }
    }
}
```

- [ ] **Step 2: Verify it compiles**

Build the project (`dotnet build NOVR/NOVR.csproj` or Unity Editor assembly compilation). No errors.

- [ ] **Step 3: Commit**

```bash
git add NOVR/VrUi/VrCanvasHitTester.cs
git commit -m "feat: add VrCanvasHitTester with registration API and graphic fallthrough"
```

---

### Task 2: Wire registration into canvas-creating scripts

**Files:**
- Modify: `NOVR/VrUi/Native/NativeVrUiRoot.cs`
- Modify: `NOVR/VrUi/Components/UIRenderedCanvasBehavior.cs`
- Modify: `NOVR/VrUi/Components/NOVRBlackoutCanvasBehavior.cs`

**Design:**
Each component that creates or manages a VR canvas calls `VrCanvasHitTester.Register(canvas)` in its `OnEnable`/`Start`/`Awake` and `VrCanvasHitTester.Unregister(canvas)` in its `OnDisable`/`OnDestroy`.

- [ ] **Step 1: Add registration calls to all three files**

For each of the three files, add at the end of the initialization method (`Awake`, `OnEnable`, `EnsureRoot`):

```csharp
VrCanvasHitTester.Register(_canvas); // (or the local canvas variable)
```

And in `OnDisable` or `OnDestroy`:

```csharp
VrCanvasHitTester.Unregister(_canvas);
```

For `NativeVrUiRoot.cs`, register both the main `_canvas` and the `_recenterWidgetCanvas`.
For `UIRenderedCanvasBehavior.cs`, register in `Initialize()`.
For `NOVRBlackoutCanvasBehavior.cs`, register in `Awake()`.

- [ ] **Step 2: Build and verify**

Build. No errors.

- [ ] **Step 3: Commit**

```bash
git add NOVR/VrUi/Native/NativeVrUiRoot.cs NOVR/VrUi/Components/UIRenderedCanvasBehavior.cs NOVR/VrUi/Components/NOVRBlackoutCanvasBehavior.cs
git commit -m "feat: register VR canvases with VrCanvasHitTester on enable"
```

---

### Task 3: Replace hit-test path in VrUiCursor — world-space hit-test (fixes Terms A + C)

**Files:**
- Modify: `NOVR/VrUi/VrUiCursor.cs`

**Interfaces:**
- Consumes: `VrCanvasHitTester.RaycastCanvases(Ray, out CanvasHit) → bool`
- Produces: refactored `UpdateCursorAngles`, `GetScreenPoint` (with frustum guard), removed `GetDistanceUnderCursor`

**Changes:**

1. Add frustum-guard to `GetScreenPoint()` — return `null`/sentinel when cursor is behind camera.

2. Modify `UpdateCursorAngles`: replace the screen-space round-trip with world-space ray-plane intersection. Keep the angle-based direction (Phase 1 interim — Phase 2 replaces angles).

3. Modify VirtualMouse feed to happen in `LateUpdate` (not `Update`), after the final `NOVRPoseDriver` pose write.

4. Remove `GetDistanceUnderCursor`/`TryGetUiDistanceUnderCursor`.

5. Update `LogRaycastAtCursor`.

- [ ] **Step 1: Read full `VrUiCursor.cs` to understand all callers**

```bash
rg -n "GetDistanceUnderCursor|TryGetUiDistanceUnderCursor|GetScreenPoint|_virtualMouse|InputState.Change" NOVR/VrUi/VrUiCursor.cs
```

- [ ] **Step 2: Modify `GetScreenPoint()` to handle behind-camera / off-frustum**

Replace current `GetScreenPoint()` with a version that:
- Uses `WorldToScreenPoint` (not `WorldToViewportPoint * Screen`)
- Checks `screenPoint.z <= 0` → return `Vector2.zero` AND sets an `_isOffscreen` flag
- The VirtualMouse feed uses this flag to suppress position/delta updates for the frame, preventing phantom clicks

```csharp
// Returns screen position OR zero if the cursor is behind the camera / off frustum.
// Caller must check _isOffscreen before trusting the result.
public Vector2 GetScreenPoint()
{
    var camera = UiCamera;
    if (_cursor == null || camera == null)
    {
        _isOffscreen = true;
        return Vector2.zero;
    }

    Vector3 screenPoint = camera.WorldToScreenPoint(
        _cursor.transform.position, Camera.MonoOrStereoscopicEye.Mono);

    if (screenPoint.z <= 0f)
    {
        // Behind camera: suppress VirtualMouse to avoid mirrored-coordinate phantom clicks.
        _isOffscreen = true;
        return Vector2.zero;
    }

    _isOffscreen = false;
    return new Vector2(
        Mathf.Clamp(screenPoint.x, 0f, camera.pixelWidth),
        Mathf.Clamp(screenPoint.y, 0f, camera.pixelHeight));
}
```

Add a new field `private bool _isOffscreen;` to VrUiCursor.

- [ ] **Step 3: Add offscreen guard to VirtualMouse feed**

In the VirtualMouse update block (currently `Update()`, lines ~168-174), wrap the `InputState.Change` in `_isOffscreen` check:

```csharp
Vector2 screenPoint = GetScreenPoint();

if (!_isOffscreen)
{
    InputState.Change(_virtualMouse, new MouseState
    {
        position = screenPoint,
        delta = realMouse.delta.ReadValue(),
        scroll = realMouse.scroll.ReadValue(),
        buttons = buttons
    });
}
// When offscreen: don't update VirtualMouse → UI system sees the last valid position,
// no hover events fire, clicks don't go to phantom elements.
```

- [ ] **Step 4: Move VirtualMouse feed to `LateUpdate()`**

The cursor world-position computation stays in `Update()` for responsiveness. The VirtualMouse `InputState.Change` call moves to a new `LateUpdate()` method (or inline in the existing one). This places it after `NOVRPoseDriver.LateUpdate()` which writes the final frame head pose.

```csharp
// LateUpdate: feed VirtualMouse after NOVRPoseDriver has written the final
// frame head-pose, so the projection (WorldToScreenPoint) and unprojection
// (GraphicRaycaster.ScreenPointToRay) see the same camera transform.
private void LateUpdate()
{
    if (_virtualMouse == null) return;
    if (_realMouse == null) return;

    Vector2 screenPoint = GetScreenPoint();
    if (_isOffscreen) return;

    // Read real mouse buttons
    var mouse = _realMouse;
    var buttons = new MouseState { ... }; // same as current Update() logic

    InputState.Change(_virtualMouse, new MouseState
    {
        position = screenPoint,
        delta = mouse.delta.ReadValue(),
        scroll = mouse.scroll.ReadValue(),
        buttons = buttons
    });
}
```

In the existing `Update()`, remove the `InputState.Change` block. Read mouse position and compute cursor angles in `Update()`; feed VirtualMouse in `LateUpdate()`.

- [ ] **Step 5: Modify `UpdateCursorAngles()`**

Replace lines 210-212:
```csharp
Vector3 viewportSpace = camera.WorldToViewportPoint(camera.transform.position + worldDirection * DefaultProjectionDistance, Camera.MonoOrStereoscopicEye.Mono);
Vector2 inScreenSpace = new Vector2(viewportSpace.x * Screen.width, viewportSpace.y * Screen.height);
float cursorDistance = GetDistanceUnderCursor(inScreenSpace);
```

With:
```csharp
Ray cursorRay = new Ray(camera.transform.position, worldDirection);
float cursorDistance;

if (VrCanvasHitTester.RaycastCanvases(cursorRay, out var canvasHit))
{
    cursorDistance = canvasHit.Distance;
}
else
{
    cursorDistance = DefaultProjectionDistance;
}
```

- [ ] **Step 6: Remove `GetDistanceUnderCursor` and `TryGetUiDistanceUnderCursor` methods**

Remove the `GetDistanceUnderCursor` method and `TryGetUiDistanceUnderCursor` entirely.
Remove the `PointerEventData` allocation and `EventSystem.RaycastAll` usage.

- [ ] **Step 7: Update `LogRaycastAtCursor`**

Replace screen-space `RaycastAll` with:
```csharp
var camera = UiCamera;
if (camera == null) return;

Ray ray = new Ray(camera.transform.position,
    (_cursor.transform.position - camera.transform.position).normalized);

if (VrCanvasHitTester.RaycastCanvases(ray, out var hit))
{
    Debug.Log($"[VrUiCursor] Canvas hit: {hit.Canvas.name} at world={hit.WorldPoint} local={hit.LocalPoint} dist={hit.Distance} hasGraphic={hit.HasGraphic}");
}
else
{
    Debug.Log("[VrUiCursor] No canvas hit.");
}
```

- [ ] **Step 8: Remove unused imports and fields**

Remove `using UnityEngine.EventSystems;` if `EventSystem` and `PointerEventData` were only used in the deleted methods.

- [ ] **Step 9: Build and verify**

Build the project. Visual test: cursor tracks mouse movement. Head movement no longer shifts the cursor's hit-target (symptom 3 partially improved). Note: at this intermediate state the cursor ray still originates from the live head position with the old angle mapping — symptom 3 is only partially fixed until Phase 2 provides the stable probe ray origin. Do NOT declare "symptom 3 fixed" after this phase alone.

- [ ] **Step 10: Commit**

```bash
git add NOVR/VrUi/VrUiCursor.cs
git commit -m "feat: replace screen-space hit-test with world-space canvas intersection; defer VirtualMouse to LateUpdate; add off-frustum guard"
```

---

### Task 4: Panel-relative mouse mapping — delete angle intermediary (fixes Term B)

**Files:**
- Modify: `NOVR/VrUi/VrUiCursor.cs`

**Interfaces:**
- Consumes: `VrCanvasHitTester`, canvas RectTransform from the hit canvas, `GetProjectionReferenceRotation()`
- Produces: cursor position on canvas surface from mouse normalized coords, with sticky canvas selection and anchor-positioned probe

**Design:**
Replace `ProjectPitchAngle`/`ProjectYawAngle` with direct mouse-to-canvas-rect mapping. The mouse maps to a unit-normalized `[0,1]²` square across the companion window. The active VR canvas defines a rectangular surface in world space. The cursor is placed directly at the mapped world point on the canvas surface. No angles, no FOV, no camera dependency.

**Canvas selection — sticky + anchor-positioned probe:**
- Store `_activeCanvas` reference, updated only when `_activeCanvas` is null or the probe ray misses it.
- The probe ray originates from the **anchor/reference position** (the same body-anchored frame that supplies the reference rotation), NOT from the live head position. This prevents head translation from shifting which canvas is selected.
- Probe ray direction: `GetProjectionReferenceRotation() * Vector3.forward`.

**Aspect ratio UX decision:**
- This plan maps the full companion window `[0,1]²` onto the full canvas rect. This means horizontal and vertical mouse sensitivity differ whenever the window and canvas aspects differ. This is the simplest implementation and the one that matches the "mouse fills the panel" expectation.
- The alternative (uniform sensitivity with letterboxed mapping to the canvas aspect) feels better for fine clicking but is a separate UX tuning — implement only if testing shows the uniform mapping causes usability issues in the bindings menu.

- [ ] **Step 1: Remove `ProjectPitchAngle`, `ProjectYawAngle`, `MaxPitchDegrees`, `MaxYawDegrees`**

Delete lines 46-47 (`MaxYawDegrees`, `MaxPitchDegrees`).
Delete lines 372-383 (`ProjectPitchAngle`, `ProjectYawAngle`).
Delete `ScreenWidth`/`ScreenHeight` properties (lines 77-78) if they have no remaining callers.

- [ ] **Step 2: Keep `DefaultProjectionDistance` as fallback**

`DefaultProjectionDistance` (5m) is still used as the fallback when no canvas is hit.

- [ ] **Step 3: Add sticky-canvas fields**

Add to VrUiCursor class:
```csharp
private Canvas _activeCanvas;
private bool _hasActiveCanvas;
```

- [ ] **Step 4: Rewrite `UpdateCursorAngles()` with panel-relative mapping and sticky canvas**

```csharp
private void UpdateCursorAngles()
{
    var camera = UiCamera;
    if (camera == null) return;

    EnsureCursorCanvas(camera);

    if (_cursor == null || _cursorRectTransform == null)
        return;

    if (!_cursor.activeSelf)
        _cursor.SetActive(true);

    var mouse = _realMouse;
    if (mouse == null) return;

    var mousePos = mouse.position.ReadValue();

    // Probe ray: originate from the anchor/reference position (body-anchored),
    // NOT from the live head position, to prevent head translation from
    // flipping which canvas is selected.
    Transform anchor = GetAnchorTransform();
    Vector3 probeOrigin = anchor != null ? anchor.position : camera.transform.position;
    Quaternion referenceRotation = GetProjectionReferenceRotation();
    Vector3 probeDirection = referenceRotation * Vector3.forward;
    Ray probeRay = new Ray(probeOrigin, probeDirection);

    // Sticky canvas: keep _activeCanvas while it stays valid; re-probe only
    // when no canvas is currently active or the probe misses the active canvas.
    Canvas targetCanvas = null;

    if (_hasActiveCanvas && _activeCanvas != null && _activeCanvas.gameObject.activeInHierarchy)
    {
        // Quick check: is the active canvas still in front of the probe?
        // Use the more expensive plane intersection only if the sticky canvas
        // is no longer valid.
        targetCanvas = _activeCanvas;
    }

    if (targetCanvas == null)
    {
        // Probe for a new canvas
        if (VrCanvasHitTester.RaycastCanvases(probeRay, out var probeHit))
        {
            targetCanvas = probeHit.Canvas;
            _activeCanvas = targetCanvas;
            _hasActiveCanvas = true;
        }
        else
        {
            _hasActiveCanvas = false;
        }
    }

    if (_hasActiveCanvas && targetCanvas != null)
    {
        var rectTransform = targetCanvas.GetComponent<RectTransform>();
        Vector2 canvasSize = rectTransform.rect.size;
        Vector2 pivot = rectTransform.pivot;

        // Mouse normalized [0,1] across the companion window.
        // Full-screen mapping: the entire window maps to the full canvas rect.
        // This means H and V sensitivity differ when window and canvas aspects differ.
        float nx = Mathf.Clamp01(mousePos.x / Screen.width);
        float ny = Mathf.Clamp01(mousePos.y / Screen.height);

        // Map to canvas local position accounting for pivot
        float localX = Mathf.Lerp(-canvasSize.x * pivot.x, canvasSize.x * (1f - pivot.x), nx);
        float localY = Mathf.Lerp(-canvasSize.y * pivot.y, canvasSize.y * (1f - pivot.y), ny);

        Vector3 localPoint = new Vector3(localX, localY, 0f);
        Vector3 worldPos = rectTransform.TransformPoint(localPoint);

        _cursor.transform.position = worldPos;
        _cursor.transform.rotation = Quaternion.LookRotation(rectTransform.forward, rectTransform.up);
    }
    else
    {
        // No canvas hit — fallback to default projection along anchor direction
        Vector3 fallbackDirection = referenceRotation * Vector3.forward;
        Vector3 fallbackOrigin = anchor != null ? anchor.position : camera.transform.position;
        Vector3 pos = fallbackOrigin + fallbackDirection * DefaultProjectionDistance;
        _cursor.transform.position = pos;
        _cursor.transform.rotation = Quaternion.LookRotation(fallbackDirection, camera.transform.up);
    }
}
```

Add helper:
```csharp
// Returns the transform that defines the body-anchored reference frame.
// Used as the probe ray origin for canvas selection.
// Falls back to the camera if no override is set.
private Transform GetAnchorTransform()
{
    if (_hasProjectionReferenceOverride)
    {
        // When the override is set (by NativeVrUiRoot), the reference rotation
        // is body-anchored. We need the corresponding position.
        // The anchor position is stored by NativeVrUiRoot; we access it via
        // the CockpitHudReference transform.
        return APIBus.CockpitHudReference?.transform;
    }
    return null; // caller falls back to camera.transform
}
```

- [ ] **Step 5: Remove unused properties**

Verify `ScreenWidth` and `ScreenHeight` have no remaining callers. Delete them.
The `_hasProjectionReferenceOverride`/`_projectionReferenceRotation` fields and their setters stay — still used by `GetProjectionReferenceRotation` for the anchor rotation.

- [ ] **Step 6: Build and verify**

Build the project. Verify:
- **Head still, mouse sweep**: cursor tracks across the canvas surface linearly. No radial divergence. Cursor and click position coincide across the full panel in both axes.
- **Head moves, mouse still**: cursor stays fixed relative to the canvas surface (no head-pose drift).
- **Head rotates while clicking**: hold click and rotate head. Click target stays under cursor (no intra-frame pose skew) — this tests the LateUpdate VirtualMouse feed.
- **Head translates (lean) while interacting**: canvas selection doesn't flip to a different canvas mid-interaction (sticky canvas + anchor-positioned probe).
- **Turn head away from panel**: cursor disappears (offscreen), no phantom clicks on unrelated UI.

- [ ] **Step 7: Commit**

```bash
git add NOVR/VrUi/VrUiCursor.cs
git commit -m "feat: panel-relative mouse mapping with sticky canvas and anchor-positioned probe (fixes Term B)"
```

---

### Task 5: Fix DynamicMapVrCursorPatch — intentional early-exit with VrCanvasHitTester

**Files:**
- Modify: `NOVR/VrUi/DynamicMapVrCursorPatch.cs`

**Design:**
The original inconsistency (some patches using `WorldToScreenPoint`, others using `GetScreenPoint()`) was resolved in Phase 1 when `GetScreenPoint()` was switched to `WorldToScreenPoint`. The remaining concern is head-pose coupling in `SelectFromMapPatch`. We add a VrCanvasHitTester gate: if no canvas is hit by the cursor ray, skip the EventSystem raycast entirely (no canvas → no map icon can be hit).

**Decision on `return false` vs `return true`:** Since this patch runs exclusively in VR mode (entire `DynamicMapVrCursorPatch` is VR-only), suppressing the original `SelectFromMap` when no canvas is hit is correct — the flat-screen logic would produce wrong results in VR anyway. Document this with a comment.

- [ ] **Step 1: Add VrCanvasHitTester early-exit gate**

In `SelectFromMapPatch.Prefix`, after the cursor/camera null checks, add:

```csharp
// VR-only: if no canvas is under the cursor ray, no map icon can be selected.
// Return false to suppress the flat-screen SelectFromMap logic (which would
// produce incorrect results in VR).
Ray cursorRay = new Ray(camera.transform.position,
    (cursor.CursorPosition - camera.transform.position).normalized);

if (!VrCanvasHitTester.RaycastCanvases(cursorRay, out _))
{
    return false; // intentionally suppress original method
}
```

Place this immediately after the existing `if (camera == null) return true;` on line 52.

- [ ] **Step 2: Build and verify**

Build the project. Verify map icon clicks still work correctly: click an icon on the map → it selects. Click empty space → nothing happens.

- [ ] **Step 3: Commit**

```bash
git add NOVR/VrUi/DynamicMapVrCursorPatch.cs
git commit -m "fix: add VrCanvasHitTester early-exit gate in SelectFromMapPatch"
```

---

## Verification

After all tasks, verify the following invariants:

1. **Head still, mouse sweep**: cursor and hit point coincide across the full panel in both axes. No radial or anisotropic divergence.
2. **Head sweep, mouse still**: hit point stays fixed on the canvas. Cursor stays on the canvas surface.
3. **Head rotates while clicking**: hold a button down and rotate head continuously. The click target does not change under the cursor (no intra-frame pose skew). This is the critical test for the LateUpdate VirtualMouse feed.
4. **Head translates (lean) while interacting**: the active canvas does not change mid-interaction (sticky canvas selection).
5. **Turn head away from panel (panel-relative cursor)**: the cursor disappears from the companion window. No phantom clicks on unrelated UI (offscreen guard).
6. **Map interaction**: clicking map icons via VR cursor works correctly. Empty-space clicks are suppressed.
7. **VirtualMouse events**: hover, click, drag all work through the InputSystemUIInputModule.
8. **Regressions**: native UI panels (main menu, settings, multiplayer) and stock game canvases (bindings menu) all interact correctly across the full panel area.

## Rollback

Each phase produces independent, tested commits. Rollback by commit hash (not relative refs, which shift with rebase):

```bash
# To undo Phase 2 (panel-relative mapping — Task 4):
git log --oneline -5
git revert <commit-hash-for-task-4>

# To undo Phase 1 (world-space hit-test — Task 3):
git revert <commit-hash-for-task-3>

# To undo everything:
git revert <commit-hash-for-task-1>^..HEAD
```

Independent commits per task make partial rollback safe.
