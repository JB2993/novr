# Deep Diff State
<!-- Append-only. Edit or prune manually whenever you like. -->

## Review Preferences
<!-- What you care about, what to deprioritize. Examples: -->
<!-- - Focus on security in admin routes -->
<!-- - Don't flag minor naming style issues -->

## Dismissed False Positives
<!-- Patterns to stop flagging. Format: {pattern} — dismissed {date} — reason: {why} -->

## Project Conventions
<!-- Discovered conventions. Format: {convention} — confidence: {high|medium} -->
- UI canvases are converted to `RenderMode.WorldSpace` with `worldCamera = APIBus.CockpitHudCamera` — confidence: high
- Canvas registration uses `VrCanvasHitTester.Register/Unregister` paired with Unity lifecycle methods (Awake/OnDestroy, OnEnable/OnDisable) — confidence: high
- No automated tests; testing is manual by launching the game — confidence: high
- Harmony patches live in `HarmonyPatches/` directory; behavior components live in `Components/` directory — confidence: high
- Cursor event dispatch bypasses InputSystem VirtualMouse, using direct GraphicRaycaster + ExecuteEvents instead — confidence: high

## Recurring Issues
<!-- Format: {pattern} — count: {N} — last seen: {date} — status: {open|resolved} -->
<!-- No prior reviews — initial analysis 2026-07-07 -->

## Learnings
<!-- Stable knowledge about this project. Things that help future reviews be more accurate. -->
- The mod uses NOVRBehaviour as a custom MonoBehaviour base class instead of standard MonoBehaviour — confidence: high
- APIBus is the central service locator for camera references (CockpitHudCamera, CockpitHudReference) — confidence: high
- All VR canvases are on layer 30 (VrUi) — confidence: high
- NOVRPoseDriver writes head pose in Update, LateUpdate, AND OnBeforeRender — timing-sensitive for any camera-dependent operation — confidence: high
- The DynamicMap uses custom coordinate-math for map interaction instead of EventSystem (raycastTarget=false by design) — confidence: high
- Old cursor code used VirtualMouse synthetic device with InputAction binding overrides; new code removed this in favor of direct EventSystem dispatch — confidence: high
