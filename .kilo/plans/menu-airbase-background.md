# Menu Airbase Background — Implementation Plan

## Goal

Replace the flat black/sprite backgrounds in Nuclear Option's menu screens (level0 main menu, level1 airbase/loadout) with a real 3D scene of the user standing on a tarmac next to a parked aircraft. Use the game's own assets (terrain, hangars, planes) loaded at runtime — no bundled meshes required.

## Why This Is Feasible

Verified by enumerating `D:\SteamLibrary\steamapps\common\Nuclear Option\NuclearOption_Data`:

- **Game engine**: Unity 2022.3.6f1, BepInEx 5.x — exactly the same stack proven to work for asset loading by `NOLiveryPlus` (`AssetBundle.LoadFromFile` pattern) and `NOBlueprinter` (whole aircraft prefab loading).
- **The complete airbase already exists in the game**: `level3` (the encyclopedia/aircraft browser scene) contains the full `terrain_encyclopedia` subtree — `island` (ground), `roads` (runway/taxiway), `decals` (markings), `hangar_med`, `controlTower1`, `shelter1` (x3), `munitionsContainer` (x4), `missileCart` (x4), `HLT-M`, `container`, plus environment (`moon`, `starfield`, `cloudPlane`, `BackdropWater`).
- **Aircraft prefabs live in `resources.assets`** — `Darkreach` confirmed via direct lookup; same pattern works for CI-22 Cricket, T/A-30 Compass, FS-12 Revoker, etc.
- **Existing scene already proves the pattern**: the encyclopedia instantiates an aircraft onto `terrain_encyclopedia.island` and renders it with `cockpitRenderer` + `selectionCam` + a directional `sun` light. We're not inventing anything — we're copying what level3 already does.

## Scene Breakdown (from `level0..level4`)

| Level | Role | What NOVR sees |
|---|---|---|
| level0 | Main menu | `MainCanvas`, `MenuButtonsPanel`, `Menu Camera` (no 3D) |
| level1 | Airbase select / loadout | `BlackoutCanvas` (recent button backdrop), `MainCanvas`, `MenuCanvas`, `AirbaseOverlay`, `selectAirbasePanel`, `SceneEssentials` |
| level2 | Mission editor | `MainCanvas`, `CameraPositionOverrideField` |
| level3 | **Aircraft encyclopedia / preview** | Real 3D scene: `terrain_encyclopedia` → `island`+`roads`+`decals`+`spawner`, buildings (`hangar_med`, `controlTower1`, `shelter1`), `Main Camera`, `cockpitRenderer`, `selectionCam`, `sun`, `moon`, `starfield` |
| level4 | Mission editor variant | UI only |

`BlackoutCanvas` lives in `level1`, not the main menu — confirmed user feedback. Do not disable it (it's the recenter button backdrop).

## Architecture

### Strategy

Load game assetbundles at plugin startup, cache the airbase prefabs, then on entering level0 or level1 instantiate the airbase scene parented to a world-anchored GameObject. Spawn a parked aircraft at a known spawnpoint. Reposition the user so they're standing on the tarmac looking at the plane. Hide the existing flat background sprite (`MainMenuBackground309` per `NuclearMenuBackground` mod).

### Where in NOVR

| Component | File | Role |
|---|---|---|
| Asset loader | `NOVR/MenuAirbase/MenuAirbaseLoader.cs` (new) | Load `sharedassets1.assets` and `sharedassets3.assets` once at plugin startup; cache prefab references |
| Scene spawner | `NOVR/MenuAirbase/MenuAirbaseController.cs` (new) | Per-scene MonoBehaviour: instantiates terrain, spawns plane, positions camera, hides background sprite, fades transitions |
| Scene detection | Hook into `UIBehaviorPatcher.cs:26-33` | Add menu airbase to the scene-name patch map |
| User positioning | `NOVRHeadsetData.cs` + new | Anchor player translation to a "standing-on-tarmac" position when airbase is active |
| Configuration | `ModConfiguration.cs` | Add `EnableMenuAirbase` toggle and `MenuAirbaseAircraft` choice |

### Asset Loading Pattern

Follow `NOLiveryPlus` (`Plugin.cs` lines 30-49) which already works for Nuclear Option:

```csharp
var bundle = AssetBundle.LoadFromFile(path);
foreach (GameObject prefab in bundle.LoadAllAssets<GameObject>())
    cache[prefab.name] = prefab;
```

**Files to load:**

| Path | Size | Contents |
|---|---|---|
| `NuclearOption_Data/sharedassets3.assets` | 3.8 MB | `terrain_encyclopedia`, `hangar_med`, `controlTower1`, `shelter1`, `munitionsContainer`, `missileCart`, `HLT-M`, `container` |
| `NuclearOption_Data/sharedassets1.assets` | 142.9 MB | Full map content including airbase, terrain tiles, islands |
| `NuclearOption_Data/resources.assets` | 130.8 MB | Aircraft prefabs (`Darkreach`, etc.), materials, textures, AudioSources |
| `NuclearOption_Data/StreamingAssets/aa/...` | (Addressables) | Runtime-loaded bundles — see "Addressables path" below |

**Caching strategy:** Keep bundles loaded for plugin lifetime. Cache references to `terrain_encyclopedia`, the desired aircraft prefab, hangar, tower, shelter. Don't unload — bundles are small relative to game RAM and the game itself keeps `resources.assets` resident.

### Spawn Logic

When level0 or level1 loads:

1. Disable `MainMenuBackground309` sprite (the existing flat background).
2. Instantiate `terrain_encyclopedia` at world origin, parented to a `MenuAirbaseRoot` GameObject.
3. Position the spawned aircraft prefab at `spawnpoint_runway` (relative to terrain_encyclopedia origin — coordinate from level3 if needed).
4. Set `Menu Camera` (or VR camera) position to a standing-spot on the tarmac, looking at the aircraft.
5. Apply `PositionZeroBehavior` so the airbase stays anchored during floating-origin shifts.

### Aircraft Choice

- Default: `Darkreach` (SFB-81 strategic bomber — visually iconic, lots of detail).
- Configurable via `ModConfiguration.MenuAirbaseAircraft` — let user pick their favorite.

### Lighting

The encyclopedia scene uses a directional `sun` + `moon` + `Reflection Probe`. Reuse the same lighting setup; position lights to produce a static "day on the tarmac" look. Skybox: `SkyboxCustom` material (already exists in `resources.assets`).

### State Gating

| State | Behavior |
|---|---|
| Plugin startup | Load bundles, cache prefabs |
| Scene loaded: level0 (main menu) | Spawn airbase; reposition user; hide `MainMenuBackground309` |
| Scene loaded: level1 (airbase select) | Spawn airbase; reposition user; **keep `BlackoutCanvas` intact** (it's the recenter button); hide any other flat background |
| Scene loaded: level2/level3/level4 | Out of scope for v1 (encyclopedia already has its own 3D scene) |
| Aircraft spawned in-game | Tear down airbase, restore defaults, defer to in-flight camera |
| Aircraft despawned / back to menu | Re-spawn airbase |

Detection signals (already in code):
- `GameManager.GetLocalAircraft(out _aircraft)` (`NOVR/Core.cs:89`) — `aircraft == null` ⇒ menu state
- `NOVRBlackoutCanvasBehavior` (`NOVR/VrUi/Components/NOVRBlackoutCanvasBehavior.cs`) — already poll-driven
- `Resources.FindObjectsOfTypeAll<GameObject>()` — locate `MainMenuBackground309` and hide

### Camera Positioning

- Place VR camera (or "Menu Camera" when not in VR) at a fixed offset from the airbase root, ~3-5 m from the aircraft's nose, eye height ~1.7 m.
- The aircraft should fill ~30-40% of the FOV — close enough to feel imposing, far enough to see the whole plane.
- Look-at target: aircraft's center of mass.
- In VR: the user gets free head-look. Standing position is fixed (not head-locked).

### Recentering

The recenter button (`BlackoutCanvas`) must keep working. It calls `NOVRHeadsetData.CalibrateTranslation()` (`NOVR/NOVRHeadsetData.cs:36-45`). Update this so when menu airbase is active, recenter sets the user's translation to the standing-on-tarmac position (not world origin).

### Performance

- `sharedassets1.assets` is 142MB but loaded once at startup. Unity will keep it resident.
- The encyclopedia airbase is a tiny subset of sharedassets1 — only the meshes used by `terrain_encyclopedia` get rendered.
- LOD: the game already provides LOD1/LOD2 on most buildings; reuse them.
- No per-frame `FindObjectsOfTypeAll` — cache references after scene-load.

## Implementation Phases

### Phase 1: Proof of concept (2-3 days)

1. Add `NOVR/MenuAirbase/MenuAirbaseLoader.cs` — bundle loading following NOLiveryPlus pattern.
2. Add `NOVR/MenuAirbase/MenuAirbaseController.cs` — barebones MonoBehaviour that on `Start` instantiates `terrain_encyclopedia` + `Darkreach` at world origin.
3. Wire into `UIBehaviorPatcher.cs` so it attaches to `MenuCanvas` (level0) and `MenuCanvas` (level1).
4. Build, deploy, run the game, verify the airbase renders without pink/missing materials.
5. **Gate**: does it look like the encyclopedia scene?

### Phase 2: Integration (3-5 days)

1. Hide `MainMenuBackground309` (existing flat sprite — see how `NuclearMenuBackground` mod does it).
2. Position VR/Menu camera at the standing spot, looking at the aircraft.
3. Hook into `Core.cs` aircraft-change detection to tear down/rebuild on menu↔flight transitions.
4. Add fade in/out for the airbase to hide pop-in.
5. Add `ModConfiguration` toggle (`EnableMenuAirbase`) so users can disable.
6. **Gate**: can the user navigate the menu in VR while standing on the tarmac? Can they launch a mission and the airbase tears down cleanly?

### Phase 3: Polish (2-3 days)

1. Aircraft choice config (default: Darkreach; options: all aircraft).
2. Multiple camera angles (cockpit-side, three-quarter front, behind tail).
3. Recentering: user recenters to standing spot, not world origin.
4. Performance tuning (LOD verification, draw call count).
5. Lighting tweaks — match game's daytime look.
6. **Gate**: does it feel like the user is actually on an airbase?

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Missing shaders / pink materials | Pre-load shaders from the same bundles; verify NOLiveryPlus + NOBlueprinter work means the shader pipeline is intact |
| Game updates break asset references | Asset bundle paths are stable; specific prefab names change — log miss-and-skip, fall back to "any plane" |
| Floating origin shifts break anchoring | Apply `PositionZeroBehavior` (already used in `FloatingOriginUiRootPatch.cs`) to the airbase root |
| Performance on low-end VR | Phase 3: add an option to disable airbase in `ModConfiguration` |
| BlackoutCanvas modified in game update | Don't hardcode — query by name each scene load |
| Addressables path is more reliable than raw AssetBundle | Investigate `NuclearOption_Data/StreamingAssets/aa/catalog.json` — if Addressables gives us a stable API, prefer it |
| Aircraft prefab depends on MapLoader/spawn infrastructure | Spawn the prefab as a static GameObject — no Unit spawner needed, just visuals |
| Lighting in menu is wrong (too dark, too bright) | Reuse encyclopedia scene's lighting setup (sun + reflection probe); test day/night |

## File Changes Summary

### New files

- `NOVR/MenuAirbase/MenuAirbaseLoader.cs` — bundle loading
- `NOVR/MenuAirbase/MenuAirbaseController.cs` — per-scene spawn/hide
- `NOVR/MenuAirbase/MenuAirbaseConfig.cs` — config bindings

### Modified files

- `NOVR/UIBehaviorPatcher.cs` — add airbase hook to scene-name patch map (line 26-33)
- `NOVR/ModConfiguration.cs` — add `EnableMenuAirbase` + `MenuAirbaseAircraft` config entries
- `NOVR/NOVRPlugin.cs` — instantiate `MenuAirbaseLoader` in `Awake`
- `NOVR/Core.cs` — wire menu airbase teardown when aircraft spawns (around line 86-92)
- `NOVR/NOVRHeadsetData.cs` — recenter target = standing position when airbase active
- `NOVR/NOVR.csproj` — add new `.cs` files to `<Compile>` (SDK-style project)

## Open Questions to Resolve During Implementation

1. **Asset loading**: raw `AssetBundle.LoadFromFile` (proven by NOLiveryPlus) vs Addressables (`StreamingAssets/aa/catalog.json`) — start with raw, revisit if Addressables gives better stability across game updates.
2. **Aircraft selection**: hardcode Darkreach for v1, or read available aircraft from `resources.assets` and expose all as config choices?
3. **Tarmac location**: which airbase? Encyclopedia uses a generic terrain — for menu we may want a specific named airbase for atmosphere.
4. **VR vs flat-screen**: VR is the primary target. Should flat-screen get the airbase too (via Menu Camera), or only VR?
5. **Loading screen**: should the airbase also replace the loading screen artwork (currently `LoadingScreenCricket`, `LoadingScreenRevoker2`, etc. — see `resources.assets`)?

## References

- **NOLiveryPlus** (`src/liveryplus/Plugin.cs`) — `AssetBundle.LoadFromFile` pattern, ~90 lines, MIT/GPL-3.0
- **NOBlueprinter** (nikkorap) — full aircraft prefab loading via assetbundles
- **NuclearMenuBackground** (dikkadev) — `MainMenuBackground309` sprite swap pattern, MIT
- **NOVR/VrUi/UIBehaviorPatcher.cs:26-33** — existing scene-name patch map to extend
- **NOVR/VrUi/Components/NOVRBlackoutCanvasBehavior.cs** — example scene-component pattern
- **NOVR/VrUi/Native/NativeMainMenuLogo.cs:13-57** — example of loading a texture from the mod folder
- **NOVR/NOVRBehaviour.cs:14-25** — `Create<T>(parent)` helper for spawning GameObjects
- **NOVR/NOVRHeadsetData.cs:36-45** — `CalibrateTranslation()` for recentering
- **NOVR/VrUi/HarmonyPatches/FloatingOriginUiRootPatch.cs** + `PositionZeroBehavior.cs` — pattern for keeping world-anchored objects stable during floating-origin shifts