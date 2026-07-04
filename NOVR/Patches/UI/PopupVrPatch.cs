using System;
using HarmonyLib;
using NOVR.PatchHelper;
using NOVR.VrUi;
using NOVR.VrUi.SpecialBehavior;
using NuclearOption.Networking.Lobbies;
using UnityEngine;

namespace NOVR.Patches.UI;

/// <summary>
/// Makes in-game popups usable in VR.
///
/// Tutorial dialogue boxes (MissionMessages -> GameplayUI.DialogueBox) live under a
/// screen-space-overlay canvas the mod never converts, so they are invisible in the
/// headset and out of reach of the VR cursor. They get adopted onto a world-space
/// popup canvas when shown.
///
/// Multiplayer "client error" messages (FlashErrorMessageSingleton) come with their own
/// DontDestroyOnLoad overlay canvas, which is converted in place to world space.
/// </summary>
internal static class PopupVrPatch
{
    private static readonly AccessTools.FieldRef<FlashErrorMessageSingleton, Canvas>? FlashErrorCanvasRef =
        CreateFlashErrorCanvasRef();

    private static AccessTools.FieldRef<FlashErrorMessageSingleton, Canvas>? CreateFlashErrorCanvasRef()
    {
        try
        {
            return AccessTools.FieldRefAccess<FlashErrorMessageSingleton, Canvas>("_canvas");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"PopupVrPatch: could not bind FlashErrorMessageSingleton._canvas: {ex.Message}");
            return null;
        }
    }

    // EnableBox(true) is the single funnel every DialogueBox.Show path goes through.
    [PatchPostfix(typeof(DialogueBox), "EnableBox")]
    private static void DialogueBox_EnableBox_Postfix(DialogueBox __instance, bool __0)
    {
        if (!__0 || __instance == null) return;

        try
        {
            NOVRPopupCanvasHost.Adopt(__instance);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"PopupVrPatch: failed to adopt DialogueBox into VR popup canvas: {ex}");
        }
    }

    [PatchPostfix(typeof(FlashErrorMessageSingleton), "ShowErrorInternal")]
    private static void FlashError_ShowErrorInternal_Postfix(FlashErrorMessageSingleton __instance)
    {
        if (__instance == null || FlashErrorCanvasRef == null) return;

        try
        {
            var canvas = FlashErrorCanvasRef(__instance);
            if (canvas == null) return;

            if (!canvas.TryGetComponent<NOVRPopupCanvasBehavior>(out var behavior))
            {
                behavior = canvas.gameObject.AddComponent<NOVRPopupCanvasBehavior>();
            }

            behavior.EnsureConfigured();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"PopupVrPatch: failed to convert flash error canvas for VR: {ex}");
        }
    }
}
