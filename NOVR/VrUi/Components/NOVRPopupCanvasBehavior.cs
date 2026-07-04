using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

/// <summary>
/// Converts a popup canvas that the base game renders in screen space (tutorial dialogue
/// boxes, multiplayer client-error messages) into a world-space canvas on the VrUi layer,
/// so the CockpitHudCamera renders it in the headset and the VR cursor can click it.
/// </summary>
public class NOVRPopupCanvasBehavior : UIRenderedCanvasBehavior
{
    // Same scale as the gameplay/menu canvases, but slightly closer than their 3m plane
    // so popups draw in front of them instead of z-fighting on the same plane.
    private const float CanvasScale = 0.003f;
    private static readonly Vector3 CanvasPosition = new(0f, 0f, 2.8f);
    private const int PopupSortingOrder = 100;

    public void EnsureConfigured()
    {
        var canvas = GetComponent<Canvas>();
        if (canvas != null)
        {
            canvas.renderMode = RenderMode.WorldSpace;
            if (canvas.worldCamera == null)
            {
                canvas.worldCamera = NOUIManager.I != null ? NOUIManager.I.CockpitHudCamera : null;
            }
            canvas.sortingOrder = PopupSortingOrder;

            if (GetComponent<GraphicRaycaster>() == null)
            {
                gameObject.AddComponent<GraphicRaycaster>();
            }
        }

        LayerHelper.SetLayerRecursive(transform, LayerHelper.GetVrUiLayer());
    }

    // Popup contents are pooled/instantiated at show time (e.g. FlashErrorMessageModal),
    // so new children need the VrUi layer applied as they appear.
    private void OnTransformChildrenChanged()
    {
        LayerHelper.SetLayerRecursive(transform, LayerHelper.GetVrUiLayer());
    }

    private void Update()
    {
        transform.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        transform.position = CanvasPosition;

        var canvas = GetComponent<Canvas>();
        if (canvas != null && canvas.worldCamera == null && NOUIManager.I != null)
        {
            canvas.worldCamera = NOUIManager.I.CockpitHudCamera;
        }
    }
}
