using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

/// <summary>
/// Binds the tactical map's canvas to the VR cockpit HUD camera.
///
/// This is the only place in NOVR that assigns a world camera to the map canvas, so if this
/// component fails to attach or binds too early the map renders to the desktop camera only and
/// is invisible in the headset. Binding is therefore deferred and retried until NOUIManager has
/// produced a cockpit HUD camera, rather than being attempted once in Start().
/// </summary>
public class NOVRDynamicMapBehavior : MonoBehaviour
{
    private Canvas? _canvas;
    private bool _bound;

    private void Start() => TryBind();

    private void Update()
    {
        if (!_bound) TryBind();
    }

    private void TryBind()
    {
        // APIBus.CockpitHudCamera dereferences NOUIManager.I, which is null until the VR UI
        // manager awakes. Check the singleton first or this throws instead of returning null.
        if (NOUIManager.I == null) return;

        var camera = APIBus.CockpitHudCamera;
        if (camera == null) return;

        _canvas = GetComponentInParent<Canvas>();
        if (_canvas == null) return;

        var chain = string.Join(" < ", GetComponentsInParent<Canvas>(true).Select(c => c.name));
        var vrUiLayer = LayerHelper.GetVrUiLayer();
        NOVRLog.Info(
            $"NOVRDynamicMapBehavior: binding canvas '{_canvas.name}' (mode={_canvas.renderMode}, " +
            $"layerBefore={gameObject.layer}, vrUiLayer={(int)vrUiLayer}) " +
            $"canvasChain=[{chain}] camera='{camera.name}'");

        // The cockpit HUD camera culls to the VR UI layer only. When the map lived under the
        // FlightHud tree it inherited that layer from NOVRFlightHudBehavior; if the map screen
        // has since been re-parented it needs the layer applied here or the camera won't draw it.
        LayerHelper.SetLayerRecursive(transform, vrUiLayer);

        _canvas.worldCamera = camera;
        VrCanvasHitTester.Register(_canvas);

        ApplyRaycastTargets();

        _bound = true;
        NOVRLog.Info($"NOVRDynamicMapBehavior: bound '{_canvas.name}' -> '{camera.name}'");
    }

    private void ApplyRaycastTargets()
    {
        // The game uses coordinate-math for map interaction instead of EventSystem,
        // so map graphics have raycastTarget=false by design. The VR cursor's
        // HasGraphicAtPoint check needs raycastTarget to find the map surface.
        var map = GetComponent<global::DynamicMap>();
        if (map == null) return;

        if (map.mapBackground != null)
        {
            var bgImg = map.mapBackground.GetComponent<Image>();
            if (bgImg != null) bgImg.raycastTarget = true;
        }

        if (map.mapImage != null)
        {
            var mapImg = map.mapImage.GetComponent<Image>();
            if (mapImg != null) mapImg.raycastTarget = true;
        }
    }

    private void OnDestroy()
    {
        if (_canvas != null)
            VrCanvasHitTester.Unregister(_canvas);
    }
}
