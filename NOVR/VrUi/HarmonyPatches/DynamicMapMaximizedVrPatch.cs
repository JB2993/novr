using HarmonyLib;
using NOVR.VrUi.SpecialBehavior;

namespace NOVR.VrUi.HarmonyPatches;

// The minimized cockpit map lives inside the GameplayUI canvas hierarchy, which is already
// converted to a world-space VrUi-layer canvas, so it renders fine in the headset. Maximizing
// the map (DynamicMap.Maximize) instead reparents it onto DynamicMap.maximizedMapCanvas, a
// separate Canvas the base game ships as Screen Space - Overlay for desktop play. Overlay
// canvases draw straight to the display surface and never pass through the VR camera stack,
// so the maximized map shows up on the desktop monitor mirror but is invisible in the headset.
// Attaching NOVRGameplayUIBehaviour gives maximizedMapCanvas the same world-space/VrUi-layer
// treatment as the rest of the cockpit UI, anchored in front of the cockpit HUD.
internal static class DynamicMapMaximizedVrPatch
{
    [HarmonyPatch(typeof(global::DynamicMap), "Awake")]
    private static class AwakePatch
    {
        [HarmonyPostfix]
        private static void Postfix(global::DynamicMap __instance)
        {
            var canvas = __instance.maximizedMapCanvas;
            if (canvas == null) return;

            if (!canvas.gameObject.TryGetComponent<NOVRGameplayUIBehaviour>(out _))
            {
                canvas.gameObject.AddComponent<NOVRGameplayUIBehaviour>();
            }
        }
    }
}
