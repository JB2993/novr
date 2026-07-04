using NOVR.VrUi.SpecialBehavior;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi;

/// <summary>
/// Owns a lazily created world-space canvas that adopts popup UI which the base game
/// parents under screen-space-overlay canvases the mod never converts (e.g. the tutorial
/// DialogueBox under GameplayUI's gameplay canvas). Overlay canvases are not rendered to
/// the HMD at all, so anything left under them is invisible and unclickable in VR.
/// The host canvas lives in the active scene and is recreated on demand after scene loads.
/// </summary>
public static class NOVRPopupCanvasHost
{
    private const float ReferenceWidth = 1920f;
    private const float ReferenceHeight = 1080f;

    private static Canvas? _canvas;

    public static Canvas GetOrCreateCanvas()
    {
        if (_canvas != null) return _canvas;

        var go = new GameObject("NOVRPopupCanvas");
        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;

        var rect = go.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(ReferenceWidth, ReferenceHeight);

        go.AddComponent<GraphicRaycaster>();
        go.AddComponent<NOVRPopupCanvasBehavior>().EnsureConfigured();

        return _canvas;
    }

    /// <summary>
    /// Reparents the popup onto the host canvas, keeping its layout values so it stays
    /// where the game's UI design put it relative to a full-screen canvas.
    /// </summary>
    public static void Adopt(Component popup)
    {
        if (popup == null) return;

        var canvas = GetOrCreateCanvas();
        if (popup.transform.parent != canvas.transform)
        {
            popup.transform.SetParent(canvas.transform, false);
            Debug.Log($"NOVRPopupCanvasHost: adopted '{popup.gameObject.name}' onto the VR popup canvas");
        }

        LayerHelper.SetLayerRecursive(canvas.transform, LayerHelper.GetVrUiLayer());
    }
}
