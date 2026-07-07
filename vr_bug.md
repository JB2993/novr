# NOVR Issue #19 — VR UI Cursor / Hit-Test Misalignment: Fix Brief

**Target:** `InfernoSuperNova/novr` — GitHub issue #19 ("Mouse off position when in bindings menu")
**Goal:** Produce a PR that fixes VR UI cursor/hit-test misalignment. **Investigate and confirm before coding.** The sections below distinguish what is confirmed from what still needs measurement.

---

## Symptom (confirmed from user testing + issue report)

1. **Center-anchored radial divergence:** cursor and click-target align at panel center, diverge as the cursor moves away from center in any direction.
2. **Anisotropic:** divergence rate differs horizontally vs. vertically (confirmed by user).
3. **Head-pose coupling** (issue #19 reporter): the activation highlight tracks headset position — moving the head with the mouse held still moves the hit-target.

These are **three potentially separate defects.** Do not collapse them into one root cause. Symptoms (1)+(2) are center-anchored; symptom (3) is head-driven. A uniform pixel-scale mismatch is corner-anchored (origin = screen bottom-left), so it **cannot** be the primary driver of a center-anchored symptom.

---

## Architecture (confirmed by prior code investigation)

- Affected canvases are converted to `RenderMode.WorldSpace`
  (`UIRenderedCanvasBehavior.cs:29`, `NOVRBlackoutCanvasBehavior.cs:15`, `NativeVrUiRoot.cs:209,415`).
- Every canvas's `worldCamera` = `APIBus.CockpitHudCamera` → `NOUIManager.I.CockpitHudCamera`.
- That camera carries a `NOVRPoseDriver` that sets `localRotation`/`localPosition` from `NOVRHeadsetData` (XRNode.CenterEye) **every frame** (`NOVRPoseDriver.cs:48-49`). It is the head-tracked VR camera.
- **No `CanvasScaler`** anywhere in the codebase.
- Input path: physical mouse → synthetic `VirtualMouse` device → `InputSystemUIInputModule` (restricted to VirtualMouse) for event dispatch, **plus** a bespoke `EventSystem.RaycastAll()` for cursor depth (`VrUiCursor.cs:290`). No gaze raycast, no competing input path.
- Cursor pipeline (`VrUiCursor.cs`): mouse XY → pitch/yaw angles (`ProjectPitchAngle`/`ProjectYawAngle`, normalized by `Screen.width`/`Screen.height`) → local direction → `GetProjectionReferenceRotation()` → projected via `camera.WorldToViewportPoint(...)` at 5m → `vp * Screen.width/height` → `RaycastAll`.
- UI camera: `targetTexture = null`, `stereoTargetEye = Both`, URP `Overlay` in the main camera stack. Renders directly into VR eye textures (not a RenderTexture).

---

## Diagnosis of each term

### Term A — Head-pose coupling (confirmed cause of symptom 3)
WorldSpace canvas + head-driven `worldCamera` means the `GraphicRaycaster` shoots its ray from a head-tracked pose. As the head rotates, the ray sweeps across the body-anchored canvas while the visible cursor (computed at the 5m head-relative point) moves at a different rate → the two desync with head motion. **Real, and structurally what the reporter observed.**

### Term B — FOV / aspect mismatch (most likely cause of symptoms 1 + 2)
`ProjectYawAngle` normalizes by `Screen.width`, `ProjectPitchAngle` by `Screen.height` → the implied angular mapping inherits the **companion-window aspect ratio** (~16:9). But `WorldToViewportPoint` projects through the VR camera's **per-eye projection**, which is near-square (~1.30:1 on Quest) with an **asymmetric** frustum. Two different aspect ratios → different H vs. V scale factors → center-anchored, radial, **anisotropic** divergence. This matches symptoms 1+2 exactly, including the confirmed H≠V rate.

### Term C — `Screen.width/height` vs `camera.pixelWidth/Height` pixel-scale mismatch (agent's earlier "root cause" — DEMOTE)
`GetScreenPoint` does `vp * Screen.width`; the raycaster recovers `vp * (Screen.width / camera.pixelWidth)`. This is a scale about the **screen origin (bottom-left corner)**, so it predicts a **corner-anchored** error, not center-anchored. It is likely a real but secondary term, and **may be zero** in this build if `Screen.width == camera.pixelWidth` for this stereo overlay camera. **This assumption was never measured.** Do not headline this fix.

---

## Measure before coding (cheap, decisive)

Add temporary logging and confirm each term before changing logic:

1. Log side-by-side at runtime: `Screen.width/height`, `camera.pixelWidth/pixelHeight`, `XRSettings.eyeTextureWidth/Height`.
   - If `Screen.width == camera.pixelWidth`, **Term C is a non-issue** — do not fix it as the headline.
2. Head still, sweep mouse across panel. Log: computed viewport point, raycast-recovered viewport, visible cursor render position.
   - Confirm the residual error is **center-anchored** (→ Term B) vs corner-anchored (→ Term C).
   - Confirm H rate ≠ V rate (already reported; verify in logs).
3. Compare the FOV that `ProjectPitchAngle`/`ProjectYawAngle` implicitly assume against the camera's actual projection (`GetStereoProjectionMatrix`, see note below).
4. Head sweep, mouse still. Confirm hit point tracks head pose (→ Term A).

---

## Recommended fix structure

Prefer eliminating whole coordinate round-trips over patching individual scale factors. A `Screen.width → camera.pixelWidth` substitution only touches Term C and leaves A and B intact — **not sufficient.**

### 1. Do the hit-test in world space (fixes Terms A + C together)
The pipeline already computes a valid **world-space cursor position** and then discards it by projecting to companion-window pixels and back through a head-tracked camera. Instead: intersect the cursor's world position against the body-anchored canvas plane and transform into the canvas `RectTransform`'s local space to hit-test directly — no camera round-trip. This removes `WorldToViewportPoint` → screen pixels → `ScreenPointToRay` from the loop entirely, killing the head-pose coupling and the pixel-space term in one change.
- Watch the canvas **lossy scale** and **pivot** handling — this is the easy-to-botch part.

### 2. Resolve the design question BEFORE fixing Term B
Determine the intended UX of the mouse mapping:
- **If panel-relative** ("mouse maps to a fixed region of a body-anchored panel" — consistent with the 5m projection distance and with what the issue reporter expects): the mapping should **not reference camera FOV at all.** Map mouse pixels directly to canvas-local coordinates on the panel and **delete the angle intermediary** (`ProjectPitchAngle`/`ProjectYawAngle`) along with the camera round-trip. Simplest and most robust; makes Term B disappear rather than correcting it.
- **If head-relative** (cursor meant to move with gaze): keep the angle mapping but derive per-axis half-angles from the camera's **actual projection matrix** instead of `Screen.width/height`, so angular sweep per pixel matches the eye per axis.

The 5m projection distance + panel-anchored expectation strongly suggest **panel-relative** is intended. Confirm, then prefer deleting the angle path.

### Note on stereo projection (if Term B fix references the matrix)
With `stereoTargetEye = Both`, `camera.projectionMatrix` may return the mono fallback, not a per-eye matrix. Use `camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left)`. The cursor is a mono construct, so one eye's symmetric-ized FOV (or the average of both eyes) is acceptable.

---

## Invariant to preserve

**The visible cursor and the hit-test must end up in the same coordinate space.** They are currently computed via different paths, which is why the desync is confusing to debug. Any fix that corrects one path without the other will merely relocate the misalignment. Verify at the end that:
- Head still + mouse sweep → cursor and hit point coincide across the **full** panel in **both** axes.
- Head sweep + mouse still → hit point stays fixed on the canvas.

---

## Files referenced

- `NOVR/VrUi/VrUiCursor.cs` — cursor pipeline, `GetScreenPoint` (88-99), `UpdateCursorAngles`, `ProjectPitchAngle`/`ProjectYawAngle` (203-211), `TryGetUiDistanceUnderCursor` (290), VirtualMouse (142, 168-174), `RestrictUIModuleToVirtualMouse` (459)
- `NOVR/VrUi/Native/NativeVrUiRoot.cs` — GraphicRaycaster (213, 420), WorldSpace conversion (209, 415)
- `NOVR/.../NOUIManager.cs` — `CreateUiCamera` (74-95), camera config (84-92)
- `NOVR/.../NOVRPoseDriver.cs` — head-pose application (48-49)
- `NOVR/.../NOVRHeadsetData.cs` — CenterEye source (131-132)
- `NOVR/VrUi/Components/UIRenderedCanvasBehavior.cs` (29), `NOVRBlackoutCanvasBehavior.cs` (15)
- `NOVR/.../DynamicMapVrCursorPatch.cs` — map hit-testing (55, 66, 88, 152, 183)
- `APIBus.cs` (12)

*Line numbers from prior investigation; verify against current HEAD.*